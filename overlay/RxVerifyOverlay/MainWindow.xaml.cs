using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using RxVerifyOverlay.Engine;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.Ocr;
using RxVerifyOverlay.ViewModels;

namespace RxVerifyOverlay;

public partial class MainWindow : Window, IOverlayVisibilityController
{
    private readonly OverlaySettings _settings;
    private EngineClient _engineClient;
    private OverlayViewModel _viewModel;
    // Nullable: briefly null between InitializeComponent() (which can raise
    // Checked for the XAML-default IsChecked="True" on AutoRefreshCheckBox)
    // and the line below that actually constructs it — see
    // OnAutoRefreshToggled's null guard.
    private readonly DispatcherTimer? _autoRefreshTimer;

    // Defensive suppression flag, same pattern as _autoRefreshTimer's
    // null guard below: InitializeComponent() can raise Checked for the
    // XAML-default IsChecked="True" on MethodOcrRadioButton before
    // _settings/_viewModel exist, and the constructor also programmatically
    // sets IsChecked once _settings is loaded (which raises Checked
    // again). Both must be no-ops — OnMethodChanged should only react to
    // an actual user click. Starts true and is flipped to false at the
    // very end of the constructor, once real initialization is done.
    private bool _suppressMethodChangeHandler = true;

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

        // W-T13 item 2: launch ~200px down from the top of the working
        // area instead of hugging the very top edge (old fixed Top="20"
        // in XAML). Clamped the same way Left is above so the window can
        // never end up partially or fully off the bottom of the screen
        // on a short/small display — this is only the INITIAL position,
        // same free-move behavior afterward (see the XAML header
        // comment).
        const double topOffset = 200;
        var top = workArea.Top + topOffset;
        var maxTop = Math.Max(workArea.Top, workArea.Bottom - Height);
        Top = Math.Min(Math.Max(workArea.Top, top), maxTop);

        _settings = OverlaySettings.Load();

        // Fresh workstation: no saved EngineCliPath (or a stale one from a
        // moved/rebuilt repo) used to mean a hard "Engine CLI not found"
        // error until the user manually located dist/cli.js via the
        // Locate.../Save flow below. Since the overlay is always built
        // inside this repo, we can auto-detect dist/cli.js by walking up
        // from the app's own build output directory. Manual override via
        // Locate.../Save (further down, and in the click handler) still
        // takes precedence any time it's set and valid.
        if (string.IsNullOrWhiteSpace(_settings.EngineCliPath) || !File.Exists(_settings.EngineCliPath))
        {
            var resolved = OverlaySettings.ResolveDefaultCliPath();
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                _settings.EngineCliPath = resolved;

                // Persist immediately so settings.json carries the
                // resolved path going forward — the whole point of
                // "preset" is that the user never has to touch this
                // (see Models/OverlaySettings.cs ResolveDefaultCliPath
                // doc). If the resolver instead returns "" (engine not
                // built yet), we leave EngineCliPath blank and don't
                // write anything — the next launch after `npm run
                // build` creates dist/cli.js resolves it then.
                _settings.Save();
            }
        }

        _engineClient = new EngineClient(_settings.EngineCliPath, _settings.NodeExecutable);
        _viewModel = new OverlayViewModel(_engineClient, _settings, overlayVisibilityController: this);
        DataContext = _viewModel;

        CliPathTextBox.Text = _settings.EngineCliPath;
        NodeExeTextBox.Text = _settings.NodeExecutable;

        // Verification method toggle — reflect the saved/default setting
        // in the radio buttons (default is Ocr, see Models/
        // OverlaySettings.cs VerificationMethod) without treating this as
        // a user-driven change (see _suppressMethodChangeHandler doc).
        if (_settings.Method == VerificationMethod.Uia)
        {
            MethodUiaRadioButton.IsChecked = true;
        }
        else
        {
            MethodOcrRadioButton.IsChecked = true;
        }
        UpdateMethodBadge();
        _suppressMethodChangeHandler = false;

        // VerifyOCR capture-region override — see Models/OverlaySettings.cs
        // and MainWindow.xaml's "OCR capture region" section.
        UseExplicitCaptureRegionCheckBox.IsChecked = _settings.UseExplicitCaptureRegion;
        CaptureRegionLeftTextBox.Text = _settings.CaptureRegionLeft.ToString();
        CaptureRegionTopTextBox.Text = _settings.CaptureRegionTop.ToString();
        CaptureRegionWidthTextBox.Text = _settings.CaptureRegionWidth.ToString();
        CaptureRegionHeightTextBox.Text = _settings.CaptureRegionHeight.ToString();

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

    /// <summary>
    /// Verification-method toggle (Step 5: combine "Verify"/"VerifyOCR"
    /// into one app, runtime-selectable). Fires on either RadioButton's
    /// Checked event; the OTHER one's Unchecked also fires but we only
    /// need one handler since GroupName guarantees exactly one is
    /// checked at a time. Saves settings and kicks off an immediate
    /// RefreshAsync so switching takes effect right away rather than
    /// waiting for the next auto-watch tick.
    /// </summary>
    private async void OnMethodChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressMethodChangeHandler) return;

        var newMethod = MethodUiaRadioButton.IsChecked == true ? VerificationMethod.Uia : VerificationMethod.Ocr;
        if (_settings.Method == newMethod) return;

        _settings.Method = newMethod;
        _settings.Save();
        UpdateMethodBadge();

        await SafeRefreshAsync();
    }

    /// <summary>Reflects the active verification method in the window title and the small badge next to "Rx Verify" (MethodBadgeText) — called on startup and every OnMethodChanged.</summary>
    private void UpdateMethodBadge()
    {
        var label = _settings.Method == VerificationMethod.Uia ? "Escript tab" : "OCR";
        MethodBadgeText.Text = label;
        Title = $"Rx Verify — {label}";
    }

    /// <summary>
    /// "Copy logs" — builds the current-Rx log blob (OverlayViewModel.
    /// BuildCurrentLogBlob) and puts it straight on the clipboard, so Will
    /// can paste it into a message in one click instead of digging through
    /// %TEMP%\VerifyOCR\ocr-*.log. In-memory + clipboard only: nothing is
    /// written to disk by this button, and the blob is rebuilt fresh from
    /// whatever is currently on screen every time (see
    /// BuildCurrentLogBlob's "current Rx only" doc) rather than
    /// accumulating history.
    /// </summary>
    private void OnCopyLogsClick(object sender, RoutedEventArgs e)
    {
        string blob;
        try
        {
            blob = _viewModel.BuildCurrentLogBlob();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't build the log: {ex.Message}", "Rx Verify",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TrySetClipboardText(blob))
        {
            MessageBox.Show(this,
                "Couldn't copy to the clipboard (it may be locked by another app — try again in a moment).",
                "Rx Verify", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        MessageBox.Show(this, "Log copied to clipboard.", "Rx Verify", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>
    /// Clipboard.SetText occasionally throws COMException/"clipboard could
    /// not be opened" when another process (clipboard manager, etc.) is
    /// briefly holding it — a well-known WPF clipboard gotcha, not
    /// specific to this app. A few short retries clears the vast majority
    /// of those transient failures without the pharmacist ever noticing.
    /// </summary>
    private static bool TrySetClipboardText(string text)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (Exception) when (attempt < 2)
            {
                System.Threading.Thread.Sleep(50);
            }
            catch (Exception)
            {
                return false;
            }
        }

        return false;
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

        // VerifyOCR capture-region override. Non-numeric/blank text boxes
        // fall back to 0 (int.TryParse's default 'out' value) rather than
        // throwing — an accidental bad value here just yields an
        // empty/invalid region, which OcrFieldReader.ReadSourceFromOcrAsync
        // already reports as a clear "capture region is empty" status
        // message rather than crashing settings save.
        _settings.UseExplicitCaptureRegion = UseExplicitCaptureRegionCheckBox.IsChecked == true;
        int.TryParse(CaptureRegionLeftTextBox.Text, out var captureLeft);
        int.TryParse(CaptureRegionTopTextBox.Text, out var captureTop);
        int.TryParse(CaptureRegionWidthTextBox.Text, out var captureWidth);
        int.TryParse(CaptureRegionHeightTextBox.Text, out var captureHeight);
        _settings.CaptureRegionLeft = captureLeft;
        _settings.CaptureRegionTop = captureTop;
        _settings.CaptureRegionWidth = captureWidth;
        _settings.CaptureRegionHeight = captureHeight;

        _settings.Save();

        // Rebuild the engine client with the new paths and rewire the
        // view model, since EngineClient's paths are immutable per
        // instance (see Engine/EngineClient.cs).
        _engineClient = new EngineClient(_settings.EngineCliPath, _settings.NodeExecutable);
        _viewModel = new OverlayViewModel(_engineClient, _settings, overlayVisibilityController: this);
        DataContext = _viewModel;

        MessageBox.Show(this, "Settings saved.", "Rx Verify", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ------------------------------------------------------------------
    // IOverlayVisibilityController — see Ocr/IOverlayVisibilityController.cs
    // and Uia/OcrFieldReader.cs's SELF-OCCLUSION GUARD doc. Called around
    // EscriptImageCapture.CaptureRegion only, never during OCR itself.
    // ------------------------------------------------------------------

    /// <summary>
    /// Hides this window (Window.Hide() — Visibility=Hidden, same
    /// mechanism WPF already uses, so no new behavior to reason about)
    /// and then waits for the screen area it was covering to actually
    /// repaint. Hiding a Topmost window is usually near-instant, but DWM
    /// composition isn't guaranteed synchronous with the Visibility
    /// change, so this yields to the Dispatcher at Render priority (lets
    /// any pending layout/render pass flush) plus a short fixed delay
    /// before returning — long enough to avoid a stale frame of the
    /// overlay's own UI still being on screen when CaptureRegion runs,
    /// short enough (~30ms) that the hide/show round-trip isn't a
    /// noticeable flicker to the pharmacist.
    /// </summary>
    public async Task HideForCaptureAsync()
    {
        Hide();
        await Dispatcher.Yield(DispatcherPriority.Render);
        await Task.Delay(30);
    }

    /// <summary>Restores the overlay after a capture — called from OcrFieldReader's finally, so this always runs even if the capture itself threw.</summary>
    public void RestoreAfterCapture()
    {
        Show();
    }
}
