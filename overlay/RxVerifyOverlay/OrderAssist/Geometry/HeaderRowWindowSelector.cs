using System;
using System.Collections.Generic;
using System.Linq;
using RxVerifyOverlay.Models;

namespace RxVerifyOverlay.OrderAssist.Geometry;

/// <summary>
/// Finds WHICH leading row(s) of a table are actually the real grid header
/// — replacing the old, much narrower assumption that "the header is
/// whatever HeaderBandLocator.CountHeaderRows (capped at 2) finds at the
/// very top of the table".
///
/// ROOT CAUSE (W-T76/78/81, "nothing highlights" — confirmed against
/// Will's real diagnostic log, 2026-08-18): both Order Assist target
/// windows are captured FULL-WINDOW (OrderAssistCoordinator.TickAsync
/// captures the whole target.Value.Bounds — this part is correct, the
/// grid header IS inside the captured region). But PioneerRx's own window
/// chrome above the actual grid — the window's TITLE BAR text and its OWN
/// menu bar/toolbar/filter row(s) — reliably produced MORE than 2 leading
/// "doesn't look like data" rows before the real header row (title bar
/// alone, PLUS a menu-bar row like "Actions | Tools | Search | Reports |
/// Analysis", PLUS on the Catalog Substitution window a filter/toolbar row
/// like "Catalog Items Filter: All"). The old code (HeaderBandLocator.
/// CountHeaderRows(rows) with its default maxHeaderRows=2, then
/// ColumnResolver.BuildPartitionedColumnBands on ALL of those leading rows
/// COMBINED) always exhausted its 2-row budget on pure chrome and never
/// reached the real header row at all — worse, X-clustering title-bar
/// words together with menu-bar words (two UNRELATED lines that happen to
/// share horizontal extent) produced exactly the garbled, Y-then-X-
/// reordered "shuffled title" bands Will's log showed (e.g. "Order Catalog
/// Substitution Recommended Item Selection" — a scrambled recombination of
/// "Recommended Order - Catalog Item Substitution Selection", the real
/// window title, wrapped/read across more than one OCR line).
///
/// FIX: don't assume the header starts at row 0 and don't cap the search
/// at 2 rows. Treat EVERY leading non-data row (HeaderBandLocator.IsDataRow
/// == false — the moment a genuine data row appears, nothing after it
/// could still be "the header", so the scan stops there, same structural
/// assumption CountHeaderRows already made, just applied per candidate
/// start instead of as one single global cutoff) as a possible header
/// START, try it alone AND paired with the row immediately below it (for a
/// 2-line-wrapped header, e.g. "Suggested" / "Order Qty" — same
/// accommodation the old maxHeaderRows=2 cap existed for), build column
/// bands for EACH candidate independently, and SCORE each one by how many
/// of the caller's expected column labels it actually contains. The
/// highest-scoring candidate wins. A pure chrome row (title/menu/filter)
/// scores 0 against real column labels and can never win once the genuine
/// header row is anywhere within the scan range — it doesn't matter how
/// many chrome rows come before it.
///
/// FAIL-SAFE PROPERTY PRESERVED: this only changes WHICH rows feed
/// ColumnResolver.BuildPartitionedColumnBands — the actual accept/reject
/// decision for a specific column is still ColumnResolver.ResolveExact's
/// unchanged, strict, case-insensitive EQUALITY match (see that class's
/// own "substring trap" doc). LabelsAreCloseMatch below is used ONLY to
/// SCORE/select which row-window looks most header-shaped; it is never
/// used to accept a column for highlighting, so a near-miss OCR misread
/// can at most cause this selector to correctly find the right rows —
/// ResolveExact afterward still requires an exact match before anything is
/// ever drawn.
/// </summary>
public static class HeaderRowWindowSelector
{
    /// <summary>
    /// How many leading rows to search before giving up — generous on
    /// purpose (the old effective limit was 2): title bar + app menu bar +
    /// a filter/toolbar row is already 3 rows of pure chrome on the
    /// Catalog Substitution window per Will's own log, and this errs
    /// toward "keep scanning" rather than risk under-shooting again on a
    /// window with even more chrome. Cheap either way — this runs once per
    /// ~1s Order Assist tick, not per frame, and stops immediately at the
    /// first genuine data row regardless of this cap.
    /// </summary>
    public const int DefaultMaxRowsToScan = 12;

    /// <summary>2 supports a header wrapped across at most 2 visual lines — same accommodation the old HeaderBandLocator maxHeaderRows default existed for (e.g. "Suggested" over "Order Qty").</summary>
    private const int MaxRowSpan = 2;

    private const int MaxNearMatchEditDistance = 1;

    /// <summary>Never near-match-tolerate a short label — a 1-character edit distance on something 5 characters or shorter risks colliding with a genuinely different short word; every real target label here ("Supplier" is the shortest) is well above this.</summary>
    private const int MinLabelLengthForNearMatch = 6;

    /// <summary>One candidate header-row window: which source rows it spans, the column bands built from JUST those rows, its own vertical extent (for diagnostics — see OrderAssistCoordinator.LogColumnDiagnosticsIfNeeded), and how many of the caller's expected labels it matched.</summary>
    public sealed record Candidate(int StartRowIndex, int RowCount, IReadOnlyList<ColumnBand> Bands, double Top, double Bottom, int Score);

    /// <summary>
    /// Every candidate window this selector considered, in scan order —
    /// used by SelectBest below AND by OrderAssistCoordinator's diagnostic
    /// logging (branch brief item 4: "log ALL candidate bands ... not just
    /// the winning one"), so a future failure paste shows every row-window
    /// that was tried and why each one lost, not just the final answer.
    /// </summary>
    public static IReadOnlyList<Candidate> EnumerateCandidates(
        IReadOnlyList<IReadOnlyList<OcrWord>> rows,
        IReadOnlyList<string> expectedLabels,
        int maxRowsToScan = DefaultMaxRowsToScan)
    {
        var candidates = new List<Candidate>();
        var scanLimit = Math.Min(rows.Count, maxRowsToScan);

        for (var start = 0; start < scanLimit; start++)
        {
            // A genuine data row can never be (part of) the header — and
            // nothing below it in a well-formed grid could be either, so
            // stop scanning entirely rather than just skipping this row.
            if (HeaderBandLocator.IsDataRow(rows[start])) break;

            for (var span = 1; span <= MaxRowSpan && start + span <= scanLimit; span++)
            {
                if (span > 1 && HeaderBandLocator.IsDataRow(rows[start + span - 1])) break;

                var window = rows.Skip(start).Take(span).ToList();
                var bands = ColumnResolver.BuildPartitionedColumnBands(window);
                var score = ScoreBands(bands, expectedLabels);

                var allWords = window.SelectMany(r => r).Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
                var top = allWords.Count > 0 ? allWords.Min(w => w.Y) : 0;
                var bottom = allWords.Count > 0 ? allWords.Max(w => w.Y + w.H) : 0;

                candidates.Add(new Candidate(start, span, bands, top, bottom, score));
            }
        }

        return candidates;
    }

    /// <summary>
    /// The single best candidate — highest score first (ties broken by:
    /// topmost start row, then the LARGER row span, so a fully-reconstructed
    /// 2-line-wrapped header is preferred over a 1-line partial read of the
    /// exact same row when both happen to match the expected labels
    /// equally well). Null if nothing scored above 0 — same "degrade to no
    /// highlight, never guess" fail-safe as the old code's headerRowCount
    /// == 0 check.
    /// </summary>
    public static Candidate? SelectBest(
        IReadOnlyList<IReadOnlyList<OcrWord>> rows,
        IReadOnlyList<string> expectedLabels,
        int maxRowsToScan = DefaultMaxRowsToScan) =>
        PickBest(EnumerateCandidates(rows, expectedLabels, maxRowsToScan));

    /// <summary>Picks the winner out of an already-built candidate list — split out from SelectBest so OrderAssistCoordinator's diagnostic can enumerate once and both display every candidate AND identify the winner among them, without scanning twice.</summary>
    public static Candidate? PickBest(IReadOnlyList<Candidate> candidates) =>
        candidates
            .Where(c => c.Score > 0)
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.StartRowIndex)
            .ThenByDescending(c => c.RowCount)
            .FirstOrDefault();

    private static int ScoreBands(IReadOnlyList<ColumnBand> bands, IReadOnlyList<string> expectedLabels) =>
        expectedLabels.Count(expected => bands.Any(b => LabelsAreCloseMatch(b.Label, expected)));

    /// <summary>
    /// SCORING-ONLY tolerance (see class doc's FAIL-SAFE PROPERTY
    /// PRESERVED section — this never drives an actual column-accept
    /// decision, only which row-window looks most header-shaped): exact
    /// match after whitespace/case normalization (identical to
    /// ColumnResolver's own NormalizeLabel), OR — branch brief item 3,
    /// "tolerate OCR noise (minor misreads)" — a Levenshtein edit distance
    /// of at most 1 on labels long enough (6+ normalized characters) that
    /// a single-character difference can't plausibly collide with an
    /// unrelated real column name.
    /// </summary>
    public static bool LabelsAreCloseMatch(string bandLabel, string expectedLabel)
    {
        var a = NormalizeLabel(bandLabel);
        var b = NormalizeLabel(expectedLabel);
        if (a == b) return true;
        if (a.Length < MinLabelLengthForNearMatch || b.Length < MinLabelLengthForNearMatch) return false;

        return LevenshteinDistance(a, b) <= MaxNearMatchEditDistance;
    }

    private static string NormalizeLabel(string label) =>
        string.Join(" ", label.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    /// <summary>Classic O(len(a)*len(b)) DP edit distance — internal, tiny inputs (single header labels, never a whole OCR blob), no external dependency needed.</summary>
    internal static int LevenshteinDistance(string a, string b)
    {
        var dp = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) dp[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) dp[0, j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                dp[i, j] = Math.Min(Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1), dp[i - 1, j - 1] + cost);
            }
        }

        return dp[a.Length, b.Length];
    }
}
