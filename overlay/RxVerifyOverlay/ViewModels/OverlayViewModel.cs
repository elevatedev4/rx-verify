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

/// <summary>One row in the overlay, in the FIXED field order — never re-sorted.</summary>
public sealed class VerdictRowViewModel
{
    public string FieldKey { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public VerdictStatus Status { get; init; }
    public string Explanation { get; init; } = "";
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
}

/// <summary>
/// Orchestrates: attach to PioneerRx -> read both panels via FieldReader
/// -> call the engine via EngineClient -> expose fixed-order verdict
/// rows for MainWindow to bind to. This is the only place that combines
/// all three pieces, so the overlay UI itself stays a thin renderer.
/// </summary>
public sealed class OverlayViewModel : INotifyPropertyChanged
{
    private readonly EngineClient _engineClient;

    public ObservableCollection<VerdictRowViewModel> Verdicts { get; } = new();

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
            Verdicts.Clear();
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
            StatusMessage = "Source script does not look structured (likely a faxed/scanned image) — manual review required. See README 'Deferred: OCR'.";
            Verdicts.Clear();
            UpdateSummary(null);
            return;
        }

        var result = await _engineClient.VerifyAsync(source, entered);

        if (!string.IsNullOrEmpty(result.Error))
        {
            StatusMessage = result.Error;
            return;
        }

        Verdicts.Clear();
        foreach (var field in FieldOrder.Fields)
        {
            var verdict = result.Verdicts.FirstOrDefault(v => v.Field == field);
            if (verdict is null) continue; // defensive: engine contract guarantees all 10 fields, but never crash the UI on a contract drift

            Verdicts.Add(new VerdictRowViewModel
            {
                FieldKey = field,
                DisplayName = FieldOrder.DisplayNames[field],
                Status = verdict.Status,
                Explanation = verdict.Explanation,
                SourceValue = verdict.SourceValue ?? "(not provided)",
                EnteredValue = verdict.EnteredValue ?? "(not provided)"
            });
        }

        UpdateSummary(result.Summary);
        StatusMessage = $"Last checked {DateTime.Now:h:mm:ss tt}.";
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
