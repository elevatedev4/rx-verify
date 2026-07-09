using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using RxVerifyOverlay.Models;

namespace RxVerifyOverlay.Ocr;

/// <summary>
/// Raw OCR output for one recognized region — text (both as one blob and
/// split into the lines the OCR engine itself detected) PLUS, as of v1,
/// every recognized WORD with its on-screen bounding box (Words). Kept
/// engine-agnostic (no Windows.Media.Ocr types here) so IOcrEngine could
/// be swapped for a Tesseract implementation later without touching any
/// caller (see IOcrEngine doc) — Words uses the plain Models.OcrWord DTO,
/// not WinRT's own OcrWord type.
///
/// WHY WORDS NOW MATTER (v1): the TS engine's src/ocr/parseEscriptOcr.ts
/// needs real (x, y, w, h) geometry to reconstruct the on-screen layout
/// (lines, label-vs-value blocks) and to skip the toolbar — Text/Lines
/// alone (v0's only output) can't support that. Text/Lines are kept
/// unchanged for diagnostics/logging (Ocr/OcrLogger.cs, the overlay's
/// "Raw OCR text" view) — they're just no longer what gets parsed.
/// </summary>
public sealed class OcrTextResult
{
    public string Text { get; init; } = "";
    public IReadOnlyList<string> Lines { get; init; } = Array.Empty<string>();
    public IReadOnlyList<OcrWord> Words { get; init; } = Array.Empty<OcrWord>();
}

/// <summary>
/// Small seam between "read text off a bitmap" and everything that
/// consumes it (capture, logging, parsing). WindowsMediaOcrEngine (this
/// branch's only implementation) wraps Windows.Media.Ocr.OcrEngine — see
/// that class's doc for the unpackaged-WinRT build risk. A future
/// Tesseract-based implementation (or any other local OCR engine) is a
/// drop-in behind this same interface; nothing else in the OCR pipeline
/// (EscriptImageCapture, OcrEscriptParser, OcrFieldReader, OcrLogger)
/// depends on which one is wired up.
/// </summary>
public interface IOcrEngine
{
    Task<OcrTextResult> RecognizeAsync(Bitmap bitmap, CancellationToken cancellationToken = default);
}

/// <summary>
/// Full result of one OCR source-read pass (Uia/OcrFieldReader.cs
/// ReadSourceFromOcrAsync): the structured OCR Words for the engine to
/// parse (v1 — see class doc on OcrTextResult.Words), PLUS every
/// diagnostic Will needs to judge OCR quality/speed on his own screen —
/// raw text, per-stage timing, char count. Error is non-null (and Words
/// is empty) on any capture/OCR failure; callers must check it before
/// trusting Words — see OverlayViewModel.RefreshAsync.
///
/// v0 HAD a `Record` property here (the C# OcrEscriptParser's parsed
/// PrescriptionRecord). RETIRED in v1: parsing moved entirely into the
/// TS engine (src/ocr/parseEscriptOcr.ts, called from src/cli.ts), which
/// EngineClient.VerifyAsync now sends Words to directly — see
/// Engine/EngineClient.cs VerifyAsync(IReadOnlyList&lt;OcrWord&gt;, ...).
/// The overlay no longer holds a parsed PrescriptionRecord for the
/// source side at all.
/// </summary>
public sealed class OcrCaptureResult
{
    public IReadOnlyList<OcrWord> Words { get; init; } = Array.Empty<OcrWord>();
    public string RawText { get; init; } = "";
    public long CaptureMs { get; init; }
    public long OcrMs { get; init; }
    public long TotalMs { get; init; }
    public int CharCount { get; init; }

    /// <summary>Non-null on any capture/recognition failure — see OcrFieldReader.ReadSourceFromOcrAsync's catch block. Words is empty in that case, never partially populated.</summary>
    public string? Error { get; init; }
}
