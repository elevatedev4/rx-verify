using System.Collections.Generic;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.Uia;

namespace RxVerifyOverlay.Ocr;

/// <summary>
/// The three outcomes of deciding whether the OCR path should even
/// attempt an engine compare for the current capture — see
/// OcrSourceUsability.Evaluate.
/// </summary>
public enum OcrSourceUsabilityDecision
{
    /// <summary>Healthy word count AND the "Escript" marker was found — proceed with the normal engine compare.</summary>
    Usable,

    /// <summary>Too few OCR words to trust ANY read (e.g. window mid-load) — takes priority over the marker check, since a sparse capture can't reliably prove the marker's absence either.</summary>
    TooSparse,

    /// <summary>Healthy word count, but no "Escript" marker anywhere in the capture — this Rx is not an e-script (transfer, faxed image, etc.), so the engine compare should be skipped entirely.</summary>
    NotAnEscript
}

/// <summary>
/// Combines OcrFieldReader.IsSourceUsable (word-count gate) with
/// EscriptMarkerDetector.ContainsMarker (escript-tab-label gate) into the
/// single decision OverlayViewModel.RefreshFromOcrAsync branches on.
/// Kept as a separate pure static method (rather than inlined in the
/// ViewModel) so the "sparse text wins over not-an-escript" ordering is
/// directly unit-testable — see Tests/OcrSourceUsabilityTests.cs.
/// </summary>
public static class OcrSourceUsability
{
    public static OcrSourceUsabilityDecision Evaluate(IReadOnlyList<OcrWord> words)
    {
        if (!OcrFieldReader.IsSourceUsable(words)) return OcrSourceUsabilityDecision.TooSparse;
        if (!EscriptMarkerDetector.ContainsMarker(words)) return OcrSourceUsabilityDecision.NotAnEscript;
        return OcrSourceUsabilityDecision.Usable;
    }
}
