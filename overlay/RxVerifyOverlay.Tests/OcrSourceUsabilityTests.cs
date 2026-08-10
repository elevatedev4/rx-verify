using System.Collections.Generic;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.Ocr;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for OcrSourceUsability.Evaluate (Ocr/OcrSourceUsability.cs)
/// — the blank-state decision behind Task 1c's guard: "if OCR text is
/// ALSO too sparse (window mid-load), prefer the existing 'not enough
/// text' behavior over a premature 'not an escript' claim." All OCR
/// words below are synthetic placeholder text.
/// </summary>
public class OcrSourceUsabilityTests
{
    private static OcrWord Word(string text) => new() { Text = text, X = 0, Y = 0, W = 10, H = 10 };

    /// <summary>12 words, well past OcrFieldReader's 10-word usability floor, INCLUDING the "Escript" marker.</summary>
    private static IReadOnlyList<OcrWord> HealthyEscriptCapture() => new[]
    {
        Word("New"), Word("Prescription"), Word("Escript"), Word("[3]"),
        Word("Patient:"), Word("Jane"), Word("Synthtest"),
        Word("Drug:"), Word("Amoxicillin"), Word("500mg"),
        Word("Refills:"), Word("2")
    };

    /// <summary>Same word count as HealthyEscriptCapture, but no "Escript" marker anywhere — a transfer/faxed-image Rx.</summary>
    private static IReadOnlyList<OcrWord> HealthyNonEscriptCapture() => new[]
    {
        Word("Transfer"), Word("Prescription"), Word("Notice"), Word("[3]"),
        Word("Patient:"), Word("Jane"), Word("Synthtest"),
        Word("Drug:"), Word("Amoxicillin"), Word("500mg"),
        Word("Refills:"), Word("2")
    };

    [Fact]
    public void EvaluateReturnsUsableForHealthyWordCountWithMarker()
    {
        Assert.Equal(OcrSourceUsabilityDecision.Usable, OcrSourceUsability.Evaluate(HealthyEscriptCapture()));
    }

    [Fact]
    public void EvaluateReturnsNotAnEscriptForHealthyWordCountWithoutMarker()
    {
        Assert.Equal(OcrSourceUsabilityDecision.NotAnEscript, OcrSourceUsability.Evaluate(HealthyNonEscriptCapture()));
    }

    [Fact]
    public void EvaluateReturnsTooSparseForFewWordsEvenWithMarker()
    {
        // Task 1c guard: a sparse capture that HAPPENS to include
        // "Escript" (e.g. only the tab strip was captured so far, mid
        // window-load) must still report TooSparse, not Usable — there
        // isn't enough text yet to trust ANY comparison.
        var words = new[] { Word("New"), Word("Prescription"), Word("Escript"), Word("[3]") };

        Assert.Equal(OcrSourceUsabilityDecision.TooSparse, OcrSourceUsability.Evaluate(words));
    }

    [Fact]
    public void EvaluateReturnsTooSparseOverNotAnEscriptWhenBothConditionsHold()
    {
        // Task 1c guard, the exact priority-ordering case: too few words
        // AND no marker in those few words. TooSparse must win — a
        // sparse capture can't reliably prove the marker is absent
        // either, so it must never be misreported as a confident
        // "not an escript" claim.
        var words = new[] { Word("Patient:"), Word("Jane"), Word("Synthtest") };

        Assert.Equal(OcrSourceUsabilityDecision.TooSparse, OcrSourceUsability.Evaluate(words));
    }

    [Fact]
    public void EvaluateReturnsTooSparseForEmptyWordList()
    {
        Assert.Equal(OcrSourceUsabilityDecision.TooSparse, OcrSourceUsability.Evaluate(System.Array.Empty<OcrWord>()));
    }
}
