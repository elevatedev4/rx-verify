using RxVerifyOverlay.OrderAssist.Parsing;

namespace RxVerifyOverlay.OrderAssist.Decisions;

/// <summary>Result of classifying one Order Quantity cell's OCR'd text — see ZeroQuantityDetector.Classify.</summary>
public enum ZeroCellState
{
    /// <summary>Parsed as a number and it's exactly zero — the owner's spec case to highlight red.</summary>
    Zero,

    /// <summary>Parsed as a number and it's non-zero — never highlighted.</summary>
    NonZero,

    /// <summary>
    /// Blank cell, OCR noise, or genuinely non-numeric text — deliberately
    /// NEVER treated the same as Zero (JUDGMENT CALL, flagged in the
    /// branch report): a cell OrderAssist failed to read is a reason to
    /// distrust that tick's OCR pass, not a reason to flash a false-alarm
    /// red box a pharmacist would learn to distrust. The next ~1s tick
    /// gets another chance at a clean read.
    /// </summary>
    Unknown
}

/// <summary>
/// The owner's "Order Quantity column, nothing should be 0" rule, applied
/// to one already-resolved cell's text (see Geometry/CellValueBucketizer.cs
/// for how that text is extracted from the Order Quantity column
/// specifically, never "Suggested Order Qty" — see ColumnResolver's
/// "substring trap" doc).
/// </summary>
public static class ZeroQuantityDetector
{
    public static ZeroCellState Classify(string? cellText)
    {
        var parsed = CurrencyParser.Parse(cellText);
        if (parsed is null) return ZeroCellState.Unknown;
        return parsed.Value == 0m ? ZeroCellState.Zero : ZeroCellState.NonZero;
    }
}
