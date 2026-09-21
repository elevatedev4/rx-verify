using System;
using System.Collections.Generic;
using System.Linq;
using RxVerifyOverlay.Models;

namespace RxVerifyOverlay.Reports;

/// <summary>One reconstructed visual line of OCR words (grouped by Y, ordered left-to-right by X) — the intermediate shape ReportGridOcrMatcher builds before restricting/matching against the "Report Name" column.</summary>
public readonly record struct OcrLine(double Y, IReadOnlyList<OcrWord> Words);

/// <summary>A matched report-name line's bounding box, relative to the CAPTURED REGION (same coordinate space as the OcrWords it was built from — see WindowsMediaOcrEngine's own doc on this) — PioneerReportDriver adds the region's screen offset before clicking.</summary>
public readonly record struct OcrRowMatch(double X, double Y, double Width, double Height)
{
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
}

/// <summary>
/// Review fix (PR #11 blocker): PioneerReportDriver.TryOcrRowSelect
/// captures the work-area rect once, before the OCR await — if Pioneer's
/// window moved/resized while RecognizeAsync was running, that rect is
/// stale. Pure comparison of the "before" vs "after" rect (X/Y/Width/
/// Height individually, not a single combined check, so a test can
/// exercise each dimension) — the driver re-reads BoundingRectangle after
/// the await and aborts/retries once whenever this reports true, rather
/// than trusting the pre-await rect for the click.
/// </summary>
public static class WorkAreaStability
{
    public static bool HasMoved(
        int originalX, int originalY, int originalWidth, int originalHeight,
        int currentX, int currentY, int currentWidth, int currentHeight) =>
        originalX != currentX || originalY != currentY || originalWidth != currentWidth || originalHeight != currentHeight;
}

/// <summary>
/// Review fix (PR #11 blocker): OcrRowMatch's coordinates are in the
/// CAPTURED BITMAP's own pixel space. Windows.Media.Ocr
/// (WindowsMediaOcrEngine) already divides its own internal upscale back
/// out before returning OcrTextResult.Words, so in the ordinary case the
/// bitmap's pixel dimensions equal the AutomationElement rect passed to
/// EscriptImageCapture.CaptureRegion 1:1 and this scale is 1.0/1.0 — this
/// mapping exists for the rarer case where they DON'T match (a
/// DPI-aware/scaled capture path returning a bitmap whose pixel size
/// differs from the rect's own logical size), so a >1 mismatch there
/// can't silently double-click a point offset from the matched row.
/// </summary>
public static class OcrCaptureScale
{
    /// <summary>(regionWidth/bitmapWidth, regionHeight/bitmapHeight) — 1.0/1.0 when they already match; falls back to 1.0 for either axis if the bitmap dimension is zero (can't divide, and a zero-size bitmap means OCR already found nothing).</summary>
    public static (double ScaleX, double ScaleY) ComputeScale(int regionWidth, int regionHeight, int bitmapWidth, int bitmapHeight)
    {
        var scaleX = bitmapWidth > 0 ? (double)regionWidth / bitmapWidth : 1.0;
        var scaleY = bitmapHeight > 0 ? (double)regionHeight / bitmapHeight : 1.0;
        return (scaleX, scaleY);
    }

    /// <summary>Maps an OCR-relative point (bitmap pixel space, as OcrRowMatch.CenterX/CenterY already are) to a screen point: scale into the capture region's own coordinate space, then add the region's screen-space origin.</summary>
    public static (double X, double Y) ToScreenPoint(double ocrX, double ocrY, double scaleX, double scaleY, int regionLeft, int regionTop) =>
        (regionLeft + ocrX * scaleX, regionTop + ocrY * scaleY);
}

/// <summary>
/// GOAL brief step 2c (OCR row-selection strategy): pure matching over
/// the OCR engine's already-recognized Words — no OCR engine, no Bitmap,
/// no capture involved here at all, so every shape (line grouping, header
/// column detection, exact-vs-collision matching) is unit testable with
/// hand-built OcrWord lists mimicking the owner's screenshot grid (see
/// RxVerifyOverlay.Tests/Reports/ReportGridOcrMatcherTests.cs).
/// PioneerReportDriver.TryOcrRowSelect is the only (impure) caller —
/// captures the FinancialReportsWorkArea pane, runs it through the
/// existing IOcrEngine (Ocr/WindowsMediaOcrEngine.cs — no new OCR
/// dependency), and hands the resulting Words straight to this class.
/// </summary>
public static class ReportGridOcrMatcher
{
    /// <summary>Words whose vertical center falls within this many pixels of a line's running average Y are considered the same visual line — generous relative to the owner's screenshot (~12px row height at 100% scaling), since OCR word boxes can jitter a few pixels within one printed row.</summary>
    private const double DefaultLineYTolerance = 6.0;

    /// <summary>Groups words into visual lines by Y-proximity, each line's words ordered left-to-right by X. Pure clustering — no assumption about row height beyond <paramref name="yTolerance"/>.</summary>
    public static IReadOnlyList<OcrLine> GroupIntoLines(IReadOnlyList<OcrWord> words, double yTolerance = DefaultLineYTolerance)
    {
        var usable = words.Where(w => !string.IsNullOrWhiteSpace(w.Text)).OrderBy(w => w.Y).ToList();
        var buckets = new List<List<OcrWord>>();

        foreach (var word in usable)
        {
            var wordCenterY = word.Y + word.H / 2;
            var bucket = buckets.FirstOrDefault(b =>
            {
                var bucketCenterY = b.Average(w => w.Y + w.H / 2);
                return Math.Abs(bucketCenterY - wordCenterY) <= yTolerance;
            });

            if (bucket is null)
            {
                bucket = new List<OcrWord>();
                buckets.Add(bucket);
            }

            bucket.Add(word);
        }

        return buckets
            .Select(b => new OcrLine(b.Average(w => w.Y), b.OrderBy(w => w.X).ToList()))
            .OrderBy(l => l.Y)
            .ToList();
    }

    /// <summary>
    /// Finds the "Report Name" / "Report Description" header line and
    /// returns the X position where the "Report Description" column
    /// starts (i.e. the "Report" word of that header pair) — everything
    /// with X strictly less than this belongs to the Report Name column.
    /// Null if the header row wasn't recognized at all (OCR miss, or the
    /// captured region didn't include the header) — callers fall back to
    /// considering the whole line, which is still safe because matching
    /// is always exact-whole-string (see ReportRowTextMatcher).
    /// </summary>
    public static double? FindReportDescriptionColumnX(IReadOnlyList<OcrWord> words)
    {
        foreach (var line in GroupIntoLines(words))
        {
            for (var i = 0; i < line.Words.Count - 1; i++)
            {
                if (string.Equals(line.Words[i].Text, "Report", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(line.Words[i + 1].Text, "Description", StringComparison.OrdinalIgnoreCase))
                {
                    return line.Words[i].X;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The core matching rule (GOAL brief: "require the whole name to
    /// match, so 'Inventory Valuation' must not pick 'Inventory Valuation
    /// (With Last Supplier)' or 'Inventory Valuation for Excel' — choose
    /// exact line equality on the Report Name column region, i.e. only
    /// consider words left of the 'Report Description' column header
    /// x-position"). Returns the matched line's bounding box (relative to
    /// the captured region) or null if no line's Report-Name-column text
    /// equals <paramref name="targetReportName"/> exactly.
    /// </summary>
    public static OcrRowMatch? FindReportNameLine(IReadOnlyList<OcrWord> words, string targetReportName)
    {
        if (string.IsNullOrWhiteSpace(targetReportName)) return null;

        var columnBoundaryX = FindReportDescriptionColumnX(words);

        foreach (var line in GroupIntoLines(words))
        {
            var nameWords = (columnBoundaryX is { } boundary
                    ? line.Words.Where(w => w.X < boundary)
                    : line.Words)
                .ToList();

            if (nameWords.Count == 0) continue;

            var lineText = string.Join(" ", nameWords.Select(w => w.Text));
            if (!ReportRowTextMatcher.IsExactMatch(lineText, targetReportName)) continue;

            var minX = nameWords.Min(w => w.X);
            var maxX = nameWords.Max(w => w.X + w.W);
            var minY = nameWords.Min(w => w.Y);
            var maxY = nameWords.Max(w => w.Y + w.H);

            return new OcrRowMatch(minX, minY, maxX - minX, maxY - minY);
        }

        return null;
    }
}
