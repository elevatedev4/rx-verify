using System.Collections.Generic;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.Ocr;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for EscriptMarkerDetector (Ocr/EscriptMarkerDetector.cs) —
/// the fuzzy "Escript" tab-label matcher behind the non-escript blank
/// state (owner's request: "When we're not looking at an Escript, blank
/// out the view..."). All OCR words below are synthetic placeholder
/// text, mirroring RxLogFormatterTests.cs's "no real patient data"
/// convention.
/// </summary>
public class EscriptMarkerDetectorTests
{
    private static OcrWord Word(string text) => new() { Text = text, X = 0, Y = 0, W = 10, H = 10 };

    [Fact]
    public void ContainsMarkerTrueForExactWordCaseInsensitive()
    {
        var words = new[] { Word("New"), Word("Prescription"), Word("Escript"), Word("[3]") };

        Assert.True(EscriptMarkerDetector.ContainsMarker(words));
    }

    [Fact]
    public void ContainsMarkerTrueForExactWordLowercase()
    {
        var words = new[] { Word("refill"), Word("replace"), Word("escript") };

        Assert.True(EscriptMarkerDetector.ContainsMarker(words));
    }

    [Theory]
    [InlineData("EscriDt")]  // real capture: p -> D (d/p confusable class)
    [InlineData("Escrlpt")]  // i -> l (l/i/1 confusable class)
    [InlineData("escr1pt")]  // i -> 1 (l/i/1 confusable class)
    public void ContainsMarkerTrueForGarbledOcrVariants(string garbled)
    {
        var words = new[] { Word("Refill"), Word("Replace"), Word(garbled), Word("[3]") };

        Assert.True(EscriptMarkerDetector.ContainsMarker(words));
    }

    [Fact]
    public void ContainsMarkerTrueForTwoSimultaneousConfusableSubstitutions()
    {
        // p -> D AND i -> l in the same word — still within the 2-substitution tolerance.
        var words = new[] { Word("EscrlDt") };

        Assert.True(EscriptMarkerDetector.ContainsMarker(words));
    }

    [Fact]
    public void ContainsMarkerFalseWhenAnyMismatchIsNotAKnownConfusablePair()
    {
        // p -> D and i -> l are both confusable substitutions, but c -> k
        // is NOT (no confusable class covers c/k) — a single
        // non-confusable mismatch disqualifies the whole word regardless
        // of how many genuinely-confusable substitutions accompany it.
        var words = new[] { Word("EskrlDt") };

        Assert.False(EscriptMarkerDetector.ContainsMarker(words));
    }

    [Fact]
    public void ContainsMarkerFalseForUnrelatedSubstitution()
    {
        // A single mismatch that ISN'T in a confusable class ('s' vs
        // 'x') must disqualify the word entirely, not count as noise.
        var words = new[] { Word("Excript") };

        Assert.False(EscriptMarkerDetector.ContainsMarker(words));
    }

    [Fact]
    public void ContainsMarkerFalseWhenNoMarkerWordPresent()
    {
        // A realistic transfer/faxed-image capture: normal Rx-area
        // words, but no "Escript" label anywhere.
        var words = new[]
        {
            Word("Patient:"), Word("Jane"), Word("Synthtest"), Word("Drug:"),
            Word("Amoxicillin"), Word("500mg"), Word("Refills:"), Word("2")
        };

        Assert.False(EscriptMarkerDetector.ContainsMarker(words));
    }

    [Fact]
    public void ContainsMarkerFalseForEmptyWordList()
    {
        Assert.False(EscriptMarkerDetector.ContainsMarker(System.Array.Empty<OcrWord>()));
    }

    [Fact]
    public void ContainsMarkerFalseForWordOfDifferentLengthEvenAfterTrim()
    {
        // "Escripts" has no trailing punctuation/digits to trim at all —
        // it's just genuinely 8 letters, not 7, so it must stay rejected.
        var words = new[] { Word("Escripts") };

        Assert.False(EscriptMarkerDetector.ContainsMarker(words));
    }

    [Fact]
    public void ContainsMarkerTrueForTrailingPeriodGlue()
    {
        // Post-review hardening: a trailing period (end-of-sentence-style
        // OCR punctuation glued onto the word) must be trimmed before
        // the length check, not treated as a length/content mismatch.
        var words = new[] { Word("Escript.") };

        Assert.True(EscriptMarkerDetector.ContainsMarker(words));
    }

    [Fact]
    public void ContainsMarkerTrueForGluedBracketTabCountBadge()
    {
        // Post-review hardening: OCR sometimes runs the "[3]" tab-count
        // badge directly into "Escript" with no separating space —
        // trailing ']', the digit, and '[' must all be stripped before
        // the length check (see TrimTrailingNonLetters doc).
        var words = new[] { Word("Escript[3]") };

        Assert.True(EscriptMarkerDetector.ContainsMarker(words));
    }

    [Fact]
    public void ContainsMarkerFalseForGlueGarbageAroundAGenuinelyWrongWord()
    {
        // Trimming the glued "[3]" badge must NOT loosen the fuzzy-match
        // rule itself: "Excript" (s -> x, not a confusable substitution)
        // stays rejected even once the trailing bracket noise is gone.
        var words = new[] { Word("Excript[3]") };

        Assert.False(EscriptMarkerDetector.ContainsMarker(words));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ContainsMarkerFalseForNullOrEmptyWordText(string? text)
    {
        var words = new List<OcrWord> { new() { Text = text ?? "", X = 0, Y = 0, W = 0, H = 0 } };

        Assert.False(EscriptMarkerDetector.ContainsMarker(words));
    }
}
