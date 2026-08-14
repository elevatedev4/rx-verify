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
    // REVIEW FIX (blocking): word-boundary match, not a raw substring --
    // cheap robustness against "with" appearing as part of some other
    // token (non-blocking finding #2).
    private static readonly Regex WithKeywordPattern = new(@"\bwith\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NumberToken = new(@"-?[\d,]+(?:\.\d+)?", RegexOptions.Compiled);

    /// <summary>
    /// Null for blank/unreadable text, for a cell where "with" is present
    /// but nothing numeric follows it, and — REVIEW FIX (blocking) — for a
    /// cell where "with" ISN'T found but the text still contains more than
    /// one number.
    ///
    /// That last case is the one the first pass got wrong: "no 'with'
    /// found" does NOT reliably mean "a differently-shaped cell with no
    /// leading count" — it's equally consistent with OCR mangling the word
    /// "with" itself ("wlth", "w1th") on an otherwise normally-shaped cell
    /// that STILL has its leading package-count number sitting right
    /// there. Once "with" itself is unreadable, text alone can't tell
    /// those two shapes apart, so grabbing "the first number" in that
    /// situation is a coin flip between the real quantity and the leading
    /// count — e.g. "1 Stock Package wlth 500.0000 EA" would have returned
    /// 1 (wrong: a 500-count package silently classified as Small) instead
    /// of failing closed. The fallback below is only trusted when the cell
    /// has EXACTLY one number in it at all — nothing else it could
    /// possibly be but the quantity (e.g. a plain "500 EA").
    /// </summary>
    public static decimal? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var withMatch = WithKeywordPattern.Match(text);
        if (withMatch.Success)
        {
            var afterWith = text[(withMatch.Index + withMatch.Length)..];
            var match = NumberToken.Match(afterWith);
            return match.Success ? CurrencyParser.Parse(match.Value) : null;
        }

        var allNumbers = NumberToken.Matches(text);
        if (allNumbers.Count != 1) return null;

        return CurrencyParser.Parse(allNumbers[0].Value);
    }
}
