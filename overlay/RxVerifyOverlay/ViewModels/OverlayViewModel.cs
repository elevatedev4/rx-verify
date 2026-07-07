using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using RxVerifyOverlay.Engine;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.Uia;

namespace RxVerifyOverlay.ViewModels;

/// <summary>
/// One row in the compact table (leading status icon | Field | Source |
/// Entered), in the FIXED field order within its category — never
/// re-sorted. Per the researched Twinlist/WCAG conventions (see
/// MainWindow.xaml row DataTemplate): the status icon leads the row (not
/// a trailing dot), the Source cell stays neutral, and the Entered
/// cell's whole background tints by match state with the icon as the
/// PRIMARY signal and color as reinforcement only.
/// </summary>
public sealed class VerdictRowViewModel
{
    public string FieldKey { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public VerdictStatus Status { get; init; }
    public string Explanation { get; init; } = "";
    public string ReasonCode { get; init; } = "";
    public string SourceValue { get; init; } = "";
    public string EnteredValue { get; init; } = "";

    /// <summary>
    /// Fields whose values are digit sequences where transposed/dropped
    /// digits are the realistic error mode (NPI, phone, quantity,
    /// refills) — these get a monospace/tabular-figure font in the row
    /// template so a transposition is visible as a column-misalignment,
    /// not just a string diff. Everything else (names, addresses, dates,
    /// drug, sig) keeps the default proportional font.
    /// </summary>
    private static readonly HashSet<string> TabularFieldKeys = new()
    {
        "prescriberNpi",
        "prescriberPhone",
        "quantity",
        "refills"
    };

    public bool IsTabularField => TabularFieldKeys.Contains(FieldKey);

    /// <summary>
    /// True only for the drug row, only while its real verdict is still
    /// being looked up in the background (see OverlayViewModel.RefreshAsync's
    /// two-phase refresh + EngineClient.VerifyAsync's skipDrugLookup).
    /// MainWindow.xaml swaps the row's leading glyph for a spinner
    /// (indeterminate ProgressBar) while this is true, instead of
    /// showing the placeholder "!" yellow glyph as if it were a real
    /// "needs a look" verdict — the drug NAME itself (SourceValue/
    /// EnteredValue) is already showing at this point, only the
    /// comparison judgment is still pending.
    /// </summary>
    public bool IsPending => ReasonCode == Models.ReasonCodes.PendingDrugLookup;

    /// <summary>
    /// Glyph for the status — WCAG requires color never be the ONLY
    /// signal, so this is the PRIMARY indicator and cell color is
    /// reinforcement. "!" (not "?") for yellow/uncertain: a question
    /// mark reads as "unknown meaning to the pharmacist", where "!" reads
    /// as "needs a look", which matches the yellow verdict's actual
    /// intent (not_provided/unverified, not necessarily unknown).
    /// </summary>
    public string Glyph => Status switch
    {
        VerdictStatus.Green => "✓",  // ✓
        VerdictStatus.Yellow => "!",
        VerdictStatus.Red => "✗",    // ✗
        _ => "!"
    };

    /// <summary>Row hover/tooltip text — the reason code + explanation move here instead of being always-visible, to keep the compact table small (see MainWindow.xaml).</summary>
    public string TooltipText => string.IsNullOrEmpty(ReasonCode) ? Explanation : $"[{ReasonCode}] {Explanation}";
}

/// <summary>
/// One of the 3 compact-table categories (Patient / Prescriber / Rx —
/// see Models/EngineModels.cs FieldCategories). Status is the
/// worst-status-wins rollup of its Rows (CategoryRollup.RollUp),
/// recomputed by OverlayViewModel every refresh.
/// </summary>
public sealed class CategoryViewModel : INotifyPropertyChanged
{
    public string Name { get; init; } = "";
    public ObservableCollection<VerdictRowViewModel> Rows { get; } = new();

    private VerdictStatus _status = VerdictStatus.Green;
    public VerdictStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Glyph));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(HeaderStatusText));
        }
    }

    /// <summary>
    /// True once this category has at least one row with real data
    /// (i.e. RefreshAsync populated it from a verify() result) — false
    /// while waiting for PioneerRx / before the first successful read.
    /// Per Will's live-test feedback: a category with NO data must render
    /// GRAY, not green — green means "data present AND matches", not
    /// "nothing to complain about yet". See MainWindow.xaml's category
    /// header/box background triggers, which check this BEFORE Status.
    /// </summary>
    private bool _hasData;
    public bool HasData
    {
        get => _hasData;
        set
        {
            if (_hasData == value) return;
            _hasData = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Glyph));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(HeaderStatusText));
        }
    }

    /// <summary>Glyph for the rolled-up category status — same mapping as VerdictRowViewModel.Glyph. Shows a neutral dash when there's no data at all (see HasData).</summary>
    public string Glyph => !HasData
        ? "–"
        : Status switch
        {
            VerdictStatus.Green => "✓",
            VerdictStatus.Yellow => "?",
            VerdictStatus.Red => "✗",
            _ => "?"
        };

    /// <summary>
    /// Text label for the category header spelling out the same
    /// worst-status-wins rollup the glyph/color already convey — per
    /// Will's live-test feedback, the header should show words, not just
    /// a symbol/color. "Match" only when every row is green; "Partial
    /// match" when at least one row is yellow and none are red; "Verify"
    /// when at least one row is red (W-T10 item 3: renamed from "Exact
    /// match"/"Likely Error" respectively). "No data" when the category
    /// has nothing to roll up yet (see HasData).
    /// </summary>
    public string StatusText => !HasData
        ? "No data"
        : Status switch
        {
            VerdictStatus.Green => "Match",
            VerdictStatus.Yellow => "Partial match",
            VerdictStatus.Red => "Verify",
            _ => "Partial match"
        };

    /// <summary>
    /// "— StatusText", for rendering immediately to the right of the
    /// category title on the header row (e.g. "Patient — Exact match") —
    /// per Will's W-T9 item 4 feedback, this must sit right next to the
    /// title (not pinned to the box's right edge) and must NOT be
    /// italic. Kept as its own bindable property (rather than a XAML
    /// StringFormat/MultiBinding) so MainWindow.xaml can bind one
    /// TextBlock directly next to Name with no extra markup.
    /// </summary>
    public string HeaderStatusText => $"— {StatusText}";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Pure worst-status-wins rollup: Red beats Yellow beats Green. Kept as a
/// standalone static method (no ViewModel/UIA dependencies) so it's
/// directly unit-testable — see Tests/CategoryRollupTests.cs.
/// </summary>
public static class CategoryRollup
{
    public static VerdictStatus RollUp(IEnumerable<VerdictStatus> statuses)
    {
        var hasRed = false;
        var hasYellow = false;
        foreach (var status in statuses)
        {
            if (status == VerdictStatus.Red) hasRed = true;
            else if (status == VerdictStatus.Yellow) hasYellow = true;
        }

        if (hasRed) return VerdictStatus.Red;
        if (hasYellow) return VerdictStatus.Yellow;
        return VerdictStatus.Green;
    }
}

/// <summary>
/// Orchestrates: attach to PioneerRx -> read both panels via FieldReader
/// -> call the engine via EngineClient -> expose the 3 rolled-up
/// categories (Patient/Prescriber/Rx — see Models/EngineModels.cs
/// FieldCategories) for MainWindow's compact table to bind to. This is
/// the only place that combines all three pieces, so the overlay UI
/// itself stays a thin renderer.
/// </summary>
public sealed class OverlayViewModel : INotifyPropertyChanged
{
    private readonly EngineClient _engineClient;

    /// <summary>The 3 categories, always in FieldCategories.Order — MainWindow.xaml binds directly to this.</summary>
    public ObservableCollection<CategoryViewModel> Categories { get; } = new();

    private string _statusMessage = "Not attached to PioneerRx yet.";
    public string StatusMessage
    {
        get => _statusMessage;
        private set { _statusMessage = value; OnPropertyChanged(); }
    }

    private int _greenCount;
    public int GreenCount { get => _greenCount; private set { _greenCount = value; OnPropertyChanged(); } }

    private int _yellowCount;
    public int YellowCount { get => _yellowCount; private set { _yellowCount = value; OnPropertyChanged(); } }

    private int _redCount;
    public int RedCount { get => _redCount; private set { _redCount = value; OnPropertyChanged(); } }

    /// <summary>
    /// Monotonic counter, bumped at the START of every RefreshAsync call.
    /// The background drug-lookup phase (RefreshDrugFieldAsync) captures
    /// the value current at its own start and checks it again before
    /// applying its result — if a NEWER refresh (e.g. the pharmacist hit
    /// Refresh again, or PioneerRx moved to a different Rx) has started
    /// in the meantime, the stale drug result is silently dropped instead
    /// of being written into rows that no longer belong to it.
    /// </summary>
    private int _refreshGeneration;

    public OverlayViewModel(EngineClient engineClient)
    {
        _engineClient = engineClient;

        // Categories are created once, in fixed display order, and their
        // Rows are cleared/repopulated on every refresh below — never
        // recreated, so MainWindow's binding to Categories itself never
        // needs to change, only the items inside it.
        foreach (var name in FieldCategories.Order)
        {
            Categories.Add(new CategoryViewModel { Name = name });
        }
    }

    /// <summary>
    /// One full refresh pass: find the window, read both panels, call
    /// the engine, update the bound rows. Safe to call repeatedly (e.g.
    /// on a timer or a manual "Refresh" button) — every failure mode
    /// (window not found, UIA read error, engine error) becomes a
    /// StatusMessage rather than an exception, so the overlay never
    /// crashes mid-shift.
    ///
    /// TWO-PHASE (per Will's live-test feedback: a noticeable delay after
    /// clicking Refresh before ANYTHING updated). Name/DOB/address/
    /// prescriber/sig/quantity/refills comparisons are cheap string/date/
    /// number logic; only the drug field's identity lookup is slow (it
    /// consults the bundled ~130k-concept local NDC dataset — see
    /// rx-verify src/drug/index.ts LocalNdcProvider). So:
    ///   Phase 1 (awaited, blocks this method briefly): call the engine
    ///     with skipDrugLookup=true. This never touches the NDC dataset
    ///     at all (see EngineClient.VerifyAsync / src/cli.ts), so it
    ///     returns fast. Every field except drug gets its real verdict;
    ///     the drug row shows its name/value immediately with a PENDING
    ///     indicator (spinner) instead of a verdict glyph.
    ///   Phase 2 (fire-and-forget, does NOT block this method or the UI
    ///     thread): the real drug lookup, via RefreshDrugFieldAsync. When
    ///     it resolves, only the drug row (and its category's rollup +
    ///     the overall summary) is updated in place.
    /// </summary>
    public async Task RefreshAsync()
    {
        var generation = ++_refreshGeneration;

        using var window = PioneerRxWindow.TryAttach();
        if (window is null)
        {
            StatusMessage = "Waiting for a PioneerRx Pre-Check/Edit/New Rx window...";
            ClearCategories();
            UpdateSummary(null);
            return;
        }

        FieldReader reader;
        PrescriptionRecord entered;
        PrescriptionRecord source;
        try
        {
            reader = new FieldReader(window);
            entered = reader.ReadEntered();
            source = reader.ReadSource();
        }
        catch (Exception ex)
        {
            StatusMessage = $"UIA read failed: {ex.Message}. Try 'Dump UIA Tree' to diagnose.";
            return;
        }

        if (!reader.IsStructuredSourceAvailable(source))
        {
            // Replaces the old (wrong) fax/scanned-image heuristic: the
            // real gate is whether the Escript tab's UIA tree
            // (ux10Dot6Escript) is present and parses to a patient+drug —
            // see Uia/FieldReader.cs ReadSource/IsStructuredSourceAvailable.
            StatusMessage = reader.SourceUnavailableReason ?? "Open the Escript tab to verify this e-script.";
            ClearCategories();
            UpdateSummary(null);
            return;
        }

        // Phase 1: fast pass, skips the drug lookup entirely.
        var fastResult = await _engineClient.VerifyAsync(source, entered, skipDrugLookup: true);

        if (generation != _refreshGeneration) return; // superseded by a newer refresh while we were awaiting

        if (!string.IsNullOrEmpty(fastResult.Error))
        {
            StatusMessage = fastResult.Error;
            return;
        }

        PopulateRows(fastResult);
        UpdateSummary(fastResult.Summary);
        StatusMessage = $"Last checked {DateTime.Now:h:mm:ss tt}. Drug lookup running…";

        // Phase 2: NOT awaited — runs in the background so this method
        // (and whatever caller triggered it, e.g. the Refresh button
        // click handler) returns immediately. See RefreshDrugFieldAsync
        // for the staleness guard against a newer refresh superseding
        // this one before it resolves.
        _ = RefreshDrugFieldAsync(source, entered, generation);
    }

    /// <summary>
    /// Last screen signature observed by WatchAsync (see below) — null
    /// until the first watch tick. Compared against the current tick's
    /// signature to decide whether a full RefreshAsync is warranted.
    /// </summary>
    private PioneerRxWindow.ScreenSignature? _lastWatchedSignature;

    /// <summary>
    /// AUTO-WATCH (W-T9 item 5): replaces the old fixed-interval "just
    /// re-run the full verify every 5s" polling with cheap
    /// change-detection. Call this on a short timer (MainWindow.xaml.cs
    /// uses ~1s) instead of RefreshAsync directly.
    ///
    /// Each tick calls PioneerRxWindow.GetScreenSignature(), which is
    /// drastically cheaper than a full RefreshAsync: it only enumerates
    /// top-level desktop windows and reads ONE window's title text (no
    /// FieldReader panel walk, no Escript tree read, no engine subprocess
    /// call) — see PioneerRxWindow.GetScreenSignature for how the Rx
    /// number is parsed straight out of the title
    /// ("Edit Rx - &lt;rx number&gt; - ...").
    ///
    /// A full RefreshAsync only actually runs when:
    ///   - the pre-check/edit/new-rx screen just appeared (wasn't present
    ///     last tick), or
    ///   - it's present but the Rx number/title changed since last tick
    ///     (pharmacist moved to a different Rx).
    /// If the screen disappeared since last tick, the categories are
    /// cleared (mirrors RefreshAsync's own "window not found" branch)
    /// without needing a full refresh. If nothing changed, this is a
    /// no-op beyond the cheap signature read — no engine call, no UIA
    /// panel read, so it's safe to poll frequently without hammering
    /// PioneerRx or the machine.
    /// </summary>
    public async Task WatchAsync()
    {
        var signature = PioneerRxWindow.GetScreenSignature();
        var previous = _lastWatchedSignature;
        _lastWatchedSignature = signature;

        if (!signature.Present)
        {
            if (previous is { Present: true })
            {
                StatusMessage = "Waiting for a PioneerRx Pre-Check/Edit/New Rx window...";
                ClearCategories();
                UpdateSummary(null);
            }
            return;
        }

        var changed = previous is null || !previous.Value.Present || previous.Value.RxNumber != signature.RxNumber;
        if (!changed) return;

        await RefreshAsync();
    }

    /// <summary>
    /// Rebuilds every category's Rows from a VerifyResult, in
    /// FieldOrder.Fields order within each category. Used by Phase 1 of
    /// RefreshAsync to populate every row; Phase 2 (RefreshDrugFieldAsync)
    /// only ever replaces the single "drug" row, but goes through the
    /// same BuildRow helper below so the two phases can never drift apart
    /// on how a FieldVerdict becomes a VerdictRowViewModel.
    /// </summary>
    private void PopulateRows(VerifyResult result)
    {
        foreach (var category in Categories) category.Rows.Clear();

        foreach (var field in FieldOrder.Fields)
        {
            var verdict = result.Verdicts.FirstOrDefault(v => v.Field == field);
            if (verdict is null) continue; // defensive: engine contract guarantees all 12 fields, but never crash the UI on a contract drift

            var categoryName = FieldCategories.CategoryByField.TryGetValue(field, out var mapped)
                ? mapped
                : FieldCategories.Rx; // defensive fallback for a future field the engine adds that this map hasn't been updated for yet
            var category = Categories.First(c => c.Name == categoryName);

            category.Rows.Add(BuildRow(field, verdict));
        }

        foreach (var category in Categories)
        {
            RollUpCategory(category);
            category.HasData = category.Rows.Count > 0;
        }
    }

    /// <summary>
    /// Recomputes one category's rolled-up Status from its current Rows,
    /// excluding any row whose field is in FieldCategories.
    /// RollupExcludedFields (currently patientAddress/prescriberAddress)
    /// from the rollup INPUT — those rows stay visible in the table, they
    /// just can never move the category's header status. Shared by
    /// PopulateRows (full refresh) and RefreshDrugFieldAsync (drug-only
    /// refresh) so the exclusion rule can't drift between the two.
    /// </summary>
    private static void RollUpCategory(CategoryViewModel category)
    {
        var rollupStatuses = category.Rows
            .Where(r => !FieldCategories.RollupExcludedFields.Contains(r.FieldKey))
            .Select(r => r.Status);
        category.Status = CategoryRollup.RollUp(rollupStatuses);
    }

    private static VerdictRowViewModel BuildRow(string field, FieldVerdict verdict) => new()
    {
        FieldKey = field,
        DisplayName = FieldOrder.DisplayNames[field],
        Status = verdict.Status,
        Explanation = verdict.Explanation,
        ReasonCode = verdict.ReasonCode,
        SourceValue = verdict.SourceValue ?? "(not provided)",
        EnteredValue = verdict.EnteredValue ?? "(not provided)"
    };

    /// <summary>
    /// Phase 2 of RefreshAsync: the real (slow) drug-identity lookup,
    /// run in the background. Re-runs a full verify() (skipDrugLookup
    /// false/omitted) rather than adding a third "drug-only" CLI mode —
    /// the non-drug comparisons are cheap enough that recomputing them
    /// costs nothing measurable, and reusing the exact same engine call
    /// shape keeps this file's engine contract to just the one
    /// skipDrugLookup flag. Only the "drug" row (and its category
    /// rollup + overall summary) from this second result is ever
    /// applied — every other row already rendered in Phase 1 is left
    /// untouched, so the pharmacist never sees the rest of the panel
    /// flicker or reset while this runs.
    /// </summary>
    private async Task RefreshDrugFieldAsync(PrescriptionRecord source, PrescriptionRecord entered, int generation)
    {
        VerifyResult result;
        try
        {
            result = await _engineClient.VerifyAsync(source, entered, skipDrugLookup: false);
        }
        catch (Exception ex)
        {
            if (generation == _refreshGeneration) StatusMessage = $"Drug lookup failed: {ex.Message}";
            return;
        }

        if (generation != _refreshGeneration) return; // a newer refresh superseded this one — drop the stale result

        if (!string.IsNullOrEmpty(result.Error))
        {
            StatusMessage = result.Error;
            return;
        }

        var drugVerdict = result.Verdicts.FirstOrDefault(v => v.Field == "drug");
        if (drugVerdict is null) return;

        var drugCategoryName = FieldCategories.CategoryByField.TryGetValue("drug", out var mapped) ? mapped : FieldCategories.Rx;
        var drugCategory = Categories.FirstOrDefault(c => c.Name == drugCategoryName);
        if (drugCategory is null) return;

        var existingIndex = -1;
        for (var i = 0; i < drugCategory.Rows.Count; i++)
        {
            if (drugCategory.Rows[i].FieldKey == "drug") { existingIndex = i; break; }
        }
        if (existingIndex < 0) return; // the panel was cleared (e.g. window lost) before this resolved — nothing to update

        // Replacing (not mutating) the row is required: VerdictRowViewModel's
        // properties are init-only / not INotifyPropertyChanged, so a
        // fresh instance in the ObservableCollection is what actually
        // triggers the DataTemplate to re-render this row's glyph/colors.
        drugCategory.Rows[existingIndex] = BuildRow("drug", drugVerdict);

        RollUpCategory(drugCategory);
        UpdateSummary(result.Summary);
        StatusMessage = $"Last checked {DateTime.Now:h:mm:ss tt}.";
    }

    /// <summary>Clears every category's rows (leaving the 3 category shells in place) — used by every early-return branch of RefreshAsync.</summary>
    private void ClearCategories()
    {
        foreach (var category in Categories)
        {
            category.Rows.Clear();
            category.Status = VerdictStatus.Green; // Status is meaningless with no data; HasData=false is what actually drives the gray "no data" display (see MainWindow.xaml).
            category.HasData = false;
        }
    }

    /// <summary>
    /// Debug helper: dumps the full UIA tree of the currently-attached
    /// PioneerRx window as plain text, for Will to diff against
    /// FieldMap.cs. Returns null (with a StatusMessage explaining why)
    /// if no window is currently attached.
    /// </summary>
    public string? DumpCurrentWindowTree()
    {
        using var window = PioneerRxWindow.TryAttach();
        if (window is null)
        {
            StatusMessage = "No PioneerRx window found to dump.";
            return null;
        }

        var walker = new UiaTreeWalker(window.WindowElement);
        return walker.DumpTree();
    }

    private void UpdateSummary(VerifySummary? summary)
    {
        GreenCount = summary?.Green ?? 0;
        YellowCount = summary?.Yellow ?? 0;
        RedCount = summary?.Red ?? 0;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
