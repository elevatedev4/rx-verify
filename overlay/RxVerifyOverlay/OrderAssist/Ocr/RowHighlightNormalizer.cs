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
    /// Binarizes every highlighted scanline of <paramref name="bitmap"/> in
    /// place. Returns the Y-ranges (inclusive top, exclusive bottom, in the
    /// bitmap's own pixel coordinates) of every band actually touched --
    /// purely for OrderAssistCoordinator's own local diagnostic logging
    /// (same posture as round 2: since the exact chroma/luminance bounds
    /// are still an estimate, this is what proves or disproves the
    /// heuristic firing at all on Will's next real capture, without
    /// needing another live-diagnosis round). Never throws outward: any
    /// failure degrades to "did nothing this tick" (returns an empty list,
    /// bitmap left unmodified) rather than blocking the capture the caller
    /// already has in hand.
    /// </summary>
    public static IReadOnlyList<(int Top, int Bottom)> NormalizeInPlace(Bitmap bitmap)
    {
        try
        {
            return NormalizeInPlaceCore(bitmap);
        }
        catch
        {
            return Array.Empty<(int, int)>();
        }
    }

    private static IReadOnlyList<(int Top, int Bottom)> NormalizeInPlaceCore(Bitmap bitmap)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        if (width <= 0 || height <= 0) return Array.Empty<(int, int)>();

        var rect = new Rectangle(0, 0, width, height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        var bands = new List<(int Top, int Bottom)>();

        try
        {
            var stride = data.Stride;
            var buffer = new byte[stride * height];
            Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);

            var pixelsInRow = new (byte R, byte G, byte B)[width];
            var bandOpenAt = -1;

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
                }
                else if (bandOpenAt >= 0)
                {
                    bands.Add((bandOpenAt, y));
                    bandOpenAt = -1;
                }
            }

            if (bandOpenAt >= 0) bands.Add((bandOpenAt, height));

            Marshal.Copy(buffer, 0, data.Scan0, buffer.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bands;
    }
}
