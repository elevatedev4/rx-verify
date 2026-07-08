using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using RxVerifyOverlay.Models;

namespace RxVerifyOverlay.Ocr;

/// <summary>
/// Raw OCR output for one recognized region — just text, both as one
/// blob and split into the lines the OCR engine itself detected. Kept
/// engine-agnostic (no Windows.Media.Ocr types here) so IOcrEngine could
/// be swapped for a Tesseract implementation later without touching any
/// caller (see IOcrEngine doc).
/// </summary>
public sealed class OcrTextResult
{
    public string Text { get; init; } = "";
    public IReadOnlyList<string> Lines { get; init; } = Array.Empty<string>();
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
/// ReadSourceFromOcrAsync): the parsed PrescriptionRecord for the engine,
/// PLUS every diagnostic Will needs to judge OCR quality/speed on his own
/// screen — raw text, per-stage timing, char count. Error is non-null
/// (and Record is an empty PrescriptionRecord) on any capture/OCR
/// failure; callers must check it before trusting Record — see
/// OverlayViewModel.RefreshAsync.
/// </summary>
public sealed class OcrCaptureResult
{
    public PrescriptionRecord Record { get; init; } = new();
    public string RawText { get; init; } = "";
    public long CaptureMs { get; init; }
    public long OcrMs { get; init; }
    public long TotalMs { get; init; }
    public int CharCount { get; init; }

    /// <summary>Non-null on any capture/recognition failure — see OcrFieldReader.ReadSourceFromOcrAsync's catch block. Record is a blank PrescriptionRecord in that case, never partially populated.</summary>
    public string? Error { get; init; }
}
