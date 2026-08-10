using System;
using System.Collections.Generic;
using RxVerifyOverlay.Models;

namespace RxVerifyOverlay.Ocr;

/// <summary>
/// Detects whether the OCR-captured Rx-area words contain PioneerRx's
/// "Escript" tab-label marker (e.g. the center tab strip reads
/// "New Prescription Escript [3]" or "Refill Replace Escript [3]" only
/// when the current Rx actually IS an e-script — a transfer or a faxed
/// image never shows that word at all).
///
/// OCR reliably garbles this specific word (Will's real captures show
/// "EscriDt" for one Escript label), so an exact string-equality check
/// would silently reject a real e-script. This tolerates up to 2
/// single-character substitutions PER WORD, but ONLY when each
/// substituted character pair belongs to a known OCR-confusable class
/// (d/p, l/i/1, o/0 — see ConfusableGroups). A mismatch outside those
/// classes disqualifies the word immediately rather than counting toward
/// the tolerance budget — this is deliberately NOT a general edit-distance
/// matcher, which would also accept unrelated 1-2-letter-different words
/// that have nothing to do with OCR noise.
///
/// Pure/static, no UIA/WinRT/PHI — directly unit-testable, see
/// Tests/EscriptMarkerDetectorTests.cs.
/// </summary>
public static class EscriptMarkerDetector
{
    private const string Marker = "escript";
    private const int MaxConfusableSubstitutions = 2;

    /// <summary>
    /// Characters PioneerRx's UI font + Windows.Media.Ocr regularly swap
    /// for one another. Membership is symmetric within a group (any
    /// member is "confusable" with any other member, including itself).
    /// </summary>
    private static readonly char[][] ConfusableGroups =
    {
        new[] { 'd', 'p' },
        new[] { 'l', 'i', '1' },
        new[] { 'o', '0' }
    };

    /// <summary>True if ANY word in the OCR capture fuzzy-matches "escript" — see class doc for the matching rule.</summary>
    public static bool ContainsMarker(IEnumerable<OcrWord> words)
    {
        foreach (var word in words)
        {
            if (IsMarkerWord(word.Text)) return true;
        }

        return false;
    }

    /// <summary>
    /// Case-insensitive fuzzy match of a single OCR word against
    /// "escript". Requires an EXACT length match (7 characters) — OCR
    /// tokenizes on whitespace, so the real "Escript" label and any
    /// adjacent "[3]" bracket come through as separate words; requiring
    /// exact length keeps this from accidentally matching a substring
    /// inside an unrelated longer word.
    /// </summary>
    internal static bool IsMarkerWord(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (text.Length != Marker.Length) return false;

        var substitutions = 0;
        for (var i = 0; i < Marker.Length; i++)
        {
            var candidateChar = char.ToLowerInvariant(text[i]);
            var markerChar = Marker[i];
            if (candidateChar == markerChar) continue;

            if (!AreConfusable(candidateChar, markerChar)) return false;

            substitutions++;
            if (substitutions > MaxConfusableSubstitutions) return false;
        }

        return true;
    }

    private static bool AreConfusable(char a, char b)
    {
        if (a == b) return true;

        foreach (var group in ConfusableGroups)
        {
            if (Array.IndexOf(group, a) >= 0 && Array.IndexOf(group, b) >= 0) return true;
        }

        return false;
    }
}
