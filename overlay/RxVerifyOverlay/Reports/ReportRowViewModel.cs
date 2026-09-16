using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RxVerifyOverlay.Reports;

/// <summary>
/// One row of ReportsWindow's picker/status list — the WPF-facing wrapper
/// around a ReportCatalogEntry. Plain INotifyPropertyChanged, same
/// pattern as ViewModels/OverlayViewModel.cs's CategoryViewModel/
/// VerdictRowViewModel, so the CheckBox/status TextBlock bindings update
/// live as ReportsCoordinator.ProgressChanged reports each report's
/// state.
/// </summary>
public sealed class ReportRowViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public ReportCatalogEntry Entry { get; }

    public string DisplayName => Entry.DisplayName;

    /// <summary>False only for the POS-daily placeholder row — greyed out, unselectable, per the GOAL brief.</summary>
    public bool IsEnabled => Entry.Enabled;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    private string _statusText = "Pending";
    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    public ReportRowViewModel(ReportCatalogEntry entry)
    {
        Entry = entry;
        _isSelected = entry.Enabled;
        _statusText = entry.Enabled ? "Pending" : "Coming next";
    }

    public void ApplyOutcome(ReportRunOutcome outcome)
    {
        StatusText = outcome.Status switch
        {
            ReportRunStatus.Pending => "Pending",
            ReportRunStatus.Running => "Running…",
            ReportRunStatus.Saved => $"Saved {outcome.SavedPath}",
            ReportRunStatus.Failed => $"Failed - {outcome.Detail}",
            _ => outcome.Status.ToString()
        };
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
