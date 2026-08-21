using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace RxVerifyOverlay.OrderAssist.Ocr;

/// <summary>
/// ROUND 3 (replaces round 2's SelectionRowNormalizer). Mutates a captured
/// Bitmap IN PLACE, BINARIZING every horizontal scanline
/// RowHighlightColorDetector.IsHighlightScanline flags as a genuine
/// highlight/selection/flag fill -- background-colored pixels become pure
/// white, every other pixel on that scanline (the text) becomes pure black
/// -- BEFORE Windows.Media.Ocr ever sees the row. See
/// RowHighlightColorDetector's own doc for why binarizing (rather than
/// round 2's plain 255-minus invert) is the fix: it produces normal
/// dark-text-on-white-background contrast regardless of the highlight's
/// ORIGINAL polarity, so the exact same pass now handles white-on-dark
/// selection blue AND dark-on-bright highlight yellow without needing to
/// know which polarity a given highlight color uses.
///
/// Called from OrderAssistCoordinator.TickAsync right after
/// EscriptImageCapture.CaptureRegion and before _ocrEngine.RecognizeAsync,
/// for BOTH target window kinds (harmless no-op when no highlighted row is
/// present that tick -- the per-scanline check simply never crosses the
/// fraction/chroma thresholds on an ordinary row).
///
/// Requires the bitmap to already be Format32bppArgb -- true of every
/// bitmap EscriptImageCapture.CaptureRegion produces. Uses LockBits +
/// Marshal.Copy rather than GetPixel/SetPixel (a managed round trip per
/// call) for the same avoidable-capture-latency reason round 2's version
/// did.
///
/// NOT unit tested directly (needs a real Bitmap/System.Drawing pixel
/// buffer, which System.Drawing.Common hard-blocks at runtime on macOS --
/// confirmed in round 2's own report) -- same "pure logic tested, OS-level
/// bitmap plumbing isn't" split as round 2's version; the actual color/
/// scanline DECISION this delegates to (RowHighlightColorDetector) is what
///'s tested -- see RxVerifyOverlay.Tests/OrderAssist/RowHighlightColorDetectorTests.cs.
/// </summary>
public static class RowHighlightNormalizer
{
    /// <summary>
    /// ROUND 5 diagnostic-only record for a scanline band that was NOT
    /// accepted as a highlight but whose dominant color still cleared
    /// RowHighlightColorDetector.MinChromaForDiagnosticCandidate -- i.e. a
    /// "near miss" worth reporting, not an ordinary plain-white/black-text
    /// row. Carries the measured chroma/luminance/fill-fraction of the
    /// band's OPENING scanline (a representative sample, not an average --
    /// kept simple on purpose) so Will's next report on a still-unread
    /// colored row pinpoints the exact values RowHighlightColorDetector saw,
    /// instead of another guess.
    /// </summary>
    public readonly record struct RejectedHighlightCandidate(int Top, int Bottom, int Chroma, double Luminance, double Fraction);

    /// <summary>Result of one NormalizeInPlace call -- the accepted (binarized) bands plus, for diagnostics only, every rejected near-miss band. See RejectedHighlightCandidate's own doc.</summary>
    public readonly record struct NormalizeResult(
        IReadOnlyList<(int Top, int Bottom)> AcceptedBands,
        IReadOnlyList<RejectedHighlightCandidate> RejectedCandidates);

    private static readonly NormalizeResult EmptyResult = new(Array.Empty<(int, int)>(), Array.Empty<RejectedHighlightCandidate>());

    /// <summary>
    /// Binarizes every highlighted scanline of <paramref name="bitmap"/> in
    /// place. Returns the Y-ranges (inclusive top, exclusive bottom, in the
    /// bitmap's own pixel coordinates) of every band actually touched, PLUS
    /// (ROUND 5) every rejected near-miss band -- purely for
    /// OrderAssistCoordinator's own local diagnostic logging (same posture
    /// as round 2: since the exact chroma/luminance bounds are still an
    /// estimate, this is what proves or disproves the heuristic firing at
    /// all on Will's next real capture, without needing another
    /// live-diagnosis round). Never throws outward: any failure degrades to
    /// "did nothing this tick" (returns an empty result, bitmap left
    /// unmodified) rather than blocking the capture the caller already has
    /// in hand.
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
        var bands = new List<(int Top, int Bottom)>();
        var candidates = new List<RejectedHighlightCandidate>();

        try
        {
            var stride = data.Stride;
            var buffer = new byte[stride * height];
            Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);

            var pixelsInRow = new (byte R, byte G, byte B)[width];
            var bandOpenAt = -1;
            var candidateOpenAt = -1;
            var candidateChroma = 0;
            var candidateLuminance = 0.0;
            var candidateFraction = 0.0;

            for (var y = 0; y < height; y++)
            {
                var rowOffset = y * stride;
                for (var x = 0; x < width; x++)
                {
                    // Format32bppArgb byte order in memory is B, G, R, A.
                    var p = rowOffset + x * 4;
                    pixelsInRow[x] = (buffer[p + 2], buffer[p + 1], buffer[p]);
                }

                if (RowHighlightColorDetector.IsHighlightScanline(pixelsInRow))
                {
                    var dominant = RowHighlightColorDetector.EstimateDominantColor(pixelsInRow);

                    for (var x = 0; x < width; x++)
                    {
                        var p = rowOffset + x * 4;
                        var (r, g, b) = pixelsInRow[x];

                        // Background-colored -> pure white; everything else
                        // (the text) -> pure black. Normal contrast
                        // regardless of the highlight's original polarity
                        // -- see class doc.
                        byte outR, outG, outB;
                        if (RowHighlightColorDetector.IsCloseToColor(r, g, b, dominant))
                        {
                            outR = outG = outB = 255;
                        }
                        else
                        {
                            outR = outG = outB = 0;
                        }

                        buffer[p] = outB;
                        buffer[p + 1] = outG;
                        buffer[p + 2] = outR;
                        // alpha (buffer[p + 3]) left untouched
                    }

                    if (bandOpenAt < 0) bandOpenAt = y;

                    // An accepted scanline can't also be a rejected
                    // candidate -- close out any candidate band that was
                    // open going into this row.
                    if (candidateOpenAt >= 0)
                    {
                        candidates.Add(new RejectedHighlightCandidate(candidateOpenAt, y, candidateChroma, candidateLuminance, candidateFraction));
                        candidateOpenAt = -1;
                    }
                }
                else
                {
                    if (bandOpenAt >= 0)
                    {
                        bands.Add((bandOpenAt, y));
                        bandOpenAt = -1;
                    }

                    // ROUND 5 diagnostic: not accepted, but is it a near
                    // miss worth logging (some real tint), or just an
                    // ordinary plain-background row (chroma ~0)?
                    var dominant = RowHighlightColorDetector.EstimateDominantColor(pixelsInRow);
                    var (chroma, luminance) = RowHighlightColorDetector.MeasureColor(dominant.R, dominant.G, dominant.B);

                    if (chroma >= RowHighlightColorDetector.MinChromaForDiagnosticCandidate)
                    {
                        if (candidateOpenAt < 0)
                        {
                            candidateOpenAt = y;
                            candidateChroma = chroma;
                            candidateLuminance = luminance;
                            var matchCount = 0;
                            foreach (var px in pixelsInRow)
                            {
                                if (RowHighlightColorDetector.IsCloseToColor(px.R, px.G, px.B, dominant)) matchCount++;
                            }
                            candidateFraction = pixelsInRow.Length == 0 ? 0.0 : (double)matchCount / pixelsInRow.Length;
                        }
                    }
                    else if (candidateOpenAt >= 0)
                    {
                        candidates.Add(new RejectedHighlightCandidate(candidateOpenAt, y, candidateChroma, candidateLuminance, candidateFraction));
                        candidateOpenAt = -1;
                    }
                }
            }

            if (bandOpenAt >= 0) bands.Add((bandOpenAt, height));
            if (candidateOpenAt >= 0) candidates.Add(new RejectedHighlightCandidate(candidateOpenAt, height, candidateChroma, candidateLuminance, candidateFraction));

            Marshal.Copy(buffer, 0, data.Scan0, buffer.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return new NormalizeResult(bands, candidates);
    }
}
