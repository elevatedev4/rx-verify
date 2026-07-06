using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using RxVerifyOverlay.Engine;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.Uia;

namespace RxVerifyOverlay.ViewModels;

/// <summary>One row in the compact table (Field | Source | Entered | status dot), in the FIXED field order within its category — never re-sorted.</summary>
public sealed class VerdictRowViewModel
{
    public string FieldKey { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public VerdictStatus Status { get; init; }
    public string Explanation { get; init; } = "";
    public string ReasonCode { get; init; } = "";
    public string SourceValue { get; init; } = "";
    public string EnteredValue { get; init; } = "";

    /// <summary>Glyph for the status, since color alone shouldn't carry all the meaning (accessibility).</summary>
    public string Glyph => Status switch
    {
        VerdictStatus.Green => "✓",  // ✓
        VerdictStatus.Yellow => "?",
        VerdictStatus.Red => "✗",    // ✗
        _ => "?"
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
        }
    }

    /// <summary>Glyph for the rolled-up category status — same mapping as VerdictRowViewModel.Glyph.</summary>
    public string Glyph => Status switch
    {
        VerdictStatus.Green => "✓",
        VerdictStatus.Yellow => "?",
        VerdictStatus.Red => "✗",
        _ => "?"
    };

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
    /// </summary>
    public async Task RefreshAsync()
    {
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

        var result = await _engineClient.VerifyAsync(source, entered);

        if (!string.IsNullOrEmpty(result.Error))
        {
            StatusMessage = result.Error;
            return;
        }

        foreach (var category in Categories) category.Rows.Clear();

        foreach (var field in FieldOrder.Fields)
        {
            var verdict = result.Verdicts.FirstOrDefault(v => v.Field == field);
            if (verdict is null) continue; // defensive: engine contract guarantees all 10 fields, but never crash the UI on a contract drift

            var categoryName = FieldCategories.CategoryByField.TryGetValue(field, out var mapped)
                ? mapped
                : FieldCategories.Rx; // defensive fallback for a future field the engine adds that this map hasn't been updated for yet
            var category = Categories.First(c => c.Name == categoryName);

            category.Rows.Add(new VerdictRowViewModel
            {
                FieldKey = field,
                DisplayName = FieldOrder.DisplayNames[field],
                Status = verdict.Status,
                Explanation = verdict.Explanation,
                ReasonCode = verdict.ReasonCode,
                SourceValue = verdict.SourceValue ?? "(not provided)",
                EnteredValue = verdict.EnteredValue ?? "(not provided)"
            });
        }

        foreach (var category in Categories)
        {
            category.Status = CategoryRollup.RollUp(category.Rows.Select(r => r.Status));
        }

        UpdateSummary(result.Summary);
        StatusMessage = $"Last checked {DateTime.Now:h:mm:ss tt}.";
    }

    /// <summary>Clears every category's rows (leaving the 3 category shells in place) — used by every early-return branch of RefreshAsync.</summary>
    private void ClearCategories()
    {
        foreach (var category in Categories)
        {
            category.Rows.Clear();
            category.Status = VerdictStatus.Green; // neutral/no-data — nothing to roll up
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
