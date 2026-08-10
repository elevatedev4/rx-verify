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
/// TRAILING GLUE (post-review hardening): before the length check, each
/// candidate word has trailing non-letter characters stripped — this is
/// the same "glued OCR token" class the rest of this codebase has hit
/// repeatedly (see rx-verify's ocr-parse-escript.test.ts "fill)" /
/// normalize-address.test.ts "StSte2050" precedents): a missed marker
/// here blanks REAL verdicts, so it's worth being cheap and robust
/// rather than requiring a perfectly space-delimited "Escript" token.
/// Covers a trailing period ("Escript.") as well as a directly-glued
/// tab-count badge with no separating space ("Escript[3]" — the ']' AND
/// the digit '3' AND the '[' are all stripped, since none of them are
/// letters and the marker itself is purely alphabetic). Only the
/// TRAILING end is trimmed, never the front.
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
    /// "escript". Trailing non-letter characters are stripped first (see
    /// class doc's TRAILING GLUE section), then requires an EXACT length
    /// match (7 characters) against what remains — OCR tokenizes on
    /// whitespace, so in the normal case the real "Escript" label and
    /// any adjacent "[3]" bracket already come through as separate
    /// words; the trim only matters when they're glued with no space.
    /// Requiring exact length (post-trim) keeps this from accidentally
    /// matching a substring inside an unrelated longer word.
    /// </summary>
    internal static bool IsMarkerWord(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        var trimmed = TrimTrailingNonLetters(text);
        if (trimmed.Length != Marker.Length) return false;

        var substitutions = 0;
        for (var i = 0; i < Marker.Length; i++)
        {
            var candidateChar = char.ToLowerInvariant(trimmed[i]);
            var markerChar = Marker[i];
            if (candidateChar == markerChar) continue;

            if (!AreConfusable(candidateChar, markerChar)) return false;

            substitutions++;
            if (substitutions > MaxConfusableSubstitutions) return false;
        }

        return true;
    }

    /// <summary>
    /// Strips characters from the END of the word for as long as they
    /// aren't letters — punctuation ("Escript." -> "Escript"), a glued
    /// bracket badge ("Escript[3]" -> "Escript"), or any mix. Digits are
    /// stripped too, deliberately: the marker itself is purely
    /// alphabetic, so any trailing digit run only ever means glued-on
    /// debris (a tab-count badge), never part of the marker word itself.
    /// Only trims the trailing end — a leading garbage character would
    /// mean OCR corrupted the word's actual first letter, which is a
    /// different failure mode this function doesn't attempt to recover.
    /// </summary>
    private static string TrimTrailingNonLetters(string text)
    {
        var end = text.Length;
        while (end > 0 && !char.IsLetter(text[end - 1])) end--;
        return text[..end];
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
