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
/// called from the default path; it only runs via
/// LegacyRowHighlightNormalizer, the runtime fallback selected by
/// settings.json "OrderAssistRowNormalizer": "legacy" (see
/// RowNormalizerModes). The default path also has a safety valve: if the
/// rewrite would change over 60% of the capture it is discarded for that
/// tick and the legacy normalizer runs on that tick's bitmap instead.
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
        IReadOnlyList<RejectedHighlightCandidate> RejectedCandidates,
        bool SafetyValveTripped = false);

    private static readonly NormalizeResult EmptyResult = new(Array.Empty<(int, int)>(), Array.Empty<RejectedHighlightCandidate>());

    /// <summary>
    /// Applies LocalContrastNormalizer to <paramref name="bitmap"/> in place.
    /// Never throws outward: any failure degrades to "did nothing this tick"
    /// (empty result, bitmap left as captured) rather than blocking the
    /// capture the caller already has in hand.
    /// </summary>
    public static NormalizeResult NormalizeInPlace(Bitmap bitmap, RowNormalizerMode mode = RowNormalizerMode.Local)
    {
        try
        {
            // Runtime fallback (settings.json OrderAssistRowNormalizer =
            // "legacy"): the exact pre-change gate-then-binarize path.
            if (mode == RowNormalizerMode.Legacy) return LegacyRowHighlightNormalizer.NormalizeInPlace(bitmap);

            var local = NormalizeInPlaceCore(bitmap);
            if (!local.SafetyValveTripped) return local;

            // Safety valve: the local pass was discarded (bitmap untouched
            // and unlocked again). Raw colored rows read worse than the
            // legacy gate's output, so run the legacy normalizer on this
            // tick's bitmap instead, and still report the trip for logging.
            var legacy = LegacyRowHighlightNormalizer.NormalizeInPlace(bitmap);
            return new NormalizeResult(legacy.AcceptedBands, legacy.RejectedCandidates, SafetyValveTripped: true);
        }
        catch
        {
            return EmptyResult;
        }
    }

    [ThreadStatic]
    private static byte[]? _buffer;

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

            // Reused per thread (UI thread): an 8 MB buffer every 500 ms tick
            // would land on the large object heap.
            var needed = stride * height;
            var buffer = _buffer;
            if (buffer is null || buffer.Length < needed) buffer = _buffer = new byte[needed];
            Marshal.Copy(data.Scan0, buffer, 0, needed);

            var result = LocalContrastNormalizer.Normalize(buffer, width, height, stride);
            if (result.Discarded) return new NormalizeResult(Array.Empty<(int, int)>(), Array.Empty<RejectedHighlightCandidate>(), SafetyValveTripped: true);
            if (result.ChangedPixels == 0) return EmptyResult;

            Marshal.Copy(buffer, 0, data.Scan0, needed);
            return new NormalizeResult(result.ChangedBands, Array.Empty<RejectedHighlightCandidate>());
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
