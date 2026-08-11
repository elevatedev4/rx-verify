using System;
using System.Diagnostics;
using System.Drawing;
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
    private const double ControlBoxRightInsetDip = 510; // box's LEFT edge sits this far in from the window's RIGHT edge — round 4: bumped from 450 (420 + ~30px margin) to 510 (480 + ~30px margin) to keep pace with ControlBoxWidthDip's widening below, so the box's right edge doesn't run off the window's right edge
    private const double ControlBoxTopOffsetDip = 60;   // box's TOP edge sits this far down from the window's TOP edge
    private const double ControlBoxWidthDip = 480;      // must match ControlBoxWindow.xaml's Width — widened from 420 in round 4 to fit the new Refresh button + "Hide overlay" checkbox without crowding
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

    // ------------------------------------------------------------------
    // OWNER FEEDBACK (round 2, item 1) — broader "is PioneerRx the
    // foreground app" detection, independent of PioneerRxWindow.TryAttach's
    // narrower title-prefix match (Pre-Check/Edit/New Rx specifically).
    // See TryGetForegroundPioneerRxWindow below.
    // ------------------------------------------------------------------
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

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

    /// <summary>
    /// REVIEW FIX (invisible-app trap): true while the separate window has
    /// been revealed as a FALLBACK because PioneerRx isn't attached at all
    /// (closed/not running) — without this, Integrated mode with Pioneer
    /// closed would hide BOTH integrated windows (nothing to draw over)
    /// AND the separate window, leaving the whole app invisible with no
    /// affordance to recover or quit. Tracked separately from
    /// _boxesShown/_controlBoxShown so this only fires
    /// ShowSeparateWindowRequested/HideSeparateWindowRequested on the
    /// actual attach/detach EDGE, not every tick (a pharmacist who
    /// manually re-hides this fallback window mid-detached-state isn't
    /// fought by the next tick re-showing it).
    /// </summary>
    private bool _fallbackSeparateWindowShown;

    /// <summary>
    /// ITEM 2: true while the pharmacist has hidden the verdict BOXES
    /// layer via the control box's checkbox or the global `\` hotkey (the
    /// control box itself stays up either way). Deliberately session-only
    /// — an in-memory field, never written to OverlaySettings/settings.json
    /// — so a pharmacist can never inherit a hidden overlay left over from
    /// a previous shift; it always starts false on every app launch.
    /// </summary>
    private bool _boxesHiddenByToggle;

    /// <summary>Raised when the pharmacist changes the Method toggle FROM THE CONTROL BOX — MainWindow.xaml.cs handles this the same way as its own Source radio buttons (persist + refresh), then calls SyncToggles() so both toggles stay in lockstep.</summary>
    public event EventHandler<VerificationMethod>? MethodToggleRequested;

    /// <summary>Item 1: raised when the control box's Refresh button is clicked — MainWindow.xaml.cs handles this identically to its own Refresh button (SafeRefreshAsync + SafeTickIntegratedOverlay).</summary>
    public event EventHandler? RefreshRequested;

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
    ///
    /// REVIEW FIX: the body is wrapped so a settings-save I/O hiccup or a
    /// downstream *Requested subscriber throwing can never propagate out
    /// of a toggle click and crash the app — same catch-and-degrade
    /// posture as Tick() below. Tick() itself is called outside the try
    /// since it's already internally exception-safe.
    /// </summary>
    public void SetDisplayMode(DisplayMode mode)
    {
        try
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
        }
        catch (Exception)
        {
            // Best-effort only — the pharmacist can just try the toggle
            // again; there's nothing more useful to do from here than
            // let the next Tick() re-evaluate visibility from scratch.
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
    ///
    /// REVIEW FIX: wraps TickCore in try/catch — PioneerRxWindow.TryAttach
    /// is documented to RETHROW if the shared UIA automation session
    /// itself goes bad (see its class doc "self-heal" catch block); every
    /// PRE-EXISTING caller routes through MainWindow's SafeRefreshAsync/
    /// SafeWatchAsync, which already catch this. This coordinator's own
    /// TryAttach calls (in TickCore) had no equivalent guard, so a
    /// transient accessibility hiccup would propagate out of an
    /// async-void DispatcherTimer tick with no DispatcherUnhandledException
    /// handler installed — i.e. crash the WHOLE process, Separate mode
    /// included, since it shares this one process. Degrades to "hide
    /// everything integrated" on any failure; the next tick tries again
    /// from a clean slate (TryAttach's own shared-session self-heal
    /// already handles recovering the underlying automation session).
    /// MainWindow.xaml.cs additionally wraps every call site to this
    /// method the same way (belt-and-suspenders), per the same review.
    /// </summary>
    public void Tick()
    {
        try
        {
            TickCore();
        }
        catch (Exception)
        {
            HideControlBoxIfShown();
            HideBoxesIfShown();
        }
    }

    private void TickCore()
    {
        if (_settings.DisplayMode != DisplayMode.Integrated)
        {
            // RE-REVIEW FIX (see FallbackSeparateWindowRule's class doc
            // for the confirmed regression this closes): leaving
            // Integrated mode must clear the fallback bookkeeping WITHOUT
            // raising HideSeparateWindowRequested — MainWindow's
            // visibility for THIS transition is already owned by
            // SetDisplayMode's own Show/HideSeparateWindowRequested call.
            ApplyFallbackDecision(FallbackSeparateWindowRule.Decide(isIntegratedMode: false, isPioneerAttached: false, _fallbackSeparateWindowShown));
            HideBoxesIfShown();
            HideControlBoxIfShown();
            return;
        }

        // NARROW attach: a Pre-Check/Edit/New-Rx window specifically —
        // required for the boxes layer, which needs real field rects to
        // draw over (see FieldReader.ReadEnteredFieldRects). Unchanged
        // from before this round.
        using var window = PioneerRxWindow.TryAttach();
        var isRxScreenAttached = window is not null;

        // BROAD foreground check (round 2, item 1): is PioneerRx the app
        // the pharmacist is currently looking at, REGARDLESS of which
        // screen (queue, search, dashboard, or a specific Rx) — see
        // TryGetForegroundPioneerRxWindow. This is what gates the CONTROL
        // BOX. It must NOT also gate the fallback-to-separate-window
        // decision below (round-3 fix — see PioneerPresence's doc for the
        // bug that caused: conflating "not in front right now" with
        // "doesn't exist" popped the fallback window at every launch and
        // on every alt-tab away from Pioneer).
        var hasForegroundPioneerWindow = TryGetForegroundPioneerRxWindow(out var foregroundHandle, out var foregroundBounds);

        // ROUND 3 FIX: the fallback rule needs its OWN, broader signal —
        // "does PioneerRx exist anywhere on the system" — independent of
        // whether it's currently in front. isRxScreenAttached and
        // hasForegroundPioneerWindow above already answer this for free
        // when either is true; DoesPioneerRxProcessExist (a single
        // process-name lookup, no window enumeration) is only called
        // when BOTH are false, so this stays cheap on the common tick
        // where Pioneer IS already known to be around. See
        // PioneerPresence.Exists (pure) and FallbackSeparateWindowRule's
        // own doc (unchanged — only the SIGNAL fed into it changes here).
        var pioneerExists = PioneerPresence.Exists(isRxScreenAttached, hasForegroundPioneerWindow, !isRxScreenAttached && !hasForegroundPioneerWindow && DoesPioneerRxProcessExist());

        // REVIEW FIX (invisible-app trap): PioneerRx doesn't exist
        // anywhere on the system (closed entirely) — both integrated
        // windows are about to hide below, which would otherwise leave
        // the WHOLE APP invisible with no affordance to recover (wait for
        // Pioneer, or switch back to Separate) or quit. Reveal the
        // separate window's own existing "Waiting for a PioneerRx..."
        // state instead — no new UI needed, and its own View toggle/
        // close button double as the recover/quit affordance. See
        // FallbackSeparateWindowRule for the pure edge-only Show/Hide
        // decision (never fights a pharmacist who manually re-hides it,
        // never hides a window they opened themselves via "Open full
        // view").
        ApplyFallbackDecision(FallbackSeparateWindowRule.Decide(isIntegratedMode: true, isPioneerAttached: pioneerExists, _fallbackSeparateWindowShown));

        if (!pioneerExists)
        {
            HideControlBoxIfShown();
            HideBoxesIfShown();
            return;
        }

        if (!hasForegroundPioneerWindow)
        {
            // ROUND 3 FIX: PioneerRx EXISTS but isn't the foreground app
            // right now (launched from a terminal that's still focused,
            // or the pharmacist alt-tabbed to something else briefly) —
            // hide the integrated UI quietly, WITHOUT popping the
            // fallback separate window (that's reserved for "Pioneer
            // doesn't exist at all", handled above). The control box/
            // boxes reappear on their own next tick once Pioneer regains
            // focus.
            HideControlBoxIfShown();
            HideBoxesIfShown();
            return;
        }

        // CONTROL BOX: reaching here already means
        // IntegratedVisibilityGate.ShouldShowControlBox(hasForegroundPioneerWindow)
        // is true (the !hasForegroundPioneerWindow branch above returned
        // early) — anchors to the narrow Rx-screen window when one's open
        // (same window either way, in practice — Pre-Check/Edit/New Rx
        // are all the same PioneerRx shell window with a different
        // title), otherwise to the foreground window's raw Win32 rect (no
        // field data needed just to position a box in its ribbon corner).
        var controlBoxHandle = isRxScreenAttached ? window!.NativeWindowHandle : foregroundHandle;
        var controlBoxBounds = isRxScreenAttached ? window!.WindowBounds : foregroundBounds;
        var isControlBoxMaximized = IsZoomed(controlBoxHandle);

        UpdateControlBox(controlBoxHandle, controlBoxBounds, isControlBoxMaximized);

        // BOXES: still requires the NARROW Rx-screen attach, that specific
        // window being foreground, maximized, and verified content. A
        // pharmacist parked on PioneerRx's queue/search screen
        // (isRxScreenAttached false) never draws boxes, since there's no
        // specific Rx's fields to draw them over.
        var isRxScreenForeground = isRxScreenAttached && GetForegroundWindow() == window!.NativeWindowHandle;
        var isRxScreenMaximized = isRxScreenAttached && IsZoomed(window!.NativeWindowHandle);
        var hasVerifiableContent = isRxScreenAttached && !_viewModel.HasNonEscriptMessage && _viewModel.Categories.Any(c => c.HasData);

        // ADDENDUM item 6 (tab gate): best-effort proxy for "PioneerRx is
        // actually on the Common tab right now" — no confirmed UIA
        // AutomationId exists for that outer tab strip (Common/Patient
        // Education/Interactions/Fill History/... — a DIFFERENT, outer
        // tab control than FieldMap.CenterTabControlAutomationId's
        // Dispense/Image/Escript/DUR-More strip, which lives INSIDE
        // Common) to check directly without guessing at a selector this
        // codebase has never validated against a live dump. If NONE of
        // the entered fields resolved to an on-screen rect this tick,
        // that's a reasonable signal the Common tab (where RxDetailsPanel
        // actually lives) isn't the active one — hide rather than risk
        // floating boxes over Patient Education/Interactions/etc.
        var hasResolvableFieldRects = _viewModel.Categories.SelectMany(c => c.Rows).Any(r => r.ScreenRect.HasValue);

        // ADDENDUM item 7 (priority — stale-box false-assurance hazard):
        // compare the Rx PioneerRx is showing RIGHT NOW against the Rx the
        // currently-displayed verdicts were actually computed for — see
        // RxIdentityGate's doc. This is the SAME check
        // HideBoxesIfRxIdentityChanged runs synchronously on
        // TitleChangeWatcher's near-instant event (see that method); this
        // copy is the ~250ms poll's safety net for whatever that
        // event-driven path might miss.
        var currentRxIdentity = isRxScreenAttached ? window!.RxNumber : null;
        var isRxIdentityStale = RxIdentityGate.IsStale(currentRxIdentity, _viewModel.CurrentVerdictsRxIdentity);

        var showBoxes = IntegratedVisibilityGate.ShouldShowBoxes(
            isRxScreenAttached, isRxScreenForeground, isRxScreenMaximized, hasVerifiableContent,
            hasResolvableFieldRects, _boxesHiddenByToggle) && !isRxIdentityStale;

        if (showBoxes)
        {
            UpdateBoxes(window!);
        }
        else
        {
            HideBoxesIfShown();
        }
    }

    /// <summary>
    /// ADDENDUM item 7 (priority): called SYNCHRONOUSLY from
    /// MainWindow.xaml.cs's TitleChangeWatcher callback — near-instant
    /// (~50ms debounce), BEFORE the resulting SafeWatchAsync/RefreshAsync
    /// has even started, let alone completed. Closes the gap between
    /// "PioneerRx's title changed" and "the next ~250ms poll tick would
    /// have caught it anyway": without this, a previous Rx's stale
    /// green/red boxes stay floating over the NEW prescription's
    /// (different) field positions for however long the refresh takes
    /// (UIA reads + an engine subprocess round-trip — easily 50-300ms+),
    /// which is exactly the "looks like it's already been checked" false-
    /// assurance hazard the owner flagged as this round's top priority.
    /// Only ever HIDES — never shows; TickCore's own regular gates are
    /// what bring the boxes back once the new Rx's verdicts arrive.
    /// Wrapped in try/catch for the same reason as Tick() (PioneerRxWindow.
    /// TryAttach can rethrow on a bad shared UIA session) — a failure here
    /// just means this ONE early-hide attempt is skipped; TickCore's own
    /// RxIdentityGate check on the very next poll tick is the safety net.
    /// </summary>
    public void HideBoxesIfRxIdentityChanged()
    {
        if (_settings.DisplayMode != DisplayMode.Integrated) return;

        try
        {
            using var window = PioneerRxWindow.TryAttach();
            var currentRxIdentity = window?.RxNumber;

            if (RxIdentityGate.IsStale(currentRxIdentity, _viewModel.CurrentVerdictsRxIdentity))
            {
                HideBoxesIfShown();
            }
        }
        catch (Exception)
        {
            // Best-effort only — see method doc.
        }
    }

    /// <summary>
    /// OWNER FEEDBACK (round 2, item 1): broader "is PioneerRx the app
    /// the pharmacist is currently looking at" check — unlike
    /// PioneerRxWindow.TryAttach (which only matches a Pre-Check/Edit/
    /// New-Rx TITLED window, needed for field-reading), this matches the
    /// CURRENT FOREGROUND window purely by its owning PROCESS name
    /// (FieldMap.TargetProcessNames — declared but previously unused,
    /// anticipating exactly this need), regardless of which PioneerRx
    /// screen it's showing. Returns the foreground window's raw physical
    /// bounds (plain Win32 GetWindowRect — no UIA needed, since only
    /// x/y/width/height are wanted here) for positioning the control box
    /// when there's no Pre-Check/Edit/New-Rx window to anchor to instead.
    /// Never throws: any failure (process exited between calls, access
    /// denied, etc.) is treated as "not PioneerRx" — Tick()'s own
    /// try/catch is a backstop, not the expected path here.
    /// </summary>
    private static bool TryGetForegroundPioneerRxWindow(out IntPtr hwnd, out Rectangle bounds)
    {
        hwnd = GetForegroundWindow();
        bounds = Rectangle.Empty;

        if (hwnd == IntPtr.Zero) return false;

        try
        {
            GetWindowThreadProcessId(hwnd, out var processId);
            using var process = Process.GetProcessById((int)processId);
            if (!FieldMap.TargetProcessNames.Any(name => string.Equals(process.ProcessName, name, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }
        catch
        {
            return false;
        }

        if (!GetWindowRect(hwnd, out var rect)) return false;

        bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        return true;
    }

    /// <summary>
    /// ROUND 3 FIX: does a PioneerRx process exist ANYWHERE on the
    /// system, foreground or not, minimized or not — a process-name
    /// lookup per FieldMap.TargetProcessNames (no window enumeration at
    /// all), only ever called from TickCore when both the narrow
    /// (isRxScreenAttached) and broad (hasForegroundPioneerWindow) checks
    /// have already come back false, so the common case (Pioneer already
    /// known to be around) never pays for this. This is the signal
    /// PioneerPresence.Exists combines with those other two to feed
    /// FallbackSeparateWindowRule — see that class's doc for why the
    /// fallback needs THIS question, not "is Pioneer in front right now".
    /// Checks each candidate name in order and stops at the first hit;
    /// every returned Process handle (for every name looked up, whether
    /// it matched or not) is disposed; any failure for a given name (WMI
    /// hiccup, etc.) is treated as "no match for that name" and moves on
    /// to the next, rather than aborting the whole check.
    /// </summary>
    private static bool DoesPioneerRxProcessExist()
    {
        foreach (var processName in FieldMap.TargetProcessNames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(processName);
            }
            catch
            {
                continue;
            }

            try
            {
                if (processes.Length > 0) return true;
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }

        return false;
    }

    /// <summary>Applies a FallbackSeparateWindowRule.Decide() result: updates the flag, then raises at most one of Show/HideSeparateWindowRequested per the decision.</summary>
    private void ApplyFallbackDecision(FallbackWindowDecision decision)
    {
        _fallbackSeparateWindowShown = decision.NewFallbackShown;
        if (decision.RaiseShow) ShowSeparateWindowRequested?.Invoke(this, EventArgs.Empty);
        if (decision.RaiseHide) HideSeparateWindowRequested?.Invoke(this, EventArgs.Empty);
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
        _controlBox.RefreshRequested += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        _controlBox.HideOverlayToggleRequested += (_, hidden) =>
        {
            // ITEM 2: update the session-only flag and re-evaluate
            // visibility IMMEDIATELY (don't wait for the next ~250ms
            // tick) — matches the existing pattern for every other
            // toggle in this app (Method/DisplayMode both trigger an
            // instant refresh/Tick rather than waiting for the poll).
            _boxesHiddenByToggle = hidden;
            Tick();
        };
        SyncToggles();
        return _controlBox;
    }

    private IntegratedBoxesWindow EnsureBoxesWindow() => _boxesWindow ??= new IntegratedBoxesWindow();

    private void UpdateControlBox(IntPtr windowHandle, Rectangle bounds, bool isMaximized)
    {
        var box = EnsureControlBox();
        var scale = DpiScaleFor(windowHandle);

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

        var scale = DpiScaleFor(window.NativeWindowHandle);

        var boxes = _viewModel.Categories
            .SelectMany(c => c.Rows)
            .Where(r => r.ScreenRect.HasValue)
            // ITEM 5: DAW gets no box at all unless it's wrong or actually
            // "in play" for this Rx — see DawBoxRule's doc. Every other
            // field is unaffected.
            .Where(r => r.FieldKey != "daw" || DawBoxRule.ShouldDrawBox(r.Status, r.EnteredValue, r.SourceValue))
            .Select(r => (r.ScreenRect!.Value, BoxColorMapper.IsGreenBox(r.Status)))
            .ToList();

        boxesWindow.SetBoxes(boxes, bounds.Location, scale, scale);
    }

    private static double DpiScaleFor(IntPtr windowHandle)
    {
        var dpi = GetDpiForWindow(windowHandle);
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
