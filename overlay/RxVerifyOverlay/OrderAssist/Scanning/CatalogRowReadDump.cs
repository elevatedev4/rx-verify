using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.OrderAssist.Geometry;

namespace RxVerifyOverlay.OrderAssist.Scanning;

/// <summary>
/// W-T96 diagnostic: what Order Assist actually READ for each body row of the
/// "Recommended Order - Catalog Item Substitution Selection" screen (supplier
/// text, cost text, the row's background color class, and whether the row-
/// highlight normalizer rewrote it). Built only when the owner turns on
/// OverlaySettings.OrderAssistRowReadDump (off by default) and sent to HQ
/// through the existing feedback path (see OrderAssistCoordinator).
///
/// PRIVACY: this screen lists wholesalers/products and prices only. Even so,
/// nothing is trusted: Supplier text is cut to <see cref="MaxSupplierChars"/>
/// characters and reduced to letters, digits, space and . - &amp; ; Cost text
/// is reduced to digits and $ . , ( ) - ; no other OCR text, window title,
/// coordinate or pixel data is included. Pure and unit tested.
/// </summary>
public static class CatalogRowReadDump
{
    public const int MaxSupplierChars = 24;
    public const int MaxCostChars = 12;
    public const int MaxRows = 30;

    /// <summary>HQ rejects a feedback message longer than this (same limit as ReportsLogUpload.MaxMessageChars / FEEDBACK_MESSAGE_MAX_LENGTH).</summary>
    public const int MaxMessageChars = 4000;

    private const int NoteReserve = 48;

    /// <summary>One body row: its cell texts and vertical extent (OCR bitmap pixels) — the extent is used by the caller to sample the row's background color, and is NOT sent.</summary>
    public sealed record BodyRowRead(int RowIndex, string Supplier, string Cost, double Top, double Bottom);

    /// <summary>One row ready to print: the row read plus its measured background.</summary>
    public sealed record RowLine(int RowIndex, string Supplier, string Cost, string ColorClass, int Chroma, int Luminance, bool Normalized);

    /// <summary>Same header/column resolution as CatalogSubstitutionScanner.Analyze; empty if Supplier or Rebate Cost Per Unit cannot be resolved.</summary>
    public static IReadOnlyList<BodyRowRead> ReadBodyRows(IReadOnlyList<OcrWord> words)
    {
        var rows = TableRowGrouper.GroupIntoRows(words);
        var winner = HeaderRowWindowSelector.SelectBest(rows, new[] { CatalogSubstitutionScanner.SupplierHeaderLabel, CatalogSubstitutionScanner.RebateCostPerUnitHeaderLabel });
        if (winner is null) return Array.Empty<BodyRowRead>();

        var supplierColumn = ColumnResolver.ResolveExact(winner.Bands, CatalogSubstitutionScanner.SupplierHeaderLabel);
        var costColumn = ColumnResolver.ResolveExact(winner.Bands, CatalogSubstitutionScanner.RebateCostPerUnitHeaderLabel);
        if (supplierColumn is null || costColumn is null) return Array.Empty<BodyRowRead>();

        var bodyRows = rows.Skip(winner.StartRowIndex + winner.RowCount).ToList();
        var supplierCells = CellValueBucketizer.BucketColumn(bodyRows, supplierColumn);
        var costCells = CellValueBucketizer.BucketColumn(bodyRows, costColumn);

        var result = new List<BodyRowRead>(bodyRows.Count);
        for (var i = 0; i < bodyRows.Count; i++)
        {
            var top = bodyRows[i].Min(w => w.Y);
            var bottom = bodyRows[i].Max(w => w.Y + w.H);
            result.Add(new BodyRowRead(i, supplierCells[i].Text, costCells[i].Text, top, bottom));
        }
        return result;
    }

    /// <summary>
    /// Background color class of one row from its median scanline color:
    /// chroma (max-min channel) under 30 is "white" when light (every channel
    /// near-white; luminance 235+) else "neutral"; otherwise the hue picks
    /// "yellow" (40-75 deg), "blue" (190-260 deg) or "other-colored". The
    /// same chroma floor (30) the legacy highlight gate uses, so the class
    /// shows which rows that gate could even see.
    /// </summary>
    public static (string Class, int Chroma, int Luminance) ClassifyColor(byte r, byte g, byte b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var chroma = max - min;
        var luminance = (int)Math.Round(0.299 * r + 0.587 * g + 0.114 * b);

        if (chroma < 30) return (min >= 235 ? "white" : "neutral", chroma, luminance);

        double hue;
        if (max == r) hue = 60.0 * (((g - b) / (double)chroma + 6.0) % 6.0);
        else if (max == g) hue = 60.0 * ((b - r) / (double)chroma + 2.0);
        else hue = 60.0 * ((r - g) / (double)chroma + 4.0);

        var cls = hue >= 40 && hue <= 75 ? "yellow" : hue >= 190 && hue <= 260 ? "blue" : "other-colored";
        return (cls, chroma, luminance);
    }

    public static string SanitizeSupplier(string? text) =>
        Truncate(Keep(text, c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '.' or '-' or '&'), MaxSupplierChars);

    public static string SanitizeCost(string? text) =>
        Truncate(Keep(text, c => char.IsAsciiDigit(c) || c is '$' or '.' or ',' or '(' or ')' or '-'), MaxCostChars);

    private static string Keep(string? text, Func<char, bool> allowed)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (var c in text) if (allowed(c)) sb.Append(c);
        return sb.ToString().Trim();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max);

    /// <summary>One line per row; unreadable cells print as "(blank)" so a row the app could not read is visible rather than missing.</summary>
    public static string BuildMessage(string header, IReadOnlyList<RowLine> lines)
    {
        // At most MaxRows rows AND never past MaxMessageChars (HQ rejects longer messages).
        var head = $"{(header.Length > 300 ? header.Substring(0, 300) : header)}\n{lines.Count} body row(s) read";
        var sb = new StringBuilder(head);
        var listed = 0;
        foreach (var l in lines.Take(MaxRows))
        {
            var supplier = SanitizeSupplier(l.Supplier);
            var cost = SanitizeCost(l.Cost);
            var line = $"row {l.RowIndex}: supplier={(supplier.Length == 0 ? "(blank)" : supplier)} cost={(cost.Length == 0 ? "(blank)" : cost)} color={l.ColorClass}(c{l.Chroma},l{l.Luminance}) normalized={(l.Normalized ? "yes" : "no")}";
            if (sb.Length + 1 + line.Length > MaxMessageChars - NoteReserve) break;
            sb.Append('\n').Append(line);
            listed++;
        }
        if (listed < lines.Count) sb.Append('\n').Append($"({lines.Count - listed} more row(s) not listed)");
        return sb.ToString();
    }
}
