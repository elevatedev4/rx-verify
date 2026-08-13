using System;
using System.Collections.Generic;
using System.Linq;
using RxVerifyOverlay.Models;

namespace RxVerifyOverlay.OrderAssist.Geometry;

/// <summary>
/// First step of turning a flat OCR word list into a table: buckets words
/// into ROWS by vertical (Y-axis) overlap, independent of any header/body
/// distinction (see HeaderBandLocator for that) or column resolution (see
/// ColumnResolver). Pure and OCR-engine-agnostic — takes plain
/// RxVerifyOverlay.Models.OcrWord (the same DTO the rest of this app's
/// OCR pipeline already produces, see Ocr/OcrModels.cs), no WPF/UIA/Win32
/// dependency, so this is fast to unit-test with synthetic word lists.
///
/// ROW-GROUPING RULE (judgment call — see class doc on OrderAssistCoordinator
/// for why an imperfect heuristic here still fails safe): two words belong
/// to the same row if the second word's TOP is above the first row's
/// currently-accumulated BOTTOM, i.e. any vertical overlap with the row's
/// growing bounding band joins it; a word starting below that band starts
/// a new row. This is the standard approach for reconstructing rows from
/// grid OCR — real spreadsheet-style rows have a visible vertical gap
/// between them (row height &gt; text height), so ordinary text lines
/// never falsely merge into one row this way, while noisy per-glyph OCR
/// jitter within the SAME row (slightly different baselines) still merges
/// correctly since the accumulated band only ever grows.
/// </summary>
public static class TableRowGrouper
{
    /// <summary>
    /// Returns rows top-to-bottom, each a list of words left-to-right.
    /// Words with blank/whitespace-only text (OCR noise) are dropped
    /// entirely before grouping — they carry no information for either
    /// header-label reconstruction or cell-value reading.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<OcrWord>> GroupIntoRows(IReadOnlyList<OcrWord> words)
    {
        var usable = words.Where(w => !string.IsNullOrWhiteSpace(w.Text)).OrderBy(w => w.Y).ToList();
        if (usable.Count == 0) return Array.Empty<IReadOnlyList<OcrWord>>();

        var rows = new List<List<OcrWord>>();
        List<OcrWord>? current = null;
        var currentBottom = double.NegativeInfinity;

        foreach (var word in usable)
        {
            if (current is not null && word.Y < currentBottom)
            {
                current.Add(word);
                currentBottom = Math.Max(currentBottom, word.Y + word.H);
                continue;
            }

            current = new List<OcrWord> { word };
            currentBottom = word.Y + word.H;
            rows.Add(current);
        }

        return rows
            .Select(row => (IReadOnlyList<OcrWord>)row.OrderBy(w => w.X).ToList())
            .ToList();
    }
}
