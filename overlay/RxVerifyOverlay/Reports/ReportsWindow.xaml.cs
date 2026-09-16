using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;

namespace RxVerifyOverlay.Reports;

/// <summary>
/// Reports mode's single non-modal window (GOAL brief step 4) — picker of
/// which of ReportCatalog.All to run, Begin/End date range (defaulted to
/// last complete calendar month via ReportRunPlan), output folder, and a
/// Run/Stop pair driving ReportsCoordinator on a background Task with a
/// CancellationTokenSource for Stop.
///
/// NON-MODAL BY DESIGN: opened with Show() (never ShowDialog()) from
/// MainWindow.xaml.cs's OpenOrFocusReportsWindow, which also owns the
/// singleton reference — see that method's doc for why re-selecting
/// "Mode: Reports" reuses/focuses the same window rather than opening a
/// second one. Closing this window (its own X, or app shutdown) does NOT
/// cancel an in-progress run: OnRunClick's background await keeps running
/// against _coordinator/the driver regardless of whether this window is
/// still open, matching the GOAL brief's "switching back closes nothing
/// that is running" — UI-update callbacks below are wrapped defensively
/// so a closed window never crashes that background run.
/// </summary>
public sealed partial class ReportsWindow : Window
{
    private readonly ObservableCollection<ReportRowViewModel> _rows;
    private readonly ReportsCoordinator _coordinator;
    private CancellationTokenSource? _runCts;

    /// <summary>Production constructor — real PioneerReportDriver (FlaUI/UIA against the live PioneerRx window).</summary>
    public ReportsWindow() : this(new PioneerReportDriver())
    {
    }

    /// <summary>Test/DI seam — see RxVerifyOverlay.Tests for why PioneerReportDriver itself isn't unit tested (no UI tests per the GOAL brief; this constructor exists mainly so ReportsWindow's own non-UIA logic could be exercised with a fake if ever needed).</summary>
    public ReportsWindow(IPioneerReportDriver driver)
    {
        InitializeComponent();

        _rows = new ObservableCollection<ReportRowViewModel>();
        foreach (var entry in ReportCatalog.All)
        {
            _rows.Add(new ReportRowViewModel(entry));
        }
        ReportsList.ItemsSource = _rows;

        _coordinator = new ReportsCoordinator(driver);
        _coordinator.ProgressChanged += OnCoordinatorProgressChanged;

        var today = DateTime.Now;
        var defaultBegin = ReportRunPlan.DefaultBegin(today);
        var defaultEnd = ReportRunPlan.DefaultEnd(today);
        BeginDatePicker.SelectedDate = defaultBegin;
        EndDatePicker.SelectedDate = defaultEnd;
        OutputFolderTextBox.Text = ReportRunPlan.DefaultOutputFolder(defaultEnd);
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Choose a folder for saved reports",
                InitialDirectory = Directory.Exists(OutputFolderTextBox.Text)
                    ? OutputFolderTextBox.Text
                    : ReportRunPlan.DefaultOutputFolder(DateTime.Now)
            };

            if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
            {
                OutputFolderTextBox.Text = dialog.FolderName;
            }
        }
        catch
        {
            // Microsoft.Win32.OpenFolderDialog unavailable/unusable on
            // this workstation's runtime — OutputFolderTextBox above is
            // still fully hand-editable, so Browse failing is never a
            // dead end for Will.
            AppendLog("Browse isn't available here - type the folder path above instead.");
        }
    }

    private async void OnRunClick(object sender, RoutedEventArgs e)
    {
        var begin = BeginDatePicker.SelectedDate;
        var end = EndDatePicker.SelectedDate;
        if (begin is null || end is null)
        {
            AppendLog("Pick a Begin and End date first.");
            return;
        }

        if (end.Value.Date < begin.Value.Date)
        {
            AppendLog("End date is before Begin date.");
            return;
        }

        var outputFolder = OutputFolderTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(outputFolder))
        {
            AppendLog("Pick an output folder first.");
            return;
        }

        var selectedRows = _rows.Where(r => r.IsEnabled && r.IsSelected).ToList();
        if (selectedRows.Count == 0)
        {
            AppendLog("Select at least one report first.");
            return;
        }

        try
        {
            Directory.CreateDirectory(outputFolder);
        }
        catch (Exception ex)
        {
            AppendLog($"Couldn't create the output folder: {ex.Message}");
            return;
        }

        var items = selectedRows
            .Select(r => ReportRunPlan.Build(r.Entry, begin.Value, end.Value, outputFolder))
            .ToList();

        foreach (var row in selectedRows)
        {
            row.StatusText = "Pending";
        }

        RunButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        _runCts = new CancellationTokenSource();

        try
        {
            await _coordinator.RunAsync(items, AppendLog, _runCts.Token);
        }
        finally
        {
            RunButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    private void OnStopClick(object sender, RoutedEventArgs e) => _runCts?.Cancel();

    private void OnCoordinatorProgressChanged(object? sender, ReportProgressEventArgs e)
    {
        try
        {
            Dispatcher.Invoke(() =>
            {
                var row = _rows.FirstOrDefault(r => r.Entry.Key == e.Outcome.Item.Entry.Key);
                row?.ApplyOutcome(e.Outcome);
            });
        }
        catch
        {
            // Best-effort UI update only — never let a closed/torn-down
            // window take down the coordinator's background run (see
            // class doc's "closes nothing that is running").
        }
    }

    private void AppendLog(string line)
    {
        try
        {
            Dispatcher.Invoke(() =>
            {
                LogTextBox.AppendText($"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");
                LogTextBox.ScrollToEnd();
            });
        }
        catch
        {
            // Best-effort only — see OnCoordinatorProgressChanged's doc.
        }
    }
}
