using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using RxVerifyOverlay.Engine;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.ViewModels;

namespace RxVerifyOverlay;

public partial class MainWindow : Window
{
    private readonly OverlaySettings _settings;
    private EngineClient _engineClient;
    private OverlayViewModel _viewModel;
    // Nullable: briefly null between InitializeComponent() (which can raise
    // Checked for the XAML-default IsChecked="True" on AutoRefreshCheckBox)
    // and the line below that actually constructs it — see
    // OnAutoRefreshToggled's null guard.
    private readonly DispatcherTimer? _autoRefreshTimer;

    public MainWindow()
    {
        InitializeComponent();

        // W-T11 item 4: launch near the RIGHT edge of the primary
        // screen's working area instead of the old fixed Left="20" (left
        // edge). Computed from the working area so it adapts to whatever
        // monitor/resolution the workstation has, and clamped so the
        // window can never end up partially or fully off-screen (e.g. if
        // WindowWidth were ever larger than the working area itself).
        // This is only the INITIAL position — the window stays freely
        // movable afterward, same as before (see the XAML header
        // comment).
        const double rightMargin = 20;
        var workArea = SystemParameters.WorkArea;
        var left = workArea.Right - Width - rightMargin;
        Left = Math.Max(workArea.Left, left);

        _settings = OverlaySettings.Load();
        _engineClient = new EngineClient(_settings.EngineCliPath, _settings.NodeExecutable);
        _viewModel = new OverlayViewModel(_engineClient);
        DataContext = _viewModel;

        CliPathTextBox.Text = _settings.EngineCliPath;
        NodeExeTextBox.Text = _settings.NodeExecutable;

        // AUTO-WATCH (W-T9 item 5): a 1s tick calling OverlayViewModel.
        // WatchAsync, NOT a fixed "always do a full RefreshAsync every
        // 5s" timer like before. WatchAsync itself only does a cheap
        // PioneerRx title read on every tick and only runs the real
        // (expensive) verify when the pre-check/edit/new-rx screen's
        // presence or Rx number actually changed since the last tick —
        // see PioneerRxWindow.GetScreenSignature +
        // OverlayViewModel.WatchAsync for the full change-detection
        // approach. A 1s tick is safe specifically because the common
        // case (nothing changed) is nearly free.
        _autoRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _autoRefreshTimer.Tick += async (_, _) => await SafeWatchAsync();

        // W-T11 item 3: Auto-watch now starts CHECKED by default (see
        // AutoRefreshCheckBox IsChecked="True" in MainWindow.xaml), so
        // start the timer to match that initial state right here rather
        // than relying on the Checked routed event firing during
        // InitializeComponent (unreliable this early — _autoRefreshTimer
        // doesn't exist yet at that point, see OnAutoRefreshToggled's
        // null guard). This is now the single source of truth for
        // "does auto-watch start running on launch", matching whatever
        // AutoRefreshCheckBox.IsChecked actually is.
        if (AutoRefreshCheckBox.IsChecked == true)
        {
            _autoRefreshTimer.Start();
        }

        // First read on launch so the panel isn't empty while the
        // pharmacist decides whether to enable auto-watch.
        Loaded += async (_, _) => await SafeRefreshAsync();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await SafeRefreshAsync();

    private async Task SafeRefreshAsync()
    {
        try
        {
            await _viewModel.RefreshAsync();
        }
        catch (Exception ex)
        {
            // Belt-and-suspenders: OverlayViewModel already catches its
            // own internal failures into StatusMessage, but this guards
            // against anything unexpected so a bad refresh can never
            // crash the whole overlay mid-shift.
            MessageBox.Show(this, $"Unexpected error during refresh: {ex.Message}", "Rx Verify",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Timer-driven counterpart to SafeRefreshAsync, calling the cheap WatchAsync instead of always forcing a full RefreshAsync — see the auto-watch timer setup in the constructor.</summary>
    private async Task SafeWatchAsync()
    {
        try
        {
            await _viewModel.WatchAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Unexpected error during auto-watch: {ex.Message}", "Rx Verify",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnAutoRefreshToggled(object sender, RoutedEventArgs e)
    {
        // Defensive null guard: WPF can raise Checked for a XAML-default
        // IsChecked="True" (see AutoRefreshCheckBox) during
        // InitializeComponent, before _autoRefreshTimer is constructed —
        // the constructor's own explicit start (matching the checkbox's
        // initial state) already covers that case, so this is a no-op
        // rather than a NullReferenceException if it fires that early.
        if (_autoRefreshTimer is null) return;

        if (AutoRefreshCheckBox.IsChecked == true)
        {
            _autoRefreshTimer.Start();
        }
        else
        {
            _autoRefreshTimer.Stop();
        }
    }

    private void OnDumpTreeClick(object sender, RoutedEventArgs e)
    {
        var dump = _viewModel.DumpCurrentWindowTree();
        if (dump is null)
        {
            MessageBox.Show(this, "No PioneerRx window found right now — open a Pre-Check Rx, Edit Rx, or New Rx screen and try again.",
                "Rx Verify", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // This dump can contain real patient/prescriber data (it's a
        // literal readout of everything on screen) — only ever written
        // to disk via this EXPLICIT, visible user action, never
        // automatically. Prompt for a save location every time rather
        // than writing to a fixed path, so it's obvious to Will where
        // it went (and that it exists at all, for cleanup).
        var dialog = new SaveFileDialog
        {
            Title = "Save UIA tree dump",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = $"pioneerrx-uia-dump-{DateTime.Now:yyyyMMdd-HHmmss}.txt"
        };

        if (dialog.ShowDialog(this) == true)
        {
            File.WriteAllText(dialog.FileName, dump);
            MessageBox.Show(this, $"Saved to {dialog.FileName}.\n\nCompare this against Uia/FieldMap.cs and Uia/PioneerRxWindow.cs to adjust labels/bounds for any field that isn't reading correctly. This file may contain real patient data — handle/delete it per your usual workstation policy.",
                "Rx Verify", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnBrowseCliPathClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Locate rx-verify's dist/cli.js",
            Filter = "JavaScript files (*.js)|*.js|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) == true)
        {
            CliPathTextBox.Text = dialog.FileName;
        }
    }

    private void OnSaveSettingsClick(object sender, RoutedEventArgs e)
    {
        _settings.EngineCliPath = CliPathTextBox.Text.Trim();
        _settings.NodeExecutable = string.IsNullOrWhiteSpace(NodeExeTextBox.Text) ? "node" : NodeExeTextBox.Text.Trim();
        _settings.Save();

        // Rebuild the engine client with the new paths and rewire the
        // view model, since EngineClient's paths are immutable per
        // instance (see Engine/EngineClient.cs).
        _engineClient = new EngineClient(_settings.EngineCliPath, _settings.NodeExecutable);
        _viewModel = new OverlayViewModel(_engineClient);
        DataContext = _viewModel;

        MessageBox.Show(this, "Settings saved.", "Rx Verify", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
