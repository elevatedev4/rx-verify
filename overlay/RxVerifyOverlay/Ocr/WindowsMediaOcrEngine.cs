using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using RxVerifyOverlay.Models;

namespace RxVerifyOverlay.Ocr;

/// <summary>
/// Local, free, on-device OCR via the WinRT Windows.Media.Ocr API —
/// ships with Windows itself (uses whatever language packs the user's
/// Windows profile has installed), so there's no model to bundle and no
/// network call, matching this app's local-only design (see
/// RxVerifyOverlay.csproj header and README "Local-only, by
/// construction").
///
/// UNPACKAGED-APP CAVEAT (the one real build/runtime risk here, flagged
/// per the branch brief — could not be verified on this Mac):
/// Windows.Media.Ocr is a WinRT API, normally consumed from a packaged
/// (MSIX) app. This overlay is an ordinary unpackaged win32/.NET desktop
/// app. Calling WinRT APIs from an unpackaged .NET 5+ app is a supported,
/// well-documented pattern (cswinrt-based projection, enabled here purely
/// by RxVerifyOverlay.csproj's TargetFramework carrying a
/// "-windows10.0.19041.0" suffix — see that file's comment) and does NOT
/// require the app to be packaged, elevated, or to declare any package
/// identity. Confidence this compiles+runs unpackaged on Win10/11:
/// MODERATE-HIGH — this exact combination (WPF + net8.0-windows10.0.x +
/// OcrEngine.TryCreateFromUserProfileLanguages) is a commonly-used
/// pattern, but it has not been build/run-verified for THIS project on
/// this Mac; the owner's first `dotnet run` is the real test.
/// </summary>
public sealed class WindowsMediaOcrEngine : IOcrEngine
{
    /// <summary>
    /// UPSCALE (field report fix: OCR silently dropped part of a sig
    /// line — "1 tab PO" captured, the rest of that line never appeared
    /// in the OCR word list AT ALL, i.e. the words are missing from the
    /// raw output, not mis-grouped). The captured region's text runs
    /// ~11px tall, well under Windows.Media.Ocr's reliable minimum text
    /// size — small enough that the engine can simply fail to detect
    /// some words rather than misread them. Upscaling 2x before
    /// recognition (see Upscale below) is the standard mitigation for
    /// small-text OCR misses; RecognizeAsync divides every returned
    /// word's (x, y, w, h) back down by the SAME factor before this
    /// class hands anything back, so the upscale is entirely transparent
    /// to every caller (OcrFieldReader, EscriptImageCapture's capture-
    /// region math, src/ocr/parseEscriptOcr.ts, and every existing test
    /// fixture) — they all see the ORIGINAL captured-bitmap coordinate
    /// space, unchanged. OcrTextResult.OcrScaleFactor carries the value
    /// through to the "OCR: ..." status line purely as a diagnostic (see
    /// ViewModels/OverlayViewModel.cs), not for any geometry callers do
    /// themselves.
    ///
    /// OCR DURATION TRADE-OFF (flagged, not measured — no live Windows
    /// box here): 2x LINEAR scale is 4x the PIXEL COUNT Windows.Media.Ocr
    /// has to process, which will likely increase OcrMs by more than 2x.
    /// Will's field logs currently show ocr 70-650ms with the capture
    /// bucket now fixed (cffe2bc) and attach/uia now fixed (this branch),
    /// so there's real headroom — but this needs live confirmation that
    /// OcrMs stays acceptable, not just that accuracy improves.
    /// </summary>
    private const float UpscaleFactor = 2.0f;

    /// <summary>
    /// Recognizes text in a GDI+ Bitmap (as produced by
    /// EscriptImageCapture.CaptureRegion) by converting it to a WinRT
    /// SoftwareBitmap and running Windows.Media.Ocr.OcrEngine over it.
    /// Throws (never swallows) on any failure — OcrFieldReader is the
    /// layer responsible for catching this and turning it into a
    /// graceful OcrCaptureResult.Error instead of crashing the overlay
    /// (see OcrFieldReader.ReadSourceFromOcrAsync).
    /// </summary>
    public async Task<OcrTextResult> RecognizeAsync(Bitmap bitmap, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        // TryCreateFromUserProfileLanguages picks whatever OCR language
        // pack matches the signed-in user's Windows display language —
        // the common case needs zero setup on Will's workstation. Null
        // means no matching OCR language pack is installed (rare on a
        // stock US English Windows 10/11 box, but possible on a stripped
        // image) — surfaced as a clear exception message rather than a
        // silent empty-text result.
        var ocrEngine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException(
                "No OCR language pack is available for the current Windows user profile language. " +
                "Install one via Settings > Time & Language > Language & region > Add a language " +
                "(ensure 'Optical character recognition' is included), then relaunch VerifyOCR.");

        using var upscaledBitmap = Upscale(bitmap, UpscaleFactor, out var scaleX, out var scaleY);

        using var softwareBitmap = await ConvertToSoftwareBitmapAsync(upscaledBitmap);
        cancellationToken.ThrowIfCancellationRequested();

        var ocrResult = await ocrEngine.RecognizeAsync(softwareBitmap);

        var lines = ocrResult.Lines.Select(l => l.Text).ToList();

        // Flatten every line's words into the flat Words list
        // src/ocr/parseEscriptOcr.ts consumes — it reconstructs its own
        // line grouping from (x, y, w, h), so no line boundary needs to
        // be preserved here beyond each word's own box. Windows.Media.Ocr's
        // OcrWord.BoundingRect is in the UPSCALED bitmap's pixel space
        // (see UPSCALE doc above) — divide by the ACTUAL per-axis scale
        // applied (scaleX/scaleY, not just the nominal UpscaleFactor —
        // see Upscale's doc for why those can differ by a sub-pixel
        // rounding amount) to land back in the ORIGINAL captured-region
        // coordinate space every caller already expects. These boxes
        // remain relative to the CAPTURED REGION, not the full screen —
        // parseEscriptOcr only ever compares words to each other
        // (relative geometry), never to an absolute screen position.
        var words = new List<RxVerifyOverlay.Models.OcrWord>();
        foreach (var line in ocrResult.Lines)
        {
            foreach (var word in line.Words)
            {
                words.Add(new RxVerifyOverlay.Models.OcrWord
                {
                    Text = word.Text ?? "",
                    X = word.BoundingRect.X / scaleX,
                    Y = word.BoundingRect.Y / scaleY,
                    W = word.BoundingRect.Width / scaleX,
                    H = word.BoundingRect.Height / scaleY
                });
            }
        }

        return new OcrTextResult
        {
            Text = ocrResult.Text ?? "",
            Lines = lines,
            Words = words,
            OcrScaleFactor = UpscaleFactor
        };
    }

    /// <summary>
    /// Upscales <paramref name="source"/> by (approximately)
    /// <paramref name="factor"/> using high-quality bicubic
    /// interpolation — the standard GDI+ mitigation for small-text OCR
    /// misses (see UpscaleFactor's doc). Caller owns and must dispose
    /// the returned Bitmap.
    ///
    /// <paramref name="scaleX"/>/<paramref name="scaleY"/> are the
    /// ACTUAL ratios applied (scaled dimension / source dimension), NOT
    /// necessarily bit-for-bit equal to <paramref name="factor"/>: pixel
    /// dimensions must be whole numbers, so scaledWidth/Height are
    /// rounded, which can shift the true ratio by a sub-pixel amount on
    /// an odd-sized source. RecognizeAsync divides by these exact ratios
    /// (not the nominal factor) so the coordinate round-trip back to the
    /// original space is exact, not approximate.
    /// </summary>
    private static Bitmap Upscale(Bitmap source, float factor, out double scaleX, out double scaleY)
    {
        var scaledWidth = Math.Max(1, (int)Math.Round(source.Width * factor));
        var scaledHeight = Math.Max(1, (int)Math.Round(source.Height * factor));

        var scaled = new Bitmap(scaledWidth, scaledHeight, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(scaled))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, 0, 0, scaledWidth, scaledHeight);
        }

        scaleX = source.Width > 0 ? (double)scaledWidth / source.Width : factor;
        scaleY = source.Height > 0 ? (double)scaledHeight / source.Height : factor;
        return scaled;
    }

    /// <summary>
    /// Bitmap -&gt; SoftwareBitmap by round-tripping through an in-memory
    /// PNG: GDI+ has no direct SoftwareBitmap constructor, but
    /// Windows.Graphics.Imaging.BitmapDecoder can build one from any
    /// encoded image stream. This costs an extra encode/decode pass
    /// (a few ms for a small captured region) but is the standard,
    /// well-documented bridge between System.Drawing and WinRT imaging
    /// types for exactly this "call WinRT OCR from GDI+ capture" case.
    /// </summary>
    private static async Task<SoftwareBitmap> ConvertToSoftwareBitmapAsync(Bitmap bitmap)
    {
        using var pngStream = new MemoryStream();
        bitmap.Save(pngStream, ImageFormat.Png);
        pngStream.Position = 0;

        using var randomAccessStream = new InMemoryRandomAccessStream();
        using (var outputStream = randomAccessStream.GetOutputStreamAt(0))
        {
            using var writer = new DataWriter(outputStream);
            writer.WriteBytes(pngStream.ToArray());
            await writer.StoreAsync();
            await outputStream.FlushAsync();
            writer.DetachStream();
        }

        randomAccessStream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(randomAccessStream);
        var rawBitmap = await decoder.GetSoftwareBitmapAsync();

        // OcrEngine.RecognizeAsync requires Bgra8 + either Premultiplied
        // or Ignore alpha mode — a freshly-decoded PNG SoftwareBitmap
        // isn't guaranteed to already be in that exact format, so convert
        // explicitly rather than assume.
        if (rawBitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 || rawBitmap.BitmapAlphaMode != BitmapAlphaMode.Premultiplied)
        {
            // NOT wrapped in `using` — ownership transfers to the caller,
            // which disposes it (see RecognizeAsync's `using var
            // softwareBitmap = ...`). rawBitmap itself is the one that
            // must be cleaned up here since it's being replaced.
            var converted = SoftwareBitmap.Convert(rawBitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            rawBitmap.Dispose();
            return converted;
        }

        return rawBitmap;
    }
}
