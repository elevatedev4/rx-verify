using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RxVerifyOverlay.Models;

namespace RxVerifyOverlay.OrderAssist.Geometry;

/// <summary>
/// Decides how many of a table's LEADING rows (as grouped by
/// TableRowGrouper) are header rows rather than data — needed because
/// both target Pioneer windows can wrap a header label onto 2 visual
/// lines (e.g. "Suggested" over "Order Qty" — see the owner's screenshot
/// of the Create Recommended Orders window), so "the header is exactly
/// row 0" isn't reliable.
///
/// HEURISTIC (JUDGMENT CALL, unverified against a real OCR capture — see
/// OrderAssistCoordinator's class doc and the branch report): a row is a
/// DATA row if any of its words looks like a decimal-formatted number
/// (digits, optional thousands separators, a literal decimal point,
/// optional leading $/-/parens). Both target windows are grid views whose
/// every real data row carries multiple decimal-formatted numeric columns
/// (BOH/EOH quantities, Cost Per Unit, Rebate Cost Per Unit, etc. — all
/// rendered with several decimal places in both of the owner's reference
/// screenshots), while no header LABEL text ever contains a literal
/// decimal point. This is deliberately NOT a "majority of words are
/// numeric" heuristic — a real data row's Description/Manufacturer/NDC
/// columns contribute plenty of non-numeric-looking tokens (drug names,
/// dash-separated NDC codes that don't parse as a single decimal), so a
/// majority-based rule would misfire; "at least one decimal-point number
/// anywhere in the row" is far more robust for this specific shape of
/// table. Capped at <paramref name="maxHeaderRows"/> (default 2, matching
/// the worst wrapping seen in either screenshot) so a pathological OCR
/// misread can't run away and swallow real data rows as "header".
/// </summary>
public static class HeaderBandLocator
{
    private static readonly Regex DecimalNumberPattern = new(@"^\(?-?\$?[\d,]+\.\d+\)?$", RegexOptions.Compiled);

    /// <summary>Number of leading rows to treat as the header band — stops at the first row IsDataRow calls a data row, or at <paramref name="maxHeaderRows"/>, whichever comes first. Returns 0 if the very first row already looks like data (nothing sane to treat as a header — callers should degrade to "no column found" rather than guess).</summary>
    public static int CountHeaderRows(IReadOnlyList<IReadOnlyList<OcrWord>> rows, int maxHeaderRows = 2)
    {
        var count = 0;
        for (var i = 0; i < rows.Count && i < maxHeaderRows; i++)
        {
            if (IsDataRow(rows[i])) break;
            count++;
        }

        return count;
    }

    /// <summary>True if any word in the row matches a decimal-formatted number — see class doc for why this is the chosen data-row signal.</summary>
    public static bool IsDataRow(IReadOnlyList<OcrWord> row) =>
        row.Any(w => !string.IsNullOrWhiteSpace(w.Text) && DecimalNumberPattern.IsMatch(w.Text.Trim()));
}
