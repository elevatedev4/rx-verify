using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.Ocr;
using RxVerifyOverlay.Parsing;

namespace RxVerifyOverlay.Uia;

/// <summary>
/// VerifyOCR's replacement for FieldReader.ReadSource(): produces the
/// SOURCE side of the comparison by capturing a screen region (no tab
/// switch — see Ocr/EscriptImageCapture.cs) and running local OCR over
/// it (Ocr/IOcrEngine — WindowsMediaOcrEngine by default), instead of
/// walking PioneerRx's Escript UIA tree. Feeds the exact same
/// PrescriptionRecord shape into the SAME unchanged EngineClient.
/// VerifyAsync the UIA path always used — the TS engine is blind to how
/// the source was produced (see rx-verify src/cli.ts).
///
/// FieldReader.ReadEntered() (the technician-entered/UIA side) is
/// UNTOUCHED and still used directly by OverlayViewModel — only the
/// source-producing call is swapped for this class.
/// </summary>
public sealed class OcrFieldReader
{
    private readonly IOcrEngine _ocrEngine;

    public OcrFieldReader(IOcrEngine? ocrEngine = null)
    {
        _ocrEngine = ocrEngine ?? new WindowsMediaOcrEngine();
    }

    /// <summary>
    /// Capture -&gt; OCR -&gt; OcrEscriptParser.Parse -&gt; PrescriptionRecord,
    /// with full Stopwatch timing and a raw-text log line on every call
    /// (see Ocr/OcrLogger.cs — the headline v0 deliverable: proving speed
    /// and text quality). Never throws — any capture/OCR failure becomes
    /// OcrCaptureResult.Error (with a blank Record) so a bad read shows a
    /// status message instead of crashing the overlay (see
    /// ViewModels/OverlayViewModel.cs RefreshAsync, mirroring how
    /// FieldReader.ReadSource's UIA failures were always handled).
    ///
    /// SELF-OCCLUSION GUARD: when <paramref name="overlayVisibilityController"/>
    /// is provided (MainWindow implements it — see
    /// Ocr/IOverlayVisibilityController.cs), the overlay window is
    /// briefly hidden immediately before the screen-region capture and
    /// ALWAYS restored immediately after (even on exception — see
    /// CaptureRegionGuardedAsync's finally). The overlay is Topmost and
    /// freely movable/resizable, so an un-guarded capture could OCR the
    /// overlay's OWN UI if it happened to overlap the capture region.
    /// Only the capture instant is guarded — OCR recognition runs against
    /// the already-captured bitmap, not the live screen, so there's no
    /// reason to keep the overlay hidden any longer than that one GDI
    /// call. Null is accepted (no-op, capture proceeds unguarded) so this
    /// class stays usable/testable without a live WPF window.
    /// </summary>
    public async Task<OcrCaptureResult> ReadSourceFromOcrAsync(PioneerRxWindow window, OverlaySettings settings, IOverlayVisibilityController? overlayVisibilityController = null)
    {
        var totalStopwatch = Stopwatch.StartNew();
        try
        {
            var region = EscriptImageCapture.ResolveCaptureRegion(window, settings);
            if (region.Width <= 0 || region.Height <= 0)
            {
                return new OcrCaptureResult
                {
                    Error = "Capture region is empty — set an explicit capture region in Engine settings, " +
                            "or confirm the PioneerRx window/Escript pane is on screen."
                };
            }

            var captureStopwatch = Stopwatch.StartNew();
            using var bitmap = await CaptureRegionGuardedAsync(region, overlayVisibilityController);
            var captureMs = captureStopwatch.ElapsedMilliseconds;

            var ocrStopwatch = Stopwatch.StartNew();
            var ocrText = await _ocrEngine.RecognizeAsync(bitmap);
            var ocrMs = ocrStopwatch.ElapsedMilliseconds;

            var record = OcrEscriptParser.Parse(ocrText.Text);
            var totalMs = totalStopwatch.ElapsedMilliseconds;

            OcrLogger.LogRead(captureMs, ocrMs, totalMs, ocrText.Text);

            return new OcrCaptureResult
            {
                Record = record,
                RawText = ocrText.Text,
                CaptureMs = captureMs,
                OcrMs = ocrMs,
                TotalMs = totalMs,
                CharCount = ocrText.Text.Length
            };
        }
        catch (Exception ex)
        {
            OcrLogger.LogError(ex);
            return new OcrCaptureResult
            {
                Error = $"OCR capture/read failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// The actual hide -&gt; capture -&gt; restore sequence backing the
    /// SELF-OCCLUSION GUARD doc above. Kept as its own method (rather than
    /// inlined in ReadSourceFromOcrAsync) so the finally/restore-even-on-
    /// exception guarantee is easy to see is unconditional, mirroring
    /// Uia/FieldReader.cs ReadSource's tab select/read/restore pattern.
    /// </summary>
    private static async Task<Bitmap> CaptureRegionGuardedAsync(Rectangle region, IOverlayVisibilityController? overlayVisibilityController)
    {
        if (overlayVisibilityController is null)
        {
            return EscriptImageCapture.CaptureRegion(region);
        }

        await overlayVisibilityController.HideForCaptureAsync();
        try
        {
            return EscriptImageCapture.CaptureRegion(region);
        }
        finally
        {
            // ALWAYS restore, success or exception — the pharmacist must
            // never be left with the overlay hidden because a capture
            // failed partway through.
            overlayVisibilityController.RestoreAfterCapture();
        }
    }

    /// <summary>
    /// Mirrors FieldReader.IsStructuredSourceAvailable's gate (patient
    /// name AND drug name both present) — the OCR path has no equivalent
    /// of "the Escript tree control wasn't found at all", so this is
    /// purely a content check on the parsed record.
    /// </summary>
    public static bool IsSourceUsable(PrescriptionRecord source)
    {
        return !string.IsNullOrWhiteSpace(source.PatientName) && !string.IsNullOrWhiteSpace(source.Drug?.Name);
    }
}
