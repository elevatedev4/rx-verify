using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace RxVerifyOverlay.OrderAssist.Ocr;

/// <summary>
/// Mutates a captured Bitmap IN PLACE so colored table rows read exactly
/// like plain white ones to Windows.Media.Ocr -- BEFORE OCR ever sees them.
/// Called from OrderAssistCoordinator.TickAsync right after
/// EscriptImageCapture.CaptureRegion and before _ocrEngine.RecognizeAsync,
/// for BOTH target window kinds.
///
/// HISTORY: rounds 3-5 binarized only scanlines that
/// RowHighlightColorDetector.IsHighlightScanline flagged as a "highlight"
/// (chroma / luminance / fill-fraction thresholds, all unmeasured
/// estimates). Round 6 (74cd116) removed the gate and hard-binarized every
/// scanline against its own median color; it broke Order Assist in the
/// field and was reverted (0503325). See LocalContrastNormalizer's class
/// doc for why neither shape was right and what replaces them: a
/// color-blind, local-background contrast rewrite that leaves plain white
/// rows byte-for-byte untouched. RowHighlightColorDetector is no longer
/// called from here (kept, with its tests, as a documented measurement
/// helper).
///
/// This class is only the System.Drawing plumbing (LockBits + Marshal.Copy
/// in, LocalContrastNormalizer.Normalize on the raw bytes, copy back only
/// if something changed). Requires Format32bppArgb -- true of every bitmap
/// EscriptImageCapture.CaptureRegion produces. NOT unit tested directly
/// (System.Drawing.Common is blocked at runtime on non-Windows); every
/// pixel decision lives in LocalContrastNormalizer and is tested there.
/// </summary>
public static class RowHighlightNormalizer
{
    /// <summary>Kept for OrderAssistCoordinator's diagnostic log signature: a Y band whose rows had at least one pixel rewritten is reported in <see cref="NormalizeResult.AcceptedBands"/>; the local-contrast rewrite has no "near miss" concept, so RejectedCandidates is always empty.</summary>
    public readonly record struct RejectedHighlightCandidate(int Top, int Bottom, int Chroma, double Luminance, double Fraction);

    /// <summary>Result of one NormalizeInPlace call -- rewritten row bands (inclusive top, exclusive bottom), plus an always-empty near-miss list kept for log-format compatibility.</summary>
    public readonly record struct NormalizeResult(
        IReadOnlyList<(int Top, int Bottom)> AcceptedBands,
        IReadOnlyList<RejectedHighlightCandidate> RejectedCandidates);

    private static readonly NormalizeResult EmptyResult = new(Array.Empty<(int, int)>(), Array.Empty<RejectedHighlightCandidate>());

    /// <summary>
    /// Applies LocalContrastNormalizer to <paramref name="bitmap"/> in place.
    /// Never throws outward: any failure degrades to "did nothing this tick"
    /// (empty result, bitmap left as captured) rather than blocking the
    /// capture the caller already has in hand.
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
        try
        {
            var stride = data.Stride;
            if (stride <= 0) return EmptyResult; // bottom-up DIB: not produced by CaptureRegion; do nothing

            var buffer = new byte[stride * height];
            Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);

            var result = LocalContrastNormalizer.Normalize(buffer, width, height, stride);
            if (result.ChangedPixels == 0) return EmptyResult;

            Marshal.Copy(buffer, 0, data.Scan0, buffer.Length);
            return new NormalizeResult(result.ChangedBands, Array.Empty<RejectedHighlightCandidate>());
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
