using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace RxVerifyOverlay.OrderAssist.Ocr;

/// <summary>
/// ROUND 2 (W-T85 bug 2) — mutates a captured Bitmap IN PLACE, inverting
/// the RGB of every horizontal scanline SelectionRowColorDetector.
/// IsSelectionScanline classifies as a Pioneer row-selection fill, so
/// white-on-dark-blue text becomes dark-on-light BEFORE Windows.Media.Ocr
/// ever sees it — normal contrast polarity, matching every other row on
/// the screen. Called from OrderAssistCoordinator.TickAsync right after
/// EscriptImageCapture.CaptureRegion and before _ocrEngine.RecognizeAsync,
/// for BOTH target window kinds (harmless no-op when no selection band is
/// present — the per-scanline check simply never crosses the fraction
/// threshold).
///
/// Requires the bitmap to already be Format32bppArgb — true of every
/// bitmap EscriptImageCapture.CaptureRegion produces (see that method).
/// Uses LockBits + Marshal.Copy rather than GetPixel/SetPixel (a managed
/// round trip per call) — for a full captured-window-sized region
/// (thousands of pixels tall x wide) that per-pixel-call overhead would be
/// a measurable, avoidable capture-latency cost on every ~1s tick.
///
/// NOT unit tested directly (needs a real Bitmap/System.Drawing pixel
/// buffer) — same "pure logic tested, OS-level bitmap plumbing isn't"
/// split as Ocr/WindowsMediaOcrEngine.cs's own Upscale/ConvertToSoftwareBitmapAsync;
/// the actual color/scanline DECISION this delegates to
/// (SelectionRowColorDetector) is what's tested — see
/// RxVerifyOverlay.Tests/OrderAssist/SelectionRowColorDetectorTests.cs.
/// </summary>
public static class SelectionRowNormalizer
{
    /// <summary>
    /// Inverts every selection-colored scanline of <paramref name="bitmap"/>
    /// in place. Returns the Y-ranges (inclusive top, exclusive bottom, in
    /// the bitmap's own pixel coordinates) of every band actually inverted
    /// — purely for OrderAssistCoordinator's own local diagnostic logging
    /// (ROUND 2, W-T85 bug 2: since this heuristic's exact color bounds are
    /// an unverified estimate — see SelectionRowColorDetector's own doc —
    /// this is what proves or disproves it firing at all on Will's next
    /// real capture, without needing another live-diagnosis round). Never
    /// throws outward: any failure degrades to "did nothing this tick"
    /// (returns an empty list, bitmap left unmodified) rather than
    /// blocking the capture the caller already has in hand.
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

                if (SelectionRowColorDetector.IsSelectionScanline(pixelsInRow))
                {
                    for (var x = 0; x < width; x++)
                    {
                        var p = rowOffset + x * 4;
                        buffer[p] = (byte)(255 - buffer[p]);         // B
                        buffer[p + 1] = (byte)(255 - buffer[p + 1]); // G
                        buffer[p + 2] = (byte)(255 - buffer[p + 2]); // R
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
