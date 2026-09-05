using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace RxVerifyOverlay.OrderAssist.Ocr;

/// <summary>
/// ROUND 3 (replaces round 2's SelectionRowNormalizer). ROUND 6 (Will,
/// verbatim: "The blue and yellow lines need to be read too, just as if
/// they are white regular lines. Currently they are being skipped."):
/// rounds 3-5 all binarized a scanline ONLY IF RowHighlightColorDetector's
/// chroma/luminance/fill-fraction gate first decided it looked like a
/// highlight — and three rounds of retuning that gate's thresholds never
/// stopped real blue/yellow rows from being skipped, because every one of
/// those thresholds was an estimate never measured against a real Pioneer
/// screen (see RowHighlightColorDetector's own class doc for the full
/// round-3/4/5 history). Round 6 deletes the gate: this class now
/// binarizes UNCONDITIONALLY, mutating a captured Bitmap IN PLACE, EVERY
/// horizontal scanline in the capture -- background-colored pixels (per
/// that scanline's OWN dominant color) become pure white, every other
/// pixel on that scanline (the text) becomes pure black -- BEFORE
/// Windows.Media.Ocr ever sees the row. There is no longer a decision
/// about WHICH rows get this treatment; every row gets it.
///
/// WHY THIS IS SAFE FOR ORDINARY (plain white) ROWS: an ordinary row's own
/// dominant color IS its plain white/near-white background (text is a
/// small minority of the row's pixels by construction — see
/// RowHighlightColorDetector.EstimateDominantColor's own doc), so
/// binarizing it maps that background to pure white and the dark text to
/// pure black — the same dark-text-on-white contrast Windows.Media.Ocr
/// already reads well today. The one real difference from the pre-round-6
/// "left untouched" path is that anti-aliased gray edge pixels around each
/// glyph now snap fully to black or white instead of staying a blended
/// gray — a deliberate trade documented in RowHighlightColorDetector.
/// BackgroundColorTolerance's own doc, not expected to hurt OCR (Windows.
/// Media.Ocr is a print/screen-text engine already tolerant of hard
/// binary edges; this is the exact same win-condition round 3's binarized
/// highlight rows already relied on).
///
/// FAILURE MODES CONSIDERED (round 6 — since every scanline is now
/// binarized unconditionally, these all had to be worked through, not just
/// the ones a highlight-only gate used to shield ordinary rows from):
///   - GRIDLINE/BORDER SCANLINE: a thin 1px separator row is typically one
///     uniform color across its whole width, so its own dominant color IS
///     that color, and the whole scanline maps to pure white (no text
///     pixels differ from "dominant" to become black). Harmless — nothing
///     is lost because there was no text there to begin with.
///   - GRADIENT CHROME (e.g. a soft-shaded toolbar/header strip): the
///     dominant color is recomputed PER SCANLINE (a fresh median for each
///     row of pixels, never reused across rows — see the loop below), so
///     a vertical gradient's per-row color still resolves correctly row by
///     row even though no single color describes the whole strip.
///   - A SCANLINE WHOSE DOMINANT COLOR IS ACTUALLY THE TEXT COLOR (dense,
///     tightly-packed text with little background showing): see
///     RowHighlightColorDetector.EstimateDominantColor's own round-6 doc.
///     The output is a polarity-flipped but still perfectly clean binary
///     image (background -&gt; black, text -&gt; white) rather than the
///     row being skipped outright — Windows.Media.Ocr is not guaranteed to
///     read white-on-black as reliably as black-on-white, but a
///     harder-to-read row is a strictly better outcome than round 3-5's
///     failure mode of a row silently never being touched at all because a
///     guessed threshold rejected it. This is flagged, not solved further,
///     because it cannot be distinguished from "genuine highlight fill
///     with light text" without color knowledge this class deliberately no
///     longer tries to have an opinion about (see class doc above).
///   - ANTI-ALIASED EDGE PIXELS: see BackgroundColorTolerance's own doc —
///     a 40-per-channel tolerance pulls a blended edge pixel to whichever
///     side (background or text) it's closer to.
///
/// Called from OrderAssistCoordinator.TickAsync right after
/// EscriptImageCapture.CaptureRegion and before _ocrEngine.RecognizeAsync,
/// for BOTH target window kinds.
///
/// Requires the bitmap to already be Format32bppArgb -- true of every
/// bitmap EscriptImageCapture.CaptureRegion produces. Uses LockBits +
/// Marshal.Copy rather than GetPixel/SetPixel (a managed round trip per
/// call) for the same avoidable-capture-latency reason round 2's version
/// did — see ROUND 6 SPEED doc below for the rest of this tick's latency
/// story.
///
/// ROUND 6 SPEED (Will, verbatim: "We also need the OCR to speed up as
/// much as possible. There is quite a delay right now."): the per-pixel
/// loop below is now guaranteed to run its full width/height on every
/// tick (no early accept/reject skip), so it was re-checked for hot-loop
/// cost: direct byte-array indexing throughout (no GetPixel/SetPixel, no
/// System.Drawing.Color struct anywhere in the loop, no per-pixel heap
/// allocation), the same shape round 3 already used for its (smaller,
/// gated) binarized region. Two sub-passes per scanline are unavoidable —
/// one to gather that row's pixels so EstimateDominantColor's median can
/// see the whole row, one to write the binarized output — but neither
/// pass allocates per pixel, and there is no THIRD pass: the diagnostic
/// "was this scanline colored" classification (see class doc's own
/// NormalizeResult) reuses the SAME dominant-color value the binarization
/// pass already computed, rather than recomputing it.
///
/// NOT unit tested directly (needs a real Bitmap/System.Drawing pixel
/// buffer, which System.Drawing.Common hard-blocks at runtime on macOS --
/// confirmed in round 2's own report) -- same "pure logic tested, OS-level
/// bitmap plumbing isn't" split as round 2's version; the actual color/
/// pixel-classification math this delegates to (RowHighlightColorDetector)
/// is what's tested -- see
/// RxVerifyOverlay.Tests/OrderAssist/RowHighlightColorDetectorTests.cs.
/// </summary>
public static class RowHighlightNormalizer
{
    /// <summary>
    /// ROUND 6 diagnostic record for one contiguous run of scanlines whose
    /// own dominant color cleared RowHighlightColorDetector.
    /// DiagnosticColoredRowChromaFloor -- i.e. a genuinely colored
    /// (non-near-white) row band, like a blue selection row or a yellow
    /// flagged row. PURELY informational: every scanline in (and out of)
    /// a colored band was ALREADY binarized identically by the
    /// unconditional pass below -- this record changes nothing about that
    /// output. It exists so OrderAssistCoordinator's field-report logging
    /// can show Will's next capture actually saw N colored scanlines this
    /// tick, proving the fix ran, instead of asking him to trust another
    /// guess. Carries the measured chroma/luminance of the band's OPENING
    /// scanline (a representative sample, not an average -- kept simple on
    /// purpose), same posture as round 5's RejectedHighlightCandidate this
    /// replaces.
    /// </summary>
    public readonly record struct ColoredScanlineBand(int Top, int Bottom, int Chroma, double Luminance);

    /// <summary>
    /// Result of one NormalizeInPlace call. ScanlineCount is the bitmap's
    /// own height (every one of which was binarized -- round 6 has no
    /// "skipped" scanlines any more). ColoredScanlineCount/ColoredBands are
    /// PURELY diagnostic (see ColoredScanlineBand's own doc) -- they report
    /// what the capture LOOKED like, not what got processed differently,
    /// since round 6 processes every scanline the same way regardless.
    /// </summary>
    public readonly record struct NormalizeResult(
        int ScanlineCount,
        int ColoredScanlineCount,
        IReadOnlyList<ColoredScanlineBand> ColoredBands);

    private static readonly NormalizeResult EmptyResult = new(0, 0, Array.Empty<ColoredScanlineBand>());

    /// <summary>
    /// Binarizes EVERY horizontal scanline of <paramref name="bitmap"/> in
    /// place, unconditionally (round 6 -- see class doc for why the old
    /// per-scanline accept/reject gate was removed rather than retuned
    /// again). Returns scanline/colored-band counts for
    /// OrderAssistCoordinator's own local diagnostic logging only -- see
    /// NormalizeResult's own doc. Never throws outward: any failure
    /// degrades to "did nothing this tick" (returns an empty result,
    /// bitmap left unmodified) rather than blocking the capture the caller
    /// already has in hand.
    /// </summary>
    public static NormalizeResult NormalizeInPlace(Bitmap bitmap)
    {
        try
        {
            return NormalizeInPlaceCore(bitmap);
        }
        catch
        {
            return EmptyResult;
        }
    }

    private static NormalizeResult NormalizeInPlaceCore(Bitmap bitmap)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        if (width <= 0 || height <= 0) return EmptyResult;

        var rect = new Rectangle(0, 0, width, height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        var coloredBands = new List<ColoredScanlineBand>();
        var coloredScanlineCount = 0;

        try
        {
            var stride = data.Stride;
            var buffer = new byte[stride * height];
            Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);

            var pixelsInRow = new (byte R, byte G, byte B)[width];
            var bandOpenAt = -1;
            var bandChroma = 0;
            var bandLuminance = 0.0;

            for (var y = 0; y < height; y++)
            {
                var rowOffset = y * stride;

                // Pass 1/2: gather this row's pixels -- direct byte-array
                // indexing, no Color struct, no per-pixel allocation.
                // Format32bppArgb byte order in memory is B, G, R, A.
                for (var x = 0; x < width; x++)
                {
                    var p = rowOffset + x * 4;
                    pixelsInRow[x] = (buffer[p + 2], buffer[p + 1], buffer[p]);
                }

                var dominant = RowHighlightColorDetector.EstimateDominantColor(pixelsInRow);

                // Pass 2/2: UNCONDITIONAL binarization (round 6 -- no gate
                // decides whether this row gets touched; every row does).
                // Background-colored (close to THIS row's own dominant
                // color) -> pure white; everything else (the text) -> pure
                // black. Normal contrast regardless of the row's original
                // polarity -- see class doc.
                for (var x = 0; x < width; x++)
                {
                    var p = rowOffset + x * 4;
                    var (r, g, b) = pixelsInRow[x];

                    byte outVal = RowHighlightColorDetector.IsCloseToColor(r, g, b, dominant) ? (byte)255 : (byte)0;

                    buffer[p] = outVal;
                    buffer[p + 1] = outVal;
                    buffer[p + 2] = outVal;
                    // alpha (buffer[p + 3]) left untouched
                }

                // DIAGNOSTIC ONLY (round 6) -- reuses the SAME `dominant`
                // value just computed above rather than a third pass/
                // recomputation. Classifies this scanline as a "colored
                // row" purely for OrderAssistCoordinator's field-report
                // logging; has zero effect on the binarization already
                // written above.
                var (chroma, luminance) = RowHighlightColorDetector.MeasureColor(dominant.R, dominant.G, dominant.B);
                if (RowHighlightColorDetector.IsNotablyColored(chroma))
                {
                    coloredScanlineCount++;
                    if (bandOpenAt < 0)
                    {
                        bandOpenAt = y;
                        bandChroma = chroma;
                        bandLuminance = luminance;
                    }
                }
                else if (bandOpenAt >= 0)
                {
                    coloredBands.Add(new ColoredScanlineBand(bandOpenAt, y, bandChroma, bandLuminance));
                    bandOpenAt = -1;
                }
            }

            if (bandOpenAt >= 0) coloredBands.Add(new ColoredScanlineBand(bandOpenAt, height, bandChroma, bandLuminance));

            Marshal.Copy(buffer, 0, data.Scan0, buffer.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return new NormalizeResult(height, coloredScanlineCount, coloredBands);
    }
}
