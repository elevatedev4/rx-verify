using System;
using System.Collections.Generic;
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
/// Lazily constructs both windows on first need — Integrated is now the
/// default (see Models/OverlaySettings.cs DisplayMode), so the common
/// case DOES create them, but a pharmacist who deliberately switches to
/// and stays on Separate still pays zero cost for this feature beyond
/// Tick()'s one cheap enum-comparison early-out.
/// </summary>
public sealed class IntegratedOverlayCoordinator
{
    // CONTROL BOX anchor, relative to PioneerRx's own WindowBounds — see
    // the owner's reference screenshot: the ribbon band right of the last
    // toolbar group (roughly x 850-1490, y 60-155 in a 1928-wide
    // maximized window) sits empty. Kept as named DIP constants (not
    // inline numbers) specifically so they're easy to retune against a
    // real workstation without hunting through positioning math.
    private const double ControlBoxRightInsetDip = 510; // box's LEFT edge sits this far in from the window's RIGHT edge — round 4: bumped from 450 (420 + ~30px margin) to 510 (480 + ~30px margin) to keep pace with ControlBoxWidthDip's widening below, so the box's right edge doesn't run off the window's right edge. Round 9: ControlBoxWidthDip shrank back to 420 for the minimal-layout redesign, so the box's right edge now sits even further inside the window's right edge than before — still safe, no retune needed; this inset is untouched.
    private const double ControlBoxTopOffsetDip = 60;   // box's TOP edge sits this far down from the window's TOP edge
    private const double ControlBoxWidthDip = 420;      // must match ControlBoxWindow.xaml's Width — round 9: narrowed from 480 to 420 as part of the "minimal layout" redesign (2-row layout, icon-ish action buttons, no redundant "Source:"/"View:" labels) needing less horizontal room
    private const double ControlBoxHeightDip = 76;      // must match ControlBoxWindow.xaml's Height — round 9: shortened from 92 to 76 when the redesign collapsed the box from 3 rows to 2

    // ORDER MODE control-box anchor (owner's live pharmacy report,
    // 2026-08-14: "the recommended order pops up in a window above the
    // main Pioneer ... activating 'Order mode' ... should change the
    // layout of the box to make it fit where it's not blocking anything,
    // like just next to the 'Color legend' on the top right"). Distinct
    // from the Verify-mode constants above (which are UNCHANGED — "Verify
    // mode keeps the current layout/position" per spec): Pioneer's
    // "Color Legend" link hugs its own window's top-right corner just
    // below the Actions/Tools/Search/Reports/Analysis menu row (see
    // order-assist-Screenshot 2026-08-13 175418.png) — a much smaller,
    // higher band than where the Verify-mode box sits. NEEDS LIVE
    // CONFIRMATION on Will's real workstation (picked from the reference
    // screenshot's proportions against the SAME 1928-wide-window
    // assumption the Verify-mode constants above already use, not yet
    // verified against a live Pioneer window in Order mode).
    //
    // REVIEW FIX (pre-merge, 2026-08-14 — reviewer re-measured the
    // reference screenshot directly): order-assist-Screenshot
    // 2026-08-13 175418.png is 1917px wide; the Actions/Tools/Search/
    // Reports/Analysis menu row sits at roughly y≈33, and "Color Legend"
    // sits DISTINCTLY BELOW it at roughly y≈60 — the original
    // OrderModeControlBoxTopOffsetDip=34 landed the box on the menu row
    // itself, covering Pioneer's own menu, precisely what the owner asked
    // to avoid. Corrected to 60 (Color Legend's own row). Re-measured and
    // confirmed independently against the same screenshot before this fix.
    private const double OrderModeControlBoxRightInsetDip = 300; // box's LEFT edge this far in from the window's RIGHT edge — keeps clear of "Color Legend," which sits flush against the right edge itself
    private const double OrderModeControlBoxTopOffsetDip = 60;   // level with "Color Legend" itself, NOT the Actions/Tools/Search/Reports/Analysis menu row above it (see REVIEW FIX above) — NEEDS LIVE CONFIRMATION, see this constant group's own doc
    private const double OrderModeControlBoxWidthDip = 260;      // must match ControlBoxWindow.xaml's CompactOrderPanel sizing — small enough to sit beside Color Legend, not over it
    private const double OrderModeControlBoxHeightDip = 34;      // a single compact row — just the Mode dropdown, no toggles/buttons (see ControlBoxWindow.SetOrderAssistState)

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
    // See IsForegroundWindowOwnedByPioneerRx below.
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

    // ------------------------------------------------------------------
    // ROUND 7 ("the integrated control box moves down/over to a small
    // popup Pioneer opens above its main window ... it needs to stay put
    // at the top-right of the MAIN window"): replaces round 5's
    // GetAncestor(hwnd, GA_ROOTOWNER)-based owner walk — it only resolved
    // to the main window when the popup was OWNED, and PioneerRx opens
    // some top-level windows that aren't — with a POSITIVE identification
    // of PioneerRx's own main window via EnumWindows + MainWindowAnchorRule.
    // The GetAncestor call itself and its ForegroundAnchorRule decision
    // class (with its own tests) are both gone entirely now, not just
    // unused — see git history for round 5's original version of either.
    // See ResolveMainPioneerWindowAnchor and MainWindowAnchorRule's own
    // doc for the full design.
    // ------------------------------------------------------------------
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

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

    /// <summary>
    /// ROUND 7: the hwnd MainWindowAnchorRule.Resolve chose LAST tick as
    /// PioneerRx's main window, or IntPtr.Zero before anything's been
    /// chosen yet (app just launched, or a previous tick found nothing
    /// eligible). Fed back into Resolve every tick — see
    /// ResolveMainPioneerWindowAnchor — so the control box keeps
    /// anchoring to the SAME window even while a same-process popup or an
    /// entirely different app is foreground. Deliberately never reset
    /// except by Resolve itself finding the cached window no longer
    /// eligible; a brief gap in Pioneer being foreground must NOT clear
    /// this (that would defeat the whole point of stickiness).
    /// </summary>
    private IntPtr _cachedMainWindowHandle = IntPtr.Zero;

    /// <summary>
    /// REVIEWER FIX (round 7 follow-up — per-tick cost): pid -&gt; "is this
    /// a PioneerRx process" answers, cached ACROSS ticks (never cleared)
    /// so EnumeratePioneerTopLevelWindows pays for Process.GetProcessById
    /// (which opens a process handle) at most once per UNIQUE pid ever,
    /// not once per top-level window per tick. Never proactively evicted,
    /// deliberately kept simple: a stale TRUE entry self-corrects on its
    /// own the moment that PioneerRx process exits, since EnumWindows
    /// simply stops reporting any hwnd for that pid at all — nothing left
    /// to look the cache up for. A stale FALSE surviving a pid being
    /// reused by a genuinely different (possibly Pioneer) process is a
    /// real but vanishingly rare edge case, not worth extra bookkeeping
    /// for a ~250ms-tick UI-positioning feature. See IsPioneerProcessId.
    /// </summary>
    private readonly Dictionary<uint, bool> _pidIsPioneerCache = new();

    /// <summary>Raised when the pharmacist changes the Method toggle FROM THE CONTROL BOX — MainWindow.xaml.cs handles this the same way as its own Source radio buttons (persist + refresh), then calls SyncToggles() so both toggles stay in lockstep.</summary>
    public event EventHandler<VerificationMethod>? MethodToggleRequested;

    /// <summary>Item 1: raised when the control box's Refresh button is clicked — MainWindow.xaml.cs handles this identically to its own Refresh button (SafeRefreshAsync + SafeTickIntegratedOverlay).</summary>
    public event EventHandler? RefreshRequested;

    /// <summary>Item 8: raised when the control box's corner X button is clicked — MainWindow.xaml.cs handles this by calling its own Close(), routing through its EXISTING Closed cleanup path rather than this coordinator duplicating any shutdown logic.</summary>
    public event EventHandler? CloseApplicationRequested;

    /// <summary>Raised when the classic separate window (MainWindow) should become visible — either DisplayMode switched to Separate, or the control box's "Open full view" button was clicked (which does NOT change DisplayMode — see SetDisplayMode).</summary>
    public event EventHandler? ShowSeparateWindowRequested;

    /// <summary>Raised when DisplayMode switched to Integrated — MainWindow.xaml.cs hides itself.</summary>
    public event EventHandler? HideSeparateWindowRequested;

    /// <summary>
    /// Raised by the control box's "Copy (safe)" button — MainWindow.xaml.cs
    /// handles this identically to its own "Copy logs (no HIPAA)" button
    /// (BuildCurrentLogBlob(redactPatient: true) + clipboard +
    /// ButtonFeedback.FlashSuccessAsync on the same Button that was
    /// clicked). 2026-08-13 (RXVERIFY-TROUBLESHOOT): CopyLogsRequested
    /// (the PHI-including "Copy" button's event) was removed along with
    /// the button itself — this is the only copy-logs event now.
    /// </summary>
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

    /// <summary>Raised when the poll-driven right-click detection (IntegratedBoxesWindow.PollCursorForHover — see HoverStateMachine, fix/hover-popup-live branch) fires over a verdict bar's hotspot — MainWindow.xaml.cs handles this by opening Integrated/ReportErrorWindow prefilled with the field's current verdict data. Wired once, in EnsureBoxesWindow, at construction. Payload extended (RXVERIFY-TROUBLESHOOT, 2026-08 round 2) with the physical click point — see ReportErrorRequestInfo's own doc — so the dialog can be positioned on the correct monitor of a multi-monitor workstation.</summary>
    public event EventHandler<ReportErrorRequestInfo>? ReportErrorRequested;

    /// <summary>
    /// Raised with the NEW checked state whenever the control box's
    /// "Order Assist" checkbox is clicked — see Integrated/ControlBoxWindow.cs
    /// OrderAssistToggleRequested. Deliberately a PLAIN bool, exactly like
    /// every other *Requested event on this class: this coordinator never
    /// references any OrderAssist.* type and never will — MainWindow.xaml.cs
    /// (the app's composition root) is the only place that both knows
    /// about this event AND owns an OrderAssist.OrderAssistCoordinator
    /// instance, so the verify flow's own composition class here stays
    /// completely decoupled from that separate, independently-toggled
    /// module (see OrderAssistCoordinator's class doc for why that
    /// decoupling matters — turn-off/split-off-as-its-own-module later).
    /// </summary>
    public event EventHandler<bool>? OrderAssistToggleRequested;

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
        // Plain bool read straight from settings — see
        // OrderAssistToggleRequested's doc for why this is the only
        // OrderAssist-related state this class ever touches.
        _controlBox?.SetOrderAssistState(_settings.OrderAssistEnabled);
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
        // IsForegroundWindowOwnedByPioneerRx. This is what gates the
        // CONTROL BOX. It must NOT also gate the fallback-to-separate-window
        // decision below (round-3 fix — see PioneerPresence's doc for the
        // bug that caused: conflating "not in front right now" with
        // "doesn't exist" popped the fallback window at every launch and
        // on every alt-tab away from Pioneer).
        //
        // ROUND 7: this ONLY answers "is some PioneerRx window in front
        // right now" — a deliberately separate question from "WHICH of
        // PioneerRx's windows should the control box anchor to" (see
        // ResolveMainPioneerWindowAnchor below). Conflating those two
        // questions via GetAncestor/GA_ROOTOWNER was round 5's bug.
        var hasForegroundPioneerWindow = IsForegroundWindowOwnedByPioneerRx();

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
        // early).
        //
        // ROUND 7: anchors to PioneerRx's POSITIVELY-identified, STICKY
        // main window (see ResolveMainPioneerWindowAnchor and
        // MainWindowAnchorRule's own doc) rather than the narrow Rx-screen
        // window or the raw foreground rect — a popup Pioneer opens above
        // its main window (owned or not) never moves this anchor, since
        // it's never even a candidate for "main window" unless it's
        // itself maximized.
        var mainWindowAnchor = ResolveMainPioneerWindowAnchor();

        if (mainWindowAnchor is null)
        {
            // No eligible PioneerRx top-level window at all right now
            // (e.g. every one is momentarily minimized/invisible) —
            // nothing sane to anchor the control box to.
            HideControlBoxIfShown();
        }
        else
        {
            var (controlBoxHandle, controlBoxBounds) = mainWindowAnchor.Value;
            var isControlBoxMaximized = IsZoomed(controlBoxHandle);

            UpdateControlBox(controlBoxHandle, controlBoxBounds, isControlBoxMaximized);
        }

        // MODE EXCLUSIVITY (owner's live pharmacy report, 2026-08-14:
        // "activating 'Order mode' instead of Verify mode ... make sure
        // that the logic will work"): while Order Assist is enabled, the
        // verify boxes/hover layer is suppressed entirely — see
        // VerifyModeGate's own doc. The narrow Rx-screen `window` attach
        // above is still paid for regardless of mode (it already feeds
        // pioneerExists/the fallback-separate-window trap, unconditionally,
        // before this point), but every ADDITIONAL per-field UIA/tab-gate
        // read below (CommonTabGate, RxIdentityGate, field-rect
        // resolution) is real, avoidable cost while in Order mode, whose
        // result could never be shown anyway. Switching back to Verify
        // mode needs no separate "resume" step — this just stops
        // short-circuiting on the very next tick.
        if (VerifyModeGate.ShouldSuppressVerifyBoxes(_settings.OrderAssistEnabled))
        {
            HideBoxesIfShown();
            return;
        }

        // BOXES: still requires the NARROW Rx-screen attach, that specific
        // window being foreground, maximized, and verified content. A
        // pharmacist parked on PioneerRx's queue/search screen
        // (isRxScreenAttached false) never draws boxes, since there's no
        // specific Rx's fields to draw them over.
        var isRxScreenForeground = isRxScreenAttached && GetForegroundWindow() == window!.NativeWindowHandle;
        var isRxScreenMaximized = isRxScreenAttached && IsZoomed(window!.NativeWindowHandle);
        var hasVerifiableContent = isRxScreenAttached && !_viewModel.HasNonEscriptMessage && _viewModel.Categories.Any(c => c.HasData);

        // TAB GATE (owner report — the round 4 addendum item 6 proxy
        // below did NOT actually hide boxes when the pharmacist switched
        // off the outer Common tab: RxDetailsPanel's field elements
        // evidently keep non-empty BoundingRectangles even while a
        // different outer tab, e.g. Patient Education/Interactions/Fill
        // History, is showing). Uia/CommonTabGate.cs now reads a
        // confirmed signal instead — the outer Common TabItem's
        // SelectionItemPattern.IsSelected (PRIMARY), falling back to the
        // cntCommonTab pane's presence/IsOffscreen (SECONDARY, see
        // FieldMap.OuterCommonPaneAutomationId) — computed once per tick
        // here and passed into IntegratedVisibilityGate.ShouldShowBoxes,
        // which short-circuits to hidden on CommonTabState.Off regardless
        // of every other input. Only meaningful while a specific Rx
        // screen is attached (there's nothing to walk otherwise).
        var commonTabState = isRxScreenAttached
            ? CommonTabGate.DetermineState(new UiaTreeWalker(window!.WindowElement), window!.NativeWindowHandle)
            : CommonTabState.Unknown;

        // ADDENDUM item 6 (tab gate) — ORIGINAL best-effort proxy for
        // "PioneerRx is actually on the Common tab right now", kept as
        // the FALLBACK layer for whenever commonTabState above comes back
        // Unknown (this Pioneer version's tree shape differs from the two
        // confirmed dumps) — see IntegratedVisibilityGate.ShouldShowBoxes.
        // No confirmed UIA AutomationId existed for the outer tab strip
        // itself when this proxy was written (Common/Patient Education/
        // Interactions/Fill History/... — a DIFFERENT, outer tab control
        // than FieldMap.CenterTabControlAutomationId's Dispense/Image/
        // Escript/DUR-More strip, which lives INSIDE Common); two of its
        // children are confirmed now (see FieldMap.OuterCommonTabNamePrefix/
        // OuterCommonPaneAutomationId), which is what CommonTabGate uses.
        // If NONE of the entered fields resolved to an on-screen rect
        // this tick, that's still a reasonable secondary signal the
        // Common tab isn't the active one.
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
            hasResolvableFieldRects, _boxesHiddenByToggle, commonTabState) && !isRxIdentityStale;

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
    /// (FieldMap.TargetProcessNames), regardless of which PioneerRx
    /// screen it's showing. Never throws: any failure (process exited
    /// between calls, access denied, etc.) is treated as "not PioneerRx"
    /// — Tick()'s own try/catch is a backstop, not the expected path
    /// here.
    ///
    /// ROUND 7: this is now ONLY the show/hide gating signal — it says
    /// nothing about WHERE to anchor. See ResolveMainPioneerWindowAnchor
    /// for that (a deliberately separate question — see
    /// MainWindowAnchorRule's doc for the round-5 bug that came from
    /// conflating the two).
    /// </summary>
    private static bool IsForegroundWindowOwnedByPioneerRx()
    {
        var hwnd = GetForegroundWindow();
        return hwnd != IntPtr.Zero && IsOwnedByPioneerRx(hwnd);
    }

    /// <summary>
    /// True when <paramref name="hwnd"/> belongs to a process named in
    /// FieldMap.TargetProcessNames — used only by
    /// IsForegroundWindowOwnedByPioneerRx above, which does exactly ONE
    /// of these per tick (the single current foreground hwnd), so an
    /// uncached Process.GetProcessById here is fine. EnumeratePioneerTopLevelWindows
    /// below has a very different cost profile (up to one call per
    /// top-level window on the whole desktop, every ~250ms tick) and uses
    /// its own cached IsPioneerProcessId instead — see that method's doc.
    /// </summary>
    private static bool IsOwnedByPioneerRx(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out var processId);
            using var process = Process.GetProcessById((int)processId);
            return FieldMap.TargetProcessNames.Any(name => string.Equals(process.ProcessName, name, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Cached wrapper around the same FieldMap.TargetProcessNames check as IsOwnedByPioneerRx, keyed by pid instead of hwnd — see _pidIsPioneerCache's own doc for why EnumeratePioneerTopLevelWindows needs this cached and IsOwnedByPioneerRx doesn't.</summary>
    private bool IsPioneerProcessId(uint processId)
    {
        if (_pidIsPioneerCache.TryGetValue(processId, out var cached)) return cached;

        bool isPioneer;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            isPioneer = FieldMap.TargetProcessNames.Any(name => string.Equals(process.ProcessName, name, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            isPioneer = false;
        }

        _pidIsPioneerCache[processId] = isPioneer;
        return isPioneer;
    }

    /// <summary>
    /// ROUND 7: resolves PioneerRx's MAIN window (see MainWindowAnchorRule's
    /// own doc for the full design) and returns its current hwnd/rect, or
    /// null if none is eligible right now. Enumerates every CURRENT
    /// top-level window owned by a PioneerRx process into plain Win32 data
    /// (EnumeratePioneerTopLevelWindows) and hands it, together with
    /// whichever hwnd was chosen LAST tick (_cachedMainWindowHandle), to
    /// MainWindowAnchorRule.Resolve — that cached handle is what makes the
    /// anchor STICKY across ticks. Updates _cachedMainWindowHandle to
    /// whatever Resolve returns (IntPtr.Zero if nothing was eligible) so
    /// the NEXT tick's Resolve call has the right memory.
    /// </summary>
    private (IntPtr Handle, Rectangle Bounds)? ResolveMainPioneerWindowAnchor()
    {
        var candidates = EnumeratePioneerTopLevelWindows();
        var anchor = MainWindowAnchorRule.Resolve(_cachedMainWindowHandle, candidates);
        _cachedMainWindowHandle = anchor?.Handle ?? IntPtr.Zero;
        return anchor is { } a ? (a.Handle, a.Bounds) : null;
    }

    /// <summary>
    /// EnumWindows over every top-level window on the system, keeping only
    /// the ones owned by a PioneerRx process and reading the plain Win32
    /// state MainWindowAnchorRule.Candidate needs for each: minimized/
    /// maximized state and its rect. A window whose rect can't be read
    /// (GetWindowRect failure) is skipped entirely rather than included
    /// with a degenerate rect — same "never trust a failed read" posture
    /// as MainWindowAnchorRule.IsSaneWindowRect's own degenerate-but-
    /// succeeded case.
    ///
    /// REVIEWER FIX (round 7 follow-up — per-tick cost): this callback
    /// runs for EVERY top-level window on the desktop, every ~250ms tick,
    /// so it's ordered to be as cheap as possible for the common case
    /// (most top-level windows on a real desktop — message-only windows,
    /// hidden helper windows, etc. — are invisible and aren't PioneerRx
    /// at all): IsWindowVisible is checked FIRST, before any process
    /// lookup, since it's a single cheap Win32 call with no process
    /// handle involved; only visible windows pay for
    /// GetWindowThreadProcessId + the cached IsPioneerProcessId lookup.
    /// Selection semantics are unchanged for visible windows — every
    /// visible, PioneerRx-owned window with a readable rect still becomes
    /// a candidate, with IsVisible now simply hardcoded true rather than
    /// re-queried, since reaching this point already proved it.
    /// </summary>
    private List<MainWindowAnchorRule.Candidate> EnumeratePioneerTopLevelWindows()
    {
        var candidates = new List<MainWindowAnchorRule.Candidate>();

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true; // keep enumerating — cheapest possible check, no process lookup at all

            GetWindowThreadProcessId(hWnd, out var processId);
            if (!IsPioneerProcessId(processId)) return true; // keep enumerating

            if (!GetWindowRect(hWnd, out var rect)) return true; // keep enumerating

            candidates.Add(new MainWindowAnchorRule.Candidate(
                hWnd,
                IsVisible: true, // already confirmed above
                IsMinimized: IsIconic(hWnd),
                IsMaximized: IsZoomed(hWnd),
                Bounds: Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom)));

            return true; // keep enumerating — need every candidate, not just the first
        }, IntPtr.Zero);

        return candidates;
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
        _controlBox.CopyLogsNoHipaaRequested += (_, button) => CopyLogsNoHipaaRequested?.Invoke(this, button);
        _controlBox.OpenSeparateWindowRequested += (_, _) => ShowSeparateWindowRequested?.Invoke(this, EventArgs.Empty);
        _controlBox.RefreshRequested += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        _controlBox.CloseApplicationRequested += (_, _) => CloseApplicationRequested?.Invoke(this, EventArgs.Empty);
        _controlBox.OrderAssistToggleRequested += (_, enabled) => OrderAssistToggleRequested?.Invoke(this, enabled);
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

    private IntegratedBoxesWindow EnsureBoxesWindow()
    {
        if (_boxesWindow is not null) return _boxesWindow;

        _boxesWindow = new IntegratedBoxesWindow();
        _boxesWindow.ReportErrorRequested += (_, info) => ReportErrorRequested?.Invoke(this, info);
        return _boxesWindow;
    }

    /// <summary>
    /// RXVERIFY-TROUBLESHOOT (2026-08): forwards MainWindow's
    /// ReportErrorWindow open/close lifetime down to the boxes window's
    /// poll — see IntegratedBoxesWindow.SetDialogOpen's own doc for why
    /// this guards against stacking a second dialog on a second
    /// right-click. A no-op if the boxes window was never even created
    /// (nothing has ever been hovered/right-clicked yet, so there's
    /// nothing to guard) — deliberately does NOT call EnsureBoxesWindow
    /// itself; MainWindow always calls this around a dialog that only
    /// ever got opened BECAUSE the boxes window's own poll already fired
    /// ReportErrorRequested, so it's guaranteed to already exist by then.
    /// </summary>
    public void SetReportDialogOpen(bool open) => _boxesWindow?.SetDialogOpen(open);

    private void UpdateControlBox(IntPtr windowHandle, Rectangle bounds, bool isMaximized)
    {
        var box = EnsureControlBox();
        var scale = DpiScaleFor(windowHandle);

        // ORDER MODE LAYOUT (owner spec, 2026-08-14) — see the
        // OrderModeControlBox*Dip constants' own doc. A plain bool read
        // straight from settings, same as everywhere else this class
        // touches OrderAssistEnabled (see OrderAssistToggleRequested's doc).
        var orderModeActive = _settings.OrderAssistEnabled;
        var rightInsetDip = orderModeActive ? OrderModeControlBoxRightInsetDip : ControlBoxRightInsetDip;
        var topOffsetDip = orderModeActive ? OrderModeControlBoxTopOffsetDip : ControlBoxTopOffsetDip;
        var widthDip = orderModeActive ? OrderModeControlBoxWidthDip : ControlBoxWidthDip;
        var heightDip = orderModeActive ? OrderModeControlBoxHeightDip : ControlBoxHeightDip;

        var physicalX = bounds.Right - (int)Math.Round(rightInsetDip * scale);
        var physicalY = bounds.Top + (int)Math.Round(topOffsetDip * scale);
        var physicalWidth = (int)Math.Round(widthDip * scale);
        var physicalHeight = (int)Math.Round(heightDip * scale);

        box.SetMaximizedGuardState(isMaximized);
        // Owner request (2026-08-13): "Remove the counter showing the
        // accurate/errors. that is not needed on the top right box." —
        // BuildStatusSummary (the "N✓ M✗" glyph counter builder) is
        // removed entirely; only the status/timing message is set now.
        box.SetStatusMessage(_viewModel.StatusMessage);

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
            // REVIEWER BLOCKER FIX (layer 3, belt-and-suspenders): forced
            // BEFORE Show() — see IntegratedBoxesWindow.ForceHitTestTransparent's
            // doc. HideAndResetHover (below) and PollCursorForHover's own
            // IsVisible check already prevent a hidden window from
            // drifting non-transparent, but this window must never
            // surface non-transparent the moment it becomes visible
            // regardless of which of those ran first.
            boxesWindow.ForceHitTestTransparent();
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

        // HOVER/RIGHT-CLICK AFFORDANCE: reportingEnabled gates ONLY
        // whether a poll-detected right-click turns into a
        // ReportErrorRequested (see IntegratedBoxesWindow.PollCursorForHover,
        // fix/hover-popup-live branch) — the hover popup itself is
        // unconditional. See OverlaySettings.RxVerifyReportKey's doc for
        // why an unset key suppresses the affordance entirely rather than
        // opening a dialog that could only ever queue locally forever.
        var reportingEnabled = !string.IsNullOrWhiteSpace(_settings.RxVerifyReportKey);

        var boxes = _viewModel.Categories
            .SelectMany(c => c.Rows)
            .Where(r => r.ScreenRect.HasValue)
            // ITEM 5: DAW gets no box at all unless it's wrong or actually
            // "in play" for this Rx — see DawBoxRule's doc. Every other
            // field is unaffected.
            .Where(r => r.FieldKey != "daw" || DawBoxRule.ShouldDrawBox(r.Status, r.EnteredValue, r.SourceValue))
            .Select(r => (
                r.ScreenRect!.Value,
                BoxColorMapper.IsGreenBox(r.Status),
                new VerdictFieldInfo(r.FieldKey, r.DisplayName, r.Status, r.SourceValue, r.EnteredValue, r.Explanation, r.ReasonCode)))
            .ToList();

        boxesWindow.SetBoxes(boxes, bounds.Location, scale, scale, reportingEnabled);
    }

    private static double DpiScaleFor(IntPtr windowHandle)
    {
        var dpi = GetDpiForWindow(windowHandle);
        return dpi > 0 ? dpi / 96.0 : 1.0;
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

        // REVIEWER BLOCKER FIX: HideAndResetHover (not a bare Hide()) —
        // see that method's doc and HoverPollDecision's class doc for the
        // stale-hotspot / stuck-non-transparent failure mode this closes.
        _boxesWindow?.HideAndResetHover();
        _boxesShown = false;
    }

    /// <summary>
    /// Called from MainWindow's Closed handler — releases both integrated
    /// windows if they were ever created. Item 8 ("shutdown runs cleanly
    /// ... window closes shouldn't throw on exit"): each Close() is
    /// wrapped independently so a problem closing one window (e.g.
    /// ControlBoxWindow's own hotkey-unregister cleanup — see its
    /// OnClosed, which already guards itself, but this is a second layer)
    /// can never stop the other from closing or block MainWindow's own
    /// Closed handler (which calls this) from finishing its own shutdown
    /// sequence, including the final Application.Current.Shutdown().
    /// </summary>
    public void Shutdown()
    {
        try { _boxesWindow?.Close(); } catch { /* best-effort only */ }
        try { _controlBox?.Close(); } catch { /* best-effort only */ }
    }
}
