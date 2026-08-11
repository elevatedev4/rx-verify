using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.Uia;
using RxVerifyOverlay.ViewModels;

namespace RxVerifyOverlay.Integrated;

/// <summary>
/// Owns and drives the two INTEGRATED display-mode windows —
/// IntegratedBoxesWindow (click-through verdict boxes drawn over
/// PioneerRx) and ControlBoxWindow (the small interactive panel in
/// Pioneer's ribbon) — plus the show/hide of the classic separate window
/// (MainWindow) as DisplayMode switches. This is the single place that
/// decides what's visible: MainWindow.xaml.cs just calls Tick() on its
/// existing ~250ms auto-watch timer / Loaded / OnRefreshClick paths, and
/// wires this class's *Requested events back into its own existing
/// settings-mutation logic (Method toggle) and BuildCurrentLogBlob/
/// clipboard code — nothing here talks to the engine or writes
/// settings.json except DisplayMode (see SetDisplayMode).
///
/// Lazily constructs both windows on first need (the common case —
/// Separate is the default — never even creates them), so a pharmacist
/// who never touches Integrated mode pays zero cost for this feature
/// beyond Tick()'s one cheap enum-comparison early-out.
/// </summary>
public sealed class IntegratedOverlayCoordinator
{
    // CONTROL BOX anchor, relative to PioneerRx's own WindowBounds — see
    // the owner's reference screenshot: the ribbon band right of the last
    // toolbar group (roughly x 850-1490, y 60-155 in a 1928-wide
    // maximized window) sits empty. Kept as named DIP constants (not
    // inline numbers) specifically so they're easy to retune against a
    // real workstation without hunting through positioning math.
    private const double ControlBoxRightInsetDip = 450; // box's LEFT edge sits this far in from the window's RIGHT edge
    private const double ControlBoxTopOffsetDip = 60;   // box's TOP edge sits this far down from the window's TOP edge
    private const double ControlBoxWidthDip = 420;      // must match ControlBoxWindow.xaml's Width
    private const double ControlBoxHeightDip = 92;      // must match ControlBoxWindow.xaml's Height

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    /// <summary>
    /// Per-monitor DPI for a specific HWND (Windows 10 1607+ —
    /// comfortably below this app's existing Windows-10-2004+ floor, see
    /// MainWindow.xaml.cs's WdaExcludeFromCapture doc). Used as the
    /// SINGLE authoritative DPI source for every physical&lt;-&gt;DIP
    /// conversion below, queried against PIONEER's own HWND — simpler and
    /// more robust than depending on our own windows' post-move
    /// VisualTreeHelper.GetDpi timing (our windows only just moved onto
    /// whatever monitor Pioneer is on; Pioneer's own DPI is authoritative
    /// and available immediately, no settling time).
    /// </summary>
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    // NOT readonly: MainWindow.xaml.cs's OnSaveSettingsClick rebuilds a
    // fresh OverlayViewModel whenever the engine paths change (a new
    // EngineClient needs a new OverlayViewModel around it — see that
    // handler) — UpdateViewModel keeps this coordinator pointed at
    // whichever instance is CURRENTLY live, so the integrated boxes/
    // control-box status never end up reading from a stale, orphaned
    // view model after a settings save.
    private OverlayViewModel _viewModel;
    private readonly OverlaySettings _settings;

    private IntegratedBoxesWindow? _boxesWindow;
    private ControlBoxWindow? _controlBox;
    private bool _boxesTopmostEstablished;
    private bool _controlBoxShown;
    private bool _boxesShown;

    /// <summary>Raised when the pharmacist changes the Method toggle FROM THE CONTROL BOX — MainWindow.xaml.cs handles this the same way as its own Source radio buttons (persist + refresh), then calls SyncToggles() so both toggles stay in lockstep.</summary>
    public event EventHandler<VerificationMethod>? MethodToggleRequested;

    /// <summary>Raised when the classic separate window (MainWindow) should become visible — either DisplayMode switched to Separate, or the control box's "Open full view" button was clicked (which does NOT change DisplayMode — see SetDisplayMode).</summary>
    public event EventHandler? ShowSeparateWindowRequested;

    /// <summary>Raised when DisplayMode switched to Integrated — MainWindow.xaml.cs hides itself.</summary>
    public event EventHandler? HideSeparateWindowRequested;

    /// <summary>Raised by either copy-logs button in the control box — MainWindow.xaml.cs handles this identically to its own "Copy logs" button (BuildCurrentLogBlob + clipboard + ButtonFeedback.FlashSuccessAsync on the same Button that was clicked).</summary>
    public event EventHandler<Button>? CopyLogsRequested;

    /// <summary>Same as CopyLogsRequested, redactPatient: true.</summary>
    public event EventHandler<Button>? CopyLogsNoHipaaRequested;

    /// <summary>
    /// Raised at the end of every SyncToggles() call — i.e. any time
    /// Method or DisplayMode changed from EITHER toggle's origin.
    /// MainWindow.xaml.cs subscribes to re-sync its OWN Source/View radio
    /// buttons, so a change made from the control box (which MainWindow's
    /// own radios have no other way of finding out about) never leaves
    /// them showing stale state next time the pharmacist reveals the
    /// separate window.
    /// </summary>
    public event EventHandler? ToggleStateChanged;

    public IntegratedOverlayCoordinator(OverlayViewModel viewModel, OverlaySettings settings)
    {
        _viewModel = viewModel;
        _settings = settings;
    }

    /// <summary>See the _viewModel field doc — call after replacing MainWindow's OverlayViewModel instance (OnSaveSettingsClick).</summary>
    public void UpdateViewModel(OverlayViewModel viewModel) => _viewModel = viewModel;

    /// <summary>
    /// Single source of truth for changing DisplayMode — both MainWindow's
    /// own toggle and the control box's toggle route through this so the
    /// setting, both windows' toggle UI, and the classic window's
    /// visibility can never drift out of sync. Persists immediately (same
    /// pattern as MainWindow.OnMethodChanged for Method).
    /// </summary>
    public void SetDisplayMode(DisplayMode mode)
    {
        var changed = _settings.DisplayMode != mode;
        _settings.DisplayMode = mode;
        _settings.Save();
        SyncToggles();

        if (changed)
        {
            if (mode == DisplayMode.Integrated) HideSeparateWindowRequested?.Invoke(this, EventArgs.Empty);
            else ShowSeparateWindowRequested?.Invoke(this, EventArgs.Empty);
        }

        Tick();
    }

    /// <summary>Pushes current settings into the control box's toggles (if it exists yet) without re-raising its *Requested events, then raises ToggleStateChanged so MainWindow can do the same for its own radio buttons — call after any settings mutation, regardless of which toggle's UI originated it.</summary>
    public void SyncToggles()
    {
        _controlBox?.SetToggleState(_settings.Method, _settings.DisplayMode);
        ToggleStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Call on every ~250ms tick (MainWindow's existing auto-watch timer),
    /// after Loaded's first refresh, and after a manual Refresh click —
    /// the same cadence the rest of the app already uses for "is anything
    /// different" polling. A no-op (one enum comparison) whenever
    /// DisplayMode is Separate, so this costs nothing for the common case.
    /// </summary>
    public void Tick()
    {
        if (_settings.DisplayMode != DisplayMode.Integrated)
        {
            HideBoxesIfShown();
            HideControlBoxIfShown();
            return;
        }

        using var window = PioneerRxWindow.TryAttach();
        var isAttached = window is not null;
        var isForeground = isAttached && GetForegroundWindow() == window!.NativeWindowHandle;
        var isMaximized = isAttached && IsZoomed(window!.NativeWindowHandle);
        var hasVerifiableContent = !_viewModel.HasNonEscriptMessage && _viewModel.Categories.Any(c => c.HasData);

        var showControlBox = IntegratedVisibilityGate.ShouldShowControlBox(isAttached, isForeground);
        var showBoxes = IntegratedVisibilityGate.ShouldShowBoxes(isAttached, isForeground, isMaximized, hasVerifiableContent);

        if (showControlBox)
        {
            UpdateControlBox(window!, isMaximized);
        }
        else
        {
            HideControlBoxIfShown();
        }

        if (showBoxes)
        {
            UpdateBoxes(window!);
        }
        else
        {
            HideBoxesIfShown();
        }
    }

    private ControlBoxWindow EnsureControlBox()
    {
        if (_controlBox is not null) return _controlBox;

        _controlBox = new ControlBoxWindow();
        _controlBox.MethodChangeRequested += (_, method) => MethodToggleRequested?.Invoke(this, method);
        _controlBox.DisplayModeChangeRequested += (_, mode) => SetDisplayMode(mode);
        _controlBox.CopyLogsRequested += (_, button) => CopyLogsRequested?.Invoke(this, button);
        _controlBox.CopyLogsNoHipaaRequested += (_, button) => CopyLogsNoHipaaRequested?.Invoke(this, button);
        _controlBox.OpenSeparateWindowRequested += (_, _) => ShowSeparateWindowRequested?.Invoke(this, EventArgs.Empty);
        SyncToggles();
        return _controlBox;
    }

    private IntegratedBoxesWindow EnsureBoxesWindow() => _boxesWindow ??= new IntegratedBoxesWindow();

    private void UpdateControlBox(PioneerRxWindow window, bool isMaximized)
    {
        var box = EnsureControlBox();
        var scale = DpiScaleFor(window);

        var bounds = window.WindowBounds;
        var physicalX = bounds.Right - (int)Math.Round(ControlBoxRightInsetDip * scale);
        var physicalY = bounds.Top + (int)Math.Round(ControlBoxTopOffsetDip * scale);
        var physicalWidth = (int)Math.Round(ControlBoxWidthDip * scale);
        var physicalHeight = (int)Math.Round(ControlBoxHeightDip * scale);

        box.SetMaximizedGuardState(isMaximized);
        box.SetStatusSummary(BuildStatusSummary(), _viewModel.StatusMessage);

        if (!_controlBoxShown)
        {
            box.Show();
            _controlBoxShown = true;
        }

        box.RepositionPhysical(physicalX, physicalY, physicalWidth, physicalHeight);
    }

    private void UpdateBoxes(PioneerRxWindow window)
    {
        var boxesWindow = EnsureBoxesWindow();
        var bounds = window.WindowBounds;

        if (!_boxesShown)
        {
            boxesWindow.Show();
            _boxesShown = true;
        }

        boxesWindow.RepositionPhysical(bounds.X, bounds.Y, bounds.Width, bounds.Height);

        if (!_boxesTopmostEstablished)
        {
            boxesWindow.EnsureTopmost();
            _boxesTopmostEstablished = true;
        }

        var scale = DpiScaleFor(window);

        var boxes = _viewModel.Categories
            .SelectMany(c => c.Rows)
            .Where(r => r.ScreenRect.HasValue)
            .Select(r => (r.ScreenRect!.Value, BoxColorMapper.IsGreenBox(r.Status)))
            .ToList();

        boxesWindow.SetBoxes(boxes, bounds.Location, scale, scale);
    }

    private static double DpiScaleFor(PioneerRxWindow window)
    {
        var dpi = GetDpiForWindow(window.NativeWindowHandle);
        return dpi > 0 ? dpi / 96.0 : 1.0;
    }

    private string BuildStatusSummary()
    {
        // "check" here means "needs a look" — yellow + red combined,
        // matching the boxes layer's own green/red binary collapse (see
        // BoxColorMapper) so the summary text and the boxes on screen
        // never disagree about what counts as "matches" vs. "check it".
        var checkCount = _viewModel.YellowCount + _viewModel.RedCount;
        return $"{_viewModel.GreenCount}✓ {checkCount}✗";
    }

    private void HideControlBoxIfShown()
    {
        if (!_controlBoxShown) return;
        _controlBox?.Hide();
        _controlBoxShown = false;
    }

    private void HideBoxesIfShown()
    {
        if (!_boxesShown) return;
        _boxesWindow?.Hide();
        _boxesShown = false;
    }

    /// <summary>Called from MainWindow's Closed handler — releases both integrated windows if they were ever created.</summary>
    public void Shutdown()
    {
        _boxesWindow?.Close();
        _controlBox?.Close();
    }
}
