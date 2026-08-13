using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using RxVerifyOverlay.Diagnostics;
using RxVerifyOverlay.Engine;
using RxVerifyOverlay.Integrated;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.Ocr;
using RxVerifyOverlay.Reporting;
using RxVerifyOverlay.Uia;
using RxVerifyOverlay.ViewModels;

namespace RxVerifyOverlay;

public partial class MainWindow : Window, IOverlayVisibilityController
{
    // How often the auto-watch timer ticks. Each tick is a cheap PioneerRx
    // title read (~1ms), NOT an OCR pass — see the AUTO-WATCH comment on
    // _autoRefreshTimer's construction below for why a short interval is
    // safe. Lowered from 1000ms so an Rx-number change is caught in
    // ~250ms worst case instead of ~1s, per Will's request to make the
    // overlay feel more responsive when Pioneer switches Rx.
    private const int AutoWatchIntervalMs = 250;

    private readonly OverlaySettings _settings;
    private EngineClient _engineClient;
    private OverlayViewModel _viewModel;
    // Nullable: briefly null between InitializeComponent() (which can raise
    // Checked for the XAML-default IsChecked="True" on AutoRefreshCheckBox)
    // and the line below that actually constructs it — see
    // OnAutoRefreshToggled's null guard.
    private readonly DispatcherTimer? _autoRefreshTimer;

    // EVENT-DRIVEN detection (latency fix, see Uia/TitleChangeWatcher.cs)
    // — fires almost immediately on a title change instead of waiting up
    // to AutoWatchIntervalMs for the next poll tick. Purely additive: the
    // poll timer above is unchanged and remains the safety net if this
    // hook fails to install (TryStart() returns false) or never fires.
    private readonly TitleChangeWatcher _titleChangeWatcher;

    // CAPTURE-EXCLUSION (latency fix — see HideForCaptureAsync/
    // RestoreAfterCapture below). True once SetWindowDisplayAffinity(
    // WDA_EXCLUDEFROMCAPTURE) has been applied to this window's HWND and
    // returned success, meaning Windows itself now omits this window
    // from any GDI screen capture (Graphics.CopyFromScreen included) —
    // so the hide -&gt; wait -&gt; capture -&gt; show round-trip is no longer
    // needed at all. Starts false (the safe default: behave exactly like
    // before) until OnSourceInitialized runs and either confirms it or
    // leaves this false as the fallback for an unsupported OS.
    private bool _excludedFromCapture;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    /// <summary>Requires Windows 10 2004+ (build 19041) — this project already targets 10.0.19041 (see the .csproj TargetFramework), so any machine that can run this build supports it. OnSourceInitialized still treats a false/failed call as "unsupported" and falls back to the hide/show path, in case a future build ever targets an older OS.</summary>
    private const uint WdaExcludeFromCapture = 0x00000011;

    // Defensive suppression flag, same pattern as _autoRefreshTimer's
    // null guard below: InitializeComponent() can raise Checked for the
    // XAML-default IsChecked="True" on MethodOcrRadioButton before
    // _settings/_viewModel exist, and the constructor also programmatically
    // sets IsChecked once _settings is loaded (which raises Checked
    // again). Both must be no-ops — OnMethodChanged should only react to
    // an actual user click. Starts true and is flipped to false at the
    // very end of the constructor, once real initialization is done.
    private bool _suppressMethodChangeHandler = true;

    // Same defensive-suppression reasoning as _suppressMethodChangeHandler
    // above, for the Display-mode toggle (DisplayIntegratedRadioButton's
    // XAML-default IsChecked="True" — Integrated is the default, see
    // Models/OverlaySettings.cs DisplayMode).
    private bool _suppressDisplayModeChangeHandler = true;

    // INTEGRATED DISPLAY MODE (Integrated/IntegratedOverlayCoordinator.cs)
    // — owns/drives the boxes-over-Pioneer layer and the in-ribbon control
    // box; this window only wires its *Requested events into the SAME
    // settings-mutation/copy-logs logic the Separate window's own controls
    // already use, and shows/hides itself in response to DisplayMode
    // switching. Non-nullable: constructed unconditionally (cheap — it
    // doesn't create either integrated window until DisplayMode actually
    // becomes Integrated), same as _viewModel.
    private readonly IntegratedOverlayCoordinator _integratedOverlay;

    /// <summary>
    /// INTEGRATED DISPLAY MODE: whether this window should start hidden.
    /// Read by App.xaml.cs right after construction — a window that starts
    /// in Integrated mode (persisted from a previous session) has no
    /// business ever flashing visible before hiding itself, so App.xaml.cs
    /// only calls Show() when this is Separate; see StartupCompleted() for
    /// how the very first RefreshAsync still happens when it doesn't.
    /// </summary>
    public DisplayMode InitialDisplayMode => _settings.DisplayMode;

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

        // Display-mode toggle — reflect the saved/default setting
        // (default Integrated, see Models/OverlaySettings.cs DisplayMode)
        // without treating this as a user-driven change (see
        // _suppressDisplayModeChangeHandler doc).
        if (_settings.DisplayMode == DisplayMode.Integrated)
        {
            DisplayIntegratedRadioButton.IsChecked = true;
        }
        else
        {
            DisplaySeparateRadioButton.IsChecked = true;
        }
        _suppressDisplayModeChangeHandler = false;

        // INTEGRATED DISPLAY MODE (Integrated/IntegratedOverlayCoordinator.cs)
        // — wires the control box's requested actions into the SAME
        // settings-mutation/copy-logs logic this window's own controls
        // already use, and shows/hides THIS window as DisplayMode
        // switches (see OnDisplayModeChanged / SetDisplayMode below).
        _integratedOverlay = new IntegratedOverlayCoordinator(_viewModel, _settings);
        _integratedOverlay.MethodToggleRequested += async (_, method) => await ApplyMethodChangeAsync(method);
        _integratedOverlay.ShowSeparateWindowRequested += (_, _) => { Show(); Activate(); };
        _integratedOverlay.HideSeparateWindowRequested += (_, _) => Hide();
        // 2026-08-13 (RXVERIFY-TROUBLESHOOT): CopyLogsRequested (the
        // control box's PHI-including "Copy" button) removed along with
        // its XAML button and event -- see Integrated/ControlBoxWindow.
        // CopyLogsNoHipaaRequested (the sanitized "Copy (safe)" button)
        // is the only copy-logs forwarding left.
        _integratedOverlay.CopyLogsNoHipaaRequested += async (_, button) => await CopyLogsToButtonAsync(button, redactPatient: true);
        _integratedOverlay.ToggleStateChanged += (_, _) => SyncOwnToggles();
        // Item 1: control box's Refresh button — identical handling to
        // this window's own OnRefreshClick.
        _integratedOverlay.RefreshRequested += async (_, _) =>
        {
            await SafeRefreshAsync();
            SafeTickIntegratedOverlay();
        };
        // Item 8: the control box's corner X button — routes through THIS
        // window's own existing Close()/Closed cleanup path (engine/
        // watcher dispose, _integratedOverlay.Shutdown(),
        // Application.Current.Shutdown() — see the Closed handler below)
        // rather than duplicating any of that here. Works the same way
        // whether MainWindow is currently visible or hidden (Integrated
        // mode) — Window.Close() doesn't require Show() to have been
        // called first, and still raises Closed either way.
        _integratedOverlay.CloseApplicationRequested += (_, _) => Close();
        // "Report error…" (verdict-tooltips-reports branch): the boxes
        // window's per-field context menu bubbles up through the
        // coordinator to here, the one place that knows how to build a
        // ReportErrorWindow (engine build/commit context — see that
        // constructor's params).
        _integratedOverlay.ReportErrorRequested += (_, field) => OpenReportErrorDialog(field);

        // VerifyOCR capture-region override — see Models/OverlaySettings.cs
        // and MainWindow.xaml's "OCR capture region" section.
        UseExplicitCaptureRegionCheckBox.IsChecked = _settings.UseExplicitCaptureRegion;
        CaptureRegionLeftTextBox.Text = _settings.CaptureRegionLeft.ToString();
        CaptureRegionTopTextBox.Text = _settings.CaptureRegionTop.ToString();
        CaptureRegionWidthTextBox.Text = _settings.CaptureRegionWidth.ToString();
        CaptureRegionHeightTextBox.Text = _settings.CaptureRegionHeight.ToString();

        // AUTO-WATCH (W-T9 item 5): a fast tick calling OverlayViewModel.
        // WatchAsync, NOT a fixed "always do a full RefreshAsync every
        // 5s" timer like before. WatchAsync itself only does a cheap
        // PioneerRx title read on every tick and only runs the real
        // (expensive) verify when the pre-check/edit/new-rx screen's
        // presence or Rx number actually changed since the last tick —
        // see PioneerRxWindow.GetScreenSignature +
        // OverlayViewModel.WatchAsync for the full change-detection
        // approach. A short tick (AutoWatchIntervalMs) is safe
        // specifically because the common case (nothing changed) is
        // nearly free.
        _autoRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AutoWatchIntervalMs) };
        _autoRefreshTimer.Tick += async (_, _) =>
        {
            await SafeWatchAsync();

            // INTEGRATED DISPLAY MODE: same cadence as the verify-content
            // watch above — reposition/show/hide the boxes+control box
            // (tracks PioneerRx moving/maximizing/losing foreground
            // independently of whether a full verify actually re-ran this
            // tick). A cheap no-op whenever DisplayMode is Separate.
            SafeTickIntegratedOverlay();
        };

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

        // EVENT-DRIVEN detection (latency fix — see
        // Uia/TitleChangeWatcher.cs). TryStart()'s return value is
        // deliberately not surfaced to the pharmacist either way: on
        // success this only IMPROVES on the poll's worst-case latency,
        // and on failure the poll (started above, unconditionally)
        // already keeps the overlay fully correct — see
        // TitleChangeWatcher's class doc for why this can never be
        // treated as an error condition.
        // ADDENDUM item 7 (priority): HideBoxesIfRxIdentityChanged runs
        // SYNCHRONOUSLY, on this same Dispatcher-thread callback, BEFORE
        // the fire-and-forget SafeWatchAsync() call even starts running —
        // see that method's doc for why closing this gap matters (a
        // previous Rx's stale boxes must never linger over a NEW
        // prescription's fields for however long the resulting refresh
        // takes).
        _titleChangeWatcher = new TitleChangeWatcher(() =>
        {
            _integratedOverlay.HideBoxesIfRxIdentityChanged();
            _ = SafeWatchAsync();
        });
        _titleChangeWatcher.TryStart();

        // CAPTURE-EXCLUSION (latency fix, branch brief item 4): applied
        // as soon as the window's native HWND exists (SourceInitialized,
        // which WPF always raises before Loaded — see the ordering this
        // relies on below), so it's active before the very first
        // SafeRefreshAsync call from Loaded can run a capture.
        SourceInitialized += OnSourceInitialized;

        // First read on launch so the panel isn't empty while the
        // pharmacist decides whether to enable auto-watch. Only fires if
        // this window is actually shown — see StartupCompleted() for the
        // Integrated-mode-at-startup case, where App.xaml.cs never calls
        // Show() at all.
        Loaded += async (_, _) =>
        {
            await SafeRefreshAsync();
            SafeTickIntegratedOverlay();
        };

        // "Report error…" store-and-forward (verdict-tooltips-reports
        // branch): retries anything queued in pending-reports.jsonl from a
        // previous session (no key configured yet at the time, or a
        // network blip) — a no-op fast-exit when RxVerifyReportKey is
        // still unset (see RxReportSubmitter.RetryPendingAsync's doc).
        // Fire-and-forget: never blocks startup, and every failure mode
        // inside it is already caught (fail-soft — see RxReportSubmitter's
        // class doc), so there's nothing here that needs a try/catch of
        // its own.
        _ = new RxReportSubmitter(_settings).RetryPendingAsync();

        // EngineClient now owns a PERSISTENT node.exe (latency fix — see
        // Engine/EngineClient.cs) instead of spawning one per call, so it
        // must be explicitly disposed on shutdown or that process would
        // otherwise keep running as an orphan after the overlay closes.
        // TitleChangeWatcher likewise holds an unmanaged OS hook that
        // must be explicitly unhooked (see its Dispose doc).
        Closed += (_, _) =>
        {
            _engineClient.Dispose();
            _titleChangeWatcher.Dispose();
            _integratedOverlay.Shutdown();

            // App.xaml sets ShutdownMode="OnExplicitShutdown" (needed
            // because this window can now start never-shown in Integrated
            // mode — see App.xaml's comment) — closing THIS window still
            // means "exit the whole app", same as before that change, so
            // it must now trigger shutdown explicitly.
            Application.Current.Shutdown();
        };
    }

    /// <summary>
    /// Called from App.xaml.cs when this window starts in Integrated mode
    /// (so App.xaml.cs never calls Show(), and Loaded — which normally
    /// kicks off the very first refresh — never fires). Runs the exact
    /// same first-refresh-plus-integrated-tick sequence Loaded would have,
    /// just triggered explicitly instead of by the window becoming visible.
    /// </summary>
    public async Task StartupCompleted()
    {
        await SafeRefreshAsync();
        SafeTickIntegratedOverlay();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        await SafeRefreshAsync();
        SafeTickIntegratedOverlay();
    }

    /// <summary>
    /// REVIEW FIX: wraps _integratedOverlay.Tick() the same way
    /// SafeRefreshAsync/SafeWatchAsync wrap their own OverlayViewModel
    /// calls — belt-and-suspenders on top of Tick()'s own internal
    /// catch-and-degrade (see IntegratedOverlayCoordinator.Tick's doc):
    /// PioneerRxWindow.TryAttach can rethrow on a bad shared UIA session,
    /// and this app has no DispatcherUnhandledException hook, so ANY
    /// unguarded call site here would be a process-killing crash (taking
    /// Separate mode down with it, since it's the same process) instead
    /// of a recoverable hiccup.
    /// </summary>
    private void SafeTickIntegratedOverlay()
    {
        try
        {
            _integratedOverlay.Tick();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Unexpected error updating the integrated overlay: {ex.Message}", "Rx Verify",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

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
        await ApplyMethodChangeAsync(newMethod);
    }

    /// <summary>
    /// The actual Method-change logic (persist + badge + refresh),
    /// extracted from OnMethodChanged so the control box's identical
    /// toggle (Integrated/ControlBoxWindow.xaml — forwarded here via
    /// _integratedOverlay.MethodToggleRequested) can apply exactly the
    /// same change instead of duplicating it. Also re-syncs THIS window's
    /// own radio buttons and the control box's toggle afterward, so
    /// whichever toggle DIDN'T originate the change still reflects it.
    /// </summary>
    private async Task ApplyMethodChangeAsync(VerificationMethod newMethod)
    {
        if (_settings.Method != newMethod)
        {
            _settings.Method = newMethod;
            _settings.Save();
            UpdateMethodBadge();

            await SafeRefreshAsync();
        }

        // Pushes the new Method into the control box's toggle AND raises
        // ToggleStateChanged, which SyncOwnToggles (subscribed in the
        // constructor) uses to reflect it on THIS window's own radio
        // buttons too — covers both directions (this window's radio
        // click, and the control box's identical toggle) through one path.
        _integratedOverlay.SyncToggles();
    }

    /// <summary>
    /// Reflects current settings (Method + DisplayMode) on THIS window's
    /// own toggle radio buttons — subscribed to
    /// _integratedOverlay.ToggleStateChanged so a change made from the
    /// CONTROL BOX (which these radio buttons have no other way of
    /// finding out about) never leaves them stale next time the
    /// pharmacist reveals this window. Suppresses both toggles' Checked
    /// handlers while doing so, same reasoning as the constructor's own
    /// startup sync.
    /// </summary>
    private void SyncOwnToggles()
    {
        _suppressMethodChangeHandler = true;
        MethodOcrRadioButton.IsChecked = _settings.Method == VerificationMethod.Ocr;
        MethodUiaRadioButton.IsChecked = _settings.Method == VerificationMethod.Uia;
        _suppressMethodChangeHandler = false;

        _suppressDisplayModeChangeHandler = true;
        DisplaySeparateRadioButton.IsChecked = _settings.DisplayMode == DisplayMode.Separate;
        DisplayIntegratedRadioButton.IsChecked = _settings.DisplayMode == DisplayMode.Integrated;
        _suppressDisplayModeChangeHandler = false;
    }

    /// <summary>Reflects the active verification method in the window title and the small badge next to "Rx Verify" (MethodBadgeText) — called on startup and every ApplyMethodChangeAsync.</summary>
    private void UpdateMethodBadge()
    {
        var label = _settings.Method == VerificationMethod.Uia ? "Escript tab" : "OCR";
        MethodBadgeText.Text = label;
        Title = $"Rx Verify — {label}";
    }

    /// <summary>
    /// Display-mode toggle (Separate/Integrated — see Models/
    /// OverlaySettings.cs DisplayMode). Routes through
    /// IntegratedOverlayCoordinator.SetDisplayMode, the single source of
    /// truth that also shows/hides THIS window (via
    /// ShowSeparateWindowRequested/HideSeparateWindowRequested, wired in
    /// the constructor) and syncs the control box's identical toggle.
    /// </summary>
    private void OnDisplayModeChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressDisplayModeChangeHandler) return;

        var newMode = DisplayIntegratedRadioButton.IsChecked == true ? DisplayMode.Integrated : DisplayMode.Separate;
        _integratedOverlay.SetDisplayMode(newMode);
    }

    /// <summary>
    /// "Copy logs (no HIPAA)" — one-click clipboard copy of everything
    /// needed to debug the CURRENTLY-DISPLAYED Rx (raw OCR text + word
    /// geometry, parsed/mapped fields, match verdicts, warnings/errors,
    /// app version + commit) so Will can paste it straight into a message
    /// instead of digging through %TEMP%\VerifyOCR\ocr-*.log — with
    /// patient name/DOB/address stripped from the title, verdict rows,
    /// and raw OCR text/word list before it hits the clipboard, so a REAL
    /// prescription's log can be pasted without exposing PHI. In-memory +
    /// clipboard only: nothing is written to disk by this button, and the
    /// blob is rebuilt fresh from whatever is currently on screen every
    /// time (see BuildCurrentLogBlob's "current Rx only" doc) rather than
    /// accumulating history. 2026-08-13 (RXVERIFY-TROUBLESHOOT): this is
    /// now the ONLY copy-logs button — the plain PHI-including "Copy
    /// logs" button (OnCopyLogsClick, redactPatient: false) was removed
    /// per owner request. See OverlayViewModel.BuildCurrentLogBlob /
    /// Diagnostics/RxLogFormatter.cs.
    /// </summary>
    private async void OnCopyLogsNoHipaaClick(object sender, RoutedEventArgs e) => await CopyLogsToButtonAsync((Button)sender, redactPatient: true);

    /// <summary>
    /// Shared "copy logs" implementation for this window's own
    /// "Copy logs (no HIPAA)" button AND the control box's identical
    /// button (forwarded via _integratedOverlay.CopyLogsNoHipaaRequested —
    /// see the constructor). Item 5 (owner asked twice — must ship): on
    /// success, the clicked button itself flashes green with a checkmark
    /// for ~1.5s (ButtonFeedback.FlashSuccessAsync) instead of a
    /// MessageBox confirmation popup; genuine failures (couldn't build
    /// the log, clipboard locked) still show a MessageBox, since those
    /// need the pharmacist's attention.
    /// </summary>
    private async Task CopyLogsToButtonAsync(Button button, bool redactPatient)
    {
        string blob;
        try
        {
            blob = _viewModel.BuildCurrentLogBlob(redactPatient);
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

        await ButtonFeedback.FlashSuccessAsync(button);
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

    /// <summary>
    /// Opens Integrated/ReportErrorWindow for one field's verdict — see
    /// _integratedOverlay.ReportErrorRequested's wiring in the
    /// constructor. Deliberately does NOT set Owner=this: MainWindow can
    /// be hidden right now (Integrated display mode is the default — see
    /// Models/OverlaySettings.cs DisplayMode), and ShowDialog against a
    /// hidden owner is unreliable on WPF; the dialog's own
    /// WindowStartupLocation="CenterScreen" (see its XAML) is what makes
    /// skipping Owner safe instead of leaving the dialog positioned at
    /// (0,0). ShowDialog (not Show) so the pharmacist finishes or cancels
    /// this one report before doing anything else with it — it's a short,
    /// single-purpose form, not a panel meant to stay open alongside work.
    /// </summary>
    private void OpenReportErrorDialog(VerdictFieldInfo field)
    {
        var engineBuild = string.IsNullOrEmpty(_engineClient.EngineBuildSha) && string.IsNullOrEmpty(_engineClient.EngineBuildBuiltAt)
            ? null
            : $"{_engineClient.EngineBuildSha ?? "unknown"} {_engineClient.EngineBuildBuiltAt ?? "unknown"}";

        try
        {
            var dialog = new ReportErrorWindow(field, engineBuild, AppDiagnostics.GetCommitSha(), _settings);
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't open the report dialog: {ex.Message}", "Rx Verify",
                MessageBoxButton.OK, MessageBoxImage.Warning);
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
        // instance (see Engine/EngineClient.cs). MUST dispose the old
        // client first — it owns a persistent node.exe process (latency
        // fix) that would otherwise leak as an orphan every time settings
        // are saved.
        var previousEngineClient = _engineClient;
        _engineClient = new EngineClient(_settings.EngineCliPath, _settings.NodeExecutable);
        _viewModel = new OverlayViewModel(_engineClient, _settings, overlayVisibilityController: this);
        DataContext = _viewModel;
        previousEngineClient.Dispose();

        // See IntegratedOverlayCoordinator's _viewModel field doc — must
        // be re-pointed at the freshly-rebuilt OverlayViewModel, or the
        // integrated boxes/control-box status would keep reading from the
        // now-orphaned old instance forever.
        _integratedOverlay.UpdateViewModel(_viewModel);

        MessageBox.Show(this, "Settings saved.", "Rx Verify", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ------------------------------------------------------------------
    // IOverlayVisibilityController — see Ocr/IOverlayVisibilityController.cs
    // and Uia/OcrFieldReader.cs's SELF-OCCLUSION GUARD doc. Called around
    // EscriptImageCapture.CaptureRegion only, never during OCR itself.
    // ------------------------------------------------------------------

    /// <summary>
    /// Latency-fix diagnosis (branch brief item 4): live captures showed
    /// the "capture" timing bucket occasionally spiking past 1000ms, and
    /// this hide/show round-trip was the prime suspect — Hide()/Show()
    /// on a Topmost window aren't guaranteed cheap or fast, especially
    /// when the event-driven TitleChangeWatcher can trigger refreshes
    /// back-to-back faster than the old 250ms poll ever could, queuing
    /// up overlapping hide/show cycles on the Dispatcher. Applies
    /// SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) to this window's
    /// HWND as soon as it exists (SourceInitialized — Loaded, which
    /// triggers the first capture, always fires after this per WPF's
    /// documented lifecycle order) so Windows itself omits this window
    /// from any GDI capture going forward; HideForCaptureAsync/
    /// RestoreAfterCapture below then skip the round-trip entirely
    /// rather than needing it at all. Any failure (unsupported OS — pre
    /// Windows 10 2004, see WdaExcludeFromCapture's doc) leaves
    /// _excludedFromCapture false, and the existing hide/show path below
    /// runs completely unchanged as the fallback.
    /// </summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            _excludedFromCapture = SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture);
        }
        catch
        {
            _excludedFromCapture = false;
        }

        // DIAGNOSTIC VISIBILITY (post-review fix): whether the exclusion
        // actually took effect on THIS machine is exactly the thing a
        // silent failure would hide — logged once at startup, unconditionally,
        // so it's in every %TEMP%\VerifyOCR\ocr-*.log file regardless of
        // whether anyone remembers to check. See IsExcludedFromCapture doc
        // and RxLogFormatter's "Capture exclusion" line (surfaced again,
        // per-refresh, in the "Copy logs" blob) for the other half of this.
        OcrLogger.LogTiming(_excludedFromCapture
            ? "Capture exclusion: active (WDA_EXCLUDEFROMCAPTURE)"
            : "Capture exclusion: unavailable — using hide/show fallback");
    }

    /// <summary>See IOverlayVisibilityController.IsExcludedFromCapture.</summary>
    public bool IsExcludedFromCapture => _excludedFromCapture;

    /// <summary>
    /// Whether HideForCaptureAsync's most recent call actually hid a
    /// visible window — INTEGRATED DISPLAY MODE fix: this window can now
    /// be intentionally hidden on purpose (DisplayMode.Integrated — see
    /// HideSeparateWindowRequested in the constructor), not just visible-
    /// and-dragged-somewhere-in-the-way like before this feature existed.
    /// RestoreAfterCapture only re-shows the window if THIS flag says it
    /// was the one that hid it — otherwise an OCR capture during
    /// Integrated mode would incorrectly reveal the separate window
    /// afterward.
    /// </summary>
    private bool _wasVisibleBeforeCapture;

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
    ///
    /// A no-op when _excludedFromCapture is true (see OnSourceInitialized)
    /// — Windows already omits this window from the capture, so there's
    /// nothing to hide — OR when the window is already hidden for another
    /// reason (see _wasVisibleBeforeCapture doc).
    /// </summary>
    public async Task HideForCaptureAsync()
    {
        if (_excludedFromCapture) return;

        _wasVisibleBeforeCapture = IsVisible;
        if (!_wasVisibleBeforeCapture) return; // already hidden (e.g. Integrated mode) — nothing to hide/restore

        Hide();
        await Dispatcher.Yield(DispatcherPriority.Render);
        await Task.Delay(30);
    }

    /// <summary>Restores the overlay after a capture — called from OcrFieldReader's finally, so this always runs even if the capture itself threw. A no-op when _excludedFromCapture is true (HideForCaptureAsync never hid the window in the first place) OR when this window was already hidden before that call (see _wasVisibleBeforeCapture doc).</summary>
    public void RestoreAfterCapture()
    {
        if (_excludedFromCapture) return;
        if (!_wasVisibleBeforeCapture) return;

        Show();
    }
}
