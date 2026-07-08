using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.Ocr;

namespace RxVerifyOverlay.Uia;

/// <summary>
/// VerifyOCR's replacement for FieldReader.ReadSource(): produces the
/// SOURCE side of the comparison by capturing a screen region (no tab
/// switch — see Ocr/EscriptImageCapture.cs) and running local OCR over
/// it (Ocr/IOcrEngine — WindowsMediaOcrEngine by default), instead of
/// walking PioneerRx's Escript UIA tree.
///
/// v1: this class no longer parses OCR output into a PrescriptionRecord
/// itself (the C# OcrEscriptParser it used to call is retired — see
/// branch report). It hands back the structured OCR Words instead;
/// EngineClient.VerifyAsync(IReadOnlyList&lt;OcrWord&gt;, ...) sends those
/// words straight to the TS engine (src/cli.ts), which derives the
/// source PrescriptionRecord itself via src/ocr/parseEscriptOcr.ts — the
/// label/value association logic is safety-critical enough to live in
/// the one place it can actually be unit-tested (vitest), not here.
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
    /// Capture -&gt; OCR -&gt; structured Words (v1: no parsing happens here
    /// any more — see class doc), with full Stopwatch timing and both a
    /// raw-text AND structured-word log line on every call (see
    /// Ocr/OcrLogger.cs — the headline v0 deliverable: proving speed and
    /// text quality). Never throws — any capture/OCR failure becomes
    /// OcrCaptureResult.Error (with empty Words) so a bad read shows a
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

            var totalMs = totalStopwatch.ElapsedMilliseconds;

            // Structured word dump (branch brief item 7), alongside the
            // flat-text diagnostic log v0 already had — see OcrLogger.LogRead.
            OcrLogger.LogRead(captureMs, ocrMs, totalMs, ocrText.Text, ocrText.Words);

            return new OcrCaptureResult
            {
                Words = ocrText.Words,
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
    /// v1 cheap pre-gate: is there enough OCR text to bother sending to
    /// the engine at all? v0's IsSourceUsable checked the PARSED record
    /// for patient name + drug name both present — that check no longer
    /// exists here since parsing moved to the TS engine (see class doc).
    /// This is deliberately a much cheaper heuristic (a minimum word
    /// count) rather than re-implementing field detection twice: a
    /// genuinely bad/empty capture will have very few recognized words,
    /// while a real e-script pane (13 labels + 13 values, at minimum)
    /// always has well more than this threshold. A capture that passes
    /// this gate but still doesn't parse into a usable record surfaces
    /// as "not provided" (yellow) verdicts from the engine itself, not a
    /// hard error — see src/ocr/parseEscriptOcr.ts's "never associate a
    /// misassigned value" philosophy. Threshold picked conservatively
    /// low (not tuned against a real capture — flagged in branch report).
    /// </summary>
    private const int MinUsableWordCount = 10;

    public static bool IsSourceUsable(IReadOnlyList<OcrWord> words)
    {
        return words.Count(w => !string.IsNullOrWhiteSpace(w.Text)) >= MinUsableWordCount;
    }
}
