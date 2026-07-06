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
    private readonly DispatcherTimer _autoRefreshTimer;

    public MainWindow()
    {
        InitializeComponent();

        _settings = OverlaySettings.Load();
        _engineClient = new EngineClient(_settings.EngineCliPath, _settings.NodeExecutable);
        _viewModel = new OverlayViewModel(_engineClient);
        DataContext = _viewModel;

        CliPathTextBox.Text = _settings.EngineCliPath;
        NodeExeTextBox.Text = _settings.NodeExecutable;

        _autoRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _autoRefreshTimer.Tick += async (_, _) => await SafeRefreshAsync();

        // First read on launch so the panel isn't empty while the
        // pharmacist decides whether to enable auto-refresh.
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

    private void OnAutoRefreshToggled(object sender, RoutedEventArgs e)
    {
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
