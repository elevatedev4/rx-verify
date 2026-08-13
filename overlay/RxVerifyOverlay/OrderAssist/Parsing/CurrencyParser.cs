using System;
using System.Globalization;

namespace RxVerifyOverlay.OrderAssist.Parsing;

/// <summary>
/// Currency-tolerant decimal parsing for OCR'd grid cells — strips a
/// leading "$" and thousands-separator commas, accepts a plain leading
/// "-" for negative (e.g. Pioneer's BOH/EOH columns, "-12.0000") and also
/// accounts-style parentheses for negative ("(12.50)"), and treats
/// blank/whitespace-only/non-numeric text as "no value" (null) rather
/// than throwing or defaulting to zero — callers (ZeroQuantityDetector,
/// SubstitutionRecommender) must be able to tell "genuinely zero" apart
/// from "couldn't read a number here at all".
/// </summary>
public static class CurrencyParser
{
    public static decimal? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var cleaned = text.Trim();

        var negative = false;
        if (cleaned.Length >= 2 && cleaned[0] == '(' && cleaned[^1] == ')')
        {
            negative = true;
            cleaned = cleaned[1..^1];
        }

        cleaned = cleaned.Replace("$", "").Replace(",", "").Trim();
        if (cleaned.Length == 0) return null;

        if (!decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        return negative ? -Math.Abs(value) : value;
    }
}
