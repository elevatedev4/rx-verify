using System.Collections.Generic;
using System.Linq;
using RxVerifyOverlay.Uia;

namespace RxVerifyOverlay.Diagnostics;

/// <summary>
/// Pure builder for the auto-diagnostic report's Correction/note text —
/// see Reporting/RxReportPayload.cs Correction's doc for why this field
/// (not a new payload property) is where free-form diagnostic prose
/// belongs. No I/O, no WPF — see
/// RxVerifyOverlay.Tests/AutoDiagnosticNoteBuilderTests.cs.
///
/// PHI SAFETY: every input here is either already-PHI-filtered
/// (refillsOcrRegionWords — see its own doc chain back to
/// src/ocr/parseEscriptOcr.ts buildRefillsOcrRegionWords) or inherently
/// non-identifying (a render-state name, a miss-reason code, a screen
/// mode, a document classification word, a git commit sha, the refills
/// FIELD's own display text — never a patient/prescriber name, DOB,
/// address, or full Rx text). Never pass anything else into this.
/// </summary>
public static class AutoDiagnosticNoteBuilder
{
    /// <summary>Every auto-filed report's Correction text starts with this exact prefix, so a human triaging HQ reports can tell an automatic report apart from a pharmacist-typed one at a glance.</summary>
    public const string NotePrefix = "AUTO-DIAGNOSTIC refills unassessed — ";

    public static string Build(
        RefillsBoxRenderState renderState,
        string? sourceValueText,
        string? refillsMissReason,
        RxScreenMode screenMode,
        string documentClassification,
        string? appCommit,
        IReadOnlyList<string>? refillsOcrRegionWords,
        int streak = 0,
        double persistedSeconds = 0)
    {
        var sourceText = string.IsNullOrEmpty(sourceValueText) ? "undefined" : sourceValueText;
        var missReasonText = string.IsNullOrEmpty(refillsMissReason) ? "none" : refillsMissReason;
        var commitText = string.IsNullOrEmpty(appCommit) ? "unknown" : appCommit;
        var ocrWordsText = refillsOcrRegionWords is { Count: > 0 } ? string.Join(" ", refillsOcrRegionWords) : "(none captured)";

        return NotePrefix +
               $"boxState={renderState}; " +
               $"engineRefillsValue={sourceText}; " +
               $"missReason={missReasonText}; " +
               $"screenMode={screenMode}; " +
               $"document={documentClassification}; " +
               $"commit={commitText}; " +
               $"nearbyOcrWords=[{ocrWordsText}]; " +
               $"streak={streak};persistedSeconds={persistedSeconds:0}";
    }
}
