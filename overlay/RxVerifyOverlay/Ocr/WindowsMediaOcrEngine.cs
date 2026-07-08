using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

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

        using var softwareBitmap = await ConvertToSoftwareBitmapAsync(bitmap);
        cancellationToken.ThrowIfCancellationRequested();

        var ocrResult = await ocrEngine.RecognizeAsync(softwareBitmap);

        var lines = ocrResult.Lines.Select(l => l.Text).ToList();
        return new OcrTextResult
        {
            Text = ocrResult.Text ?? "",
            Lines = lines
        };
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
