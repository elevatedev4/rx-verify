using System;
using System.Text.RegularExpressions;

namespace RxVerifyOverlay.OrderAssist.Parsing;

/// <summary>
/// Extracts the per-package quantity from a Catalog Item Substitution
/// "Shipping Size" cell's free-text OCR value — the owner's reference
/// screenshot renders these as e.g. "1 Stock Package with 30.0000 Tablets"
/// or "1 Stock Package with 500.0000 EA": a leading PACKAGE COUNT (always
/// "1" in every row observed), the literal word "with", then the actual
/// per-package quantity PackageClassifier needs to tell a 30-count bottle
/// from a 500-count case.
///
/// Deliberately anchors on the word "with" rather than "the first/last
/// number in the string" specifically so the leading package-count number
/// (always 1, never the thing we want) can never be mistaken for the
/// quantity — see the two Parse cases below.
/// </summary>
public static class PackageQuantityParser
{
    private const string WithKeyword = "with";
    private static readonly Regex NumberToken = new(@"-?[\d,]+(?:\.\d+)?", RegexOptions.Compiled);

    /// <summary>
    /// Null for blank/unreadable text or, deliberately, for a cell where
    /// "with" is present but nothing numeric follows it. (JUDGMENT CALL:
    /// this is exactly the shape a UI-truncated OCR capture would produce
    /// — see class doc's "1 Stock Package with 30.0000..." example — and
    /// falling back to searching the whole string in that case would just
    /// as likely pick up the leading package-COUNT "1" instead, silently
    /// mis-classifying a large package as small. Fail closed instead: no
    /// reading is safer than a wrong one.)
    /// </summary>
    public static decimal? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var withIndex = text.IndexOf(WithKeyword, StringComparison.OrdinalIgnoreCase);
        if (withIndex >= 0)
        {
            var afterWith = text[(withIndex + WithKeyword.Length)..];
            var match = NumberToken.Match(afterWith);
            return match.Success ? CurrencyParser.Parse(match.Value) : null;
        }

        // No "with" keyword at all -- a different cell shape than the
        // reference screenshot (e.g. a plain "500 EA"). Safe to fall back
        // to the first number found since there's no leading package-count
        // number to be confused with.
        var fallback = NumberToken.Match(text);
        return fallback.Success ? CurrencyParser.Parse(fallback.Value) : null;
    }
}
