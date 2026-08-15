using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using RxVerifyOverlay.Ocr;

namespace RxVerifyOverlay.Integrated;

/// <summary>
/// The click-through verdict-boxes layer for INTEGRATED display mode —
/// see Integrated/IntegratedOverlayCoordinator.cs, the only class that
/// creates/drives this window. Positioned exactly over PioneerRx's window
/// bounds (in PHYSICAL pixels, via RepositionPhysical — see
/// NativeWindowPositioning.cs) every refresh tick; its child boxes are
/// laid out in DIPs relative to its own top-left (see DpiRectConverter).
/// </summary>
public sealed partial class IntegratedBoxesWindow : Window
{
    // ------------------------------------------------------------------
    // CLICK-THROUGH (spec hard requirement — the pharmacist must be able
    // to type/click through this window into PioneerRx underneath it,
    // with zero difference in behavior). WPF has no first-class API for
    // this; the standard, well-documented approach is these three
    // extended window styles applied directly via Win32, as soon as the
    // native HWND exists (OnSourceInitialized).
    // ------------------------------------------------------------------
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020; // invisible to mouse hit-testing — clicks pass through to whatever is underneath
    private const int WS_EX_LAYERED = 0x00080000;      // required for WS_EX_TRANSPARENT to actually take effect (WPF's AllowsTransparency already makes this a layered window under the hood, but that's an implementation detail this doesn't rely on)
    private const int WS_EX_NOACTIVATE = 0x08000000;   // never steals keyboard focus/activation from PioneerRx, even momentarily on Show()
    private const int WS_EX_TOOLWINDOW = 0x00000080;   // keeps this out of Alt-Tab / the taskbar switcher — it's a pure overlay, never a real window to switch to

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    // ------------------------------------------------------------------
    // HOVER/RIGHT-CLICK AFFORDANCE:
    // WS_EX_TRANSPARENT above is a WHOLE-WINDOW Win32 style — while it's
    // set, Windows routes every mouse message straight to whatever is
    // underneath, and WPF never sees so much as a MouseMove for this
    // window. There is no WPF- or even per-element Win32 way to make ONE
    // child region interactive while the rest of the same top-level
    // window stays click-through — the extended style applies to the
    // whole HWND.
    //
    // The fix: poll the cursor's OS-level screen position on a short
    // timer (_hoverPollTimer) and dynamically CLEAR WS_EX_TRANSPARENT only
    // while the cursor sits over one of the current per-field verdict-bar
    // hotspots (_hotspots, rebuilt every SetBoxes call), restoring it the
    // instant the cursor is elsewhere. This is what makes the Cursor
    // property on each hotspot Border actually change on hover (confirmed
    // working in live testing) — the hotspot Border elements (AddHotspot)
    // are the only hit-testable content in the canvas (see BoxCanvas's
    // XAML — its own Background is deliberately unset so empty space
    // never swallows a click), so a click that lands outside every
    // hotspot but still within this poll's coarse "clear transparency"
    // window has nothing to hit and is effectively a no-op rather than a
    // stray interaction with Pioneer.
    //
    // KNOWN, ACCEPTED TRADEOFF (flagged, not silently introduced): this
    // narrows the "spec hard requirement" click-through guarantee at the
    // class doc's top from "always, everywhere" to "everywhere except the
    // ~5-DIP-wide verdict bars themselves, and only while the cursor is
    // already sitting on one" — there was no way to add a hover affordance
    // ON the bars without SOME carve-out. The poll interval
    // (HoverPollIntervalMs) bounds how stale that carve-out's edges can be;
    // a click landing exactly as the cursor crosses a hotspot's boundary
    // could in principle be swallowed instead of reaching Pioneer for up
    // to one poll interval. WS_EX_NOACTIVATE/WS_EX_TOOLWINDOW are never
    // toggled (only the TRANSPARENT bit) — hovering/clicking a bar must
    // still never steal keyboard focus/activation from Pioneer.
    //
    // REDESIGN (owner live-test feedback, fix/hover-popup-live branch):
    // this class ORIGINALLY drove the hover tooltip and "Report error…"
    // affordance off WPF's own ToolTipService/ContextMenu, attached to
    // each hotspot Border. Live testing on the owner's PC showed that
    // dead: "hover shows a different cursor but no popup with the info.
    // Right click doesn't do anything." The Cursor-changing above DOES
    // work — it's a simpler, lower-level WM_SETCURSOR-driven mechanism —
    // but ToolTipService's delayed-show timer chain and ContextMenu's
    // open logic are both documented to be unreliable on a topmost,
    // WS_EX_NOACTIVATE-styled window. BOTH are now retired entirely:
    // AddHotspot no longer sets ToolTip/ContextMenu at all — see
    // HoverStateMachine (pure dwell/right-click detection, driven by this
    // SAME poll) and HoverPopupWindow (a custom, always-click-through
    // popup this class shows/hides itself, never WPF's ToolTipService).
    // Right-click detection uses GetAsyncKeyState(VK_RBUTTON) rather than
    // a WPF PreviewMouseRightButtonDown handler for the same reliability
    // reason: GetAsyncKeyState reads the live hardware button state
    // directly from the OS, with no dependency on WPF actually routing an
    // input event to this window at all — the same "OS-level poll,
    // deterministic under exotic window styles" property that already
    // made GetCursorPos's cursor-position check reliable, unlike the
    // WPF-event-dependent alternative. See PollCursorForHover.
    //
    // RXVERIFY-TROUBLESHOOT (2026-08, owner live-test follow-up: "hover
    // is working now but the right click isn't"): the edge-detected
    // press/release comparison this needs was already correct here (see
    // HoverStateMachine, now delegating to the extracted RightClickDetector
    // for its own tests) — what was missing was (1) a dialog-open guard
    // so a second right-click while ReportErrorWindow is already open
    // can't stack a second one (_reportDialogOpen/SetDialogOpen below),
    // and (2) the report dialog itself never explicitly Activate()'d
    // after Show() — see MainWindow.xaml.cs OpenReportErrorDialog's doc
    // for why a process whose OWN windows are all WS_EX_NOACTIVATE can
    // have its one real, activatable window denied real foreground focus
    // by Windows unless nudged. The hover popup is also force-hidden the
    // instant a right-click fires (owner ask: its info reappears inside
    // the report dialog instead) — see HoverStateMachine.Update's
    // override.
    // ------------------------------------------------------------------
    private const int HoverPollIntervalMs = 60;

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    /// <summary>High-order bit set means the key/button is currently down — see PollCursorForHover's IsRightButtonDown check. Read fresh every poll tick; this is the SAME OS-level state a low-level mouse hook would see, with no dependency on which window (if any) currently has focus/activation/mouse capture.</summary>
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const int VK_RBUTTON = 0x02;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    /// <summary>Managed-side mirror of whether WS_EX_TRANSPARENT is currently set, so PollCursorForHover can skip the SetWindowLong round-trip on every tick when nothing changed (only ever toggled by SetHitTestTransparent). Starts true — matches the baseline OnSourceInitialized establishes.</summary>
    private bool _hitTestTransparent = true;

    /// <summary>The current tick's per-field hotspot rects, in the same window-relative DIP space as VerdictBarGeometry's bars — rebuilt every SetBoxes call, read by PollCursorForHover via CursorHitTest.FindContainingRectIndex. Empty (never null) before the first SetBoxes call. Always the SAME length and index order as _hotspotFields.</summary>
    private readonly List<DipRect> _hotspots = new();

    /// <summary>The VerdictFieldInfo for each entry in _hotspots, same index — kept as a separate parallel list (rather than combining into one List&lt;(DipRect, VerdictFieldInfo)&gt;) so CursorHitTest/HoverPollDecision's existing IReadOnlyList&lt;DipRect&gt;-only signatures don't need to change. Rebuilt alongside _hotspots in every AddHotspot call.</summary>
    private readonly List<VerdictFieldInfo> _hotspotFields = new();

    /// <summary>OverlaySettings.RxVerifyReportKey is non-empty, captured from SetBoxes' own reportingEnabled parameter — read by PollCursorForHover when a right-click transition fires, gating whether ReportErrorRequested is actually raised (see that property's doc).</summary>
    private bool _reportingEnabled;

    /// <summary>
    /// True while MainWindow's ReportErrorWindow is currently open —
    /// set/cleared via SetDialogOpen, called from
    /// IntegratedOverlayCoordinator (itself called from MainWindow around
    /// the dialog's Show/Closed) — see RightClickDetector's own doc for
    /// why this is fed into HoverStateMachine as a plain per-tick sample
    /// field rather than something this class or the state machine
    /// remembers on its own: a second right-click while the dialog is
    /// already open must not stack a second one (RXVERIFY-TROUBLESHOOT,
    /// suspect #2 — "a guard variable left stuck"), and the caller
    /// resetting this to false the instant the dialog closes is what
    /// guarantees the guard can never get stuck true.
    /// </summary>
    private bool _reportDialogOpen;

    /// <summary>
    /// True while ShowReportingDisabledNotice's MessageBox is currently up
    /// — feat/report-key-delivery. MessageBox.Show pumps its own nested
    /// message loop (same mechanic as ReportErrorWindow's ShowDialog
    /// above), which means THIS window's own _hoverPollTimer can still
    /// tick while it's open — without this guard a pharmacist repeatedly
    /// right-clicking the same hotspot while the notice is already
    /// showing could stack a second (and third...) copy of it. Set/
    /// cleared entirely within ShowReportingDisabledNotice itself (the
    /// call is synchronous), unlike _reportDialogOpen above, which is
    /// mirrored from MainWindow around an async dialog lifetime.
    /// </summary>
    private bool _reportingDisabledNoticeShowing;

    private readonly DispatcherTimer _hoverPollTimer;

    /// <summary>Pure dwell/right-click detection — see HoverStateMachine's own class doc for the full design. Reset (not replaced) on every "nothing to hover" early-out, see PollCursorForHover/HideAndResetHover.</summary>
    private readonly HoverStateMachine _hoverStateMachine = new();

    /// <summary>Lazily created on first use (EnsurePopupWindow) — most refresh ticks never need it (cursor isn't dwelling on a bar), so a pharmacist who never hovers a verdict bar never pays for it.</summary>
    private HoverPopupWindow? _popupWindow;

    private System.Drawing.Point _lastWindowOriginPhysical;
    private double _lastDpiScaleX = 1.0;
    private double _lastDpiScaleY = 1.0;

    // ------------------------------------------------------------------
    // RIGHT-CLICK DIAGNOSTICS (RXVERIFY-TROUBLESHOOT, 2026-08 round 2 —
    // owner: "Alt+Tab shows NO Report error window anywhere, the dialog
    // NEVER opens"): the previous round's Activate()/Topmost fix targets
    // MainWindow's dialog, which only ever runs if the chain gets that
    // far — these three fields make every step of the chain (raw button
    // state -> hotspot hit-test -> detector fire -> event raise) visible
    // in OcrLogger's plain-text log (%TEMP%\VerifyOCR\ocr-*.log) with NO
    // PHI (button/bool/index state only), so the NEXT right-click
    // produces an unambiguous trail instead of another "nothing happened"
    // report. See PollCursorForHover for where each is read/logged.
    // ------------------------------------------------------------------

    /// <summary>Edge-tracks the RAW VK_RBUTTON state independent of hotspot/dialog gating, purely so the first few presses of a session get logged REGARDLESS of whether the cursor happens to be over a hotspot — proves (or disproves) that GetAsyncKeyState sees the physical button at all on this machine, which the gated/hotspot-scoped RightClickDetector alone can never show.</summary>
    private bool _diagRawButtonPrevDown;

    /// <summary>Caps the raw-transition diagnostic to the first few presses per session (see _diagRawButtonPrevDown) — once GetAsyncKeyState is proven to see the button, logging every subsequent press forever adds nothing and just grows the log.</summary>
    private int _diagRawButtonLogCount;

    private const int DiagRawButtonLogLimit = 3;

    /// <summary>Null until the first poll tick with hotspots to test against — lets the hotspot ENTER/LEAVE transition log (poll-level state, logged only on change per the troubleshooting brief) distinguish "never checked yet" from "checked and currently false".</summary>
    private bool? _diagLastOverHotspot;

    /// <summary>Raised when the poll detects a right-click press (GetAsyncKeyState transition) while the cursor is over a hotspot — IntegratedOverlayCoordinator forwards this up to MainWindow.xaml.cs, which opens Integrated/ReportErrorWindow prefilled with the field's current verdict data. Never raised for a patient field (VerdictFieldInfo.IsPatientField) or when reporting is disabled (SetBoxes' reportingEnabled parameter) — see PollCursorForHover, which checks both before invoking this.</summary>
    public event EventHandler<ReportErrorRequestInfo>? ReportErrorRequested;

    // Round 4 item 4 ("boxes are colored border + fully transparent
    // middle"): no fill brushes at all anymore (see GreenBrush/RedBrush
    // below and Background below in SetBoxes) — the old ~9% opacity tint
    // brushes are gone entirely, not just set to a lower opacity.
    //
    // ROUND 5 gave the (then still full-border) boxes a thicker, square
    // stroke; ROUND 6 replaced GREEN's full border with a left-edge bar
    // (see VerdictBarGeometry); ROUND 7 (owner, live pharmacy testing:
    // red verdicts should render "exactly like green — solid left-edge
    // bar") removed the full-border path entirely — every verdict is now
    // a bar, so there's no stroke thickness/corner-radius constant left
    // to keep around.

    // Same green/red as MainWindow.xaml's GreenBrush/RedBrush — the boxes
    // layer deliberately never uses yellow (see BoxColorMapper: Yellow
    // collapses to red/"check it" here, per the owner's binary-glance
    // spec).
    private static readonly SolidColorBrush GreenBrush = new(Color.FromRgb(0x2E, 0x7D, 0x32));
    private static readonly SolidColorBrush RedBrush = new(Color.FromRgb(0xC6, 0x28, 0x28));

    private IntPtr _hwnd = IntPtr.Zero;

    public IntegratedBoxesWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;

        // HOVER/RIGHT-CLICK AFFORDANCE: started unconditionally (cheap —
        // GetCursorPos plus a small-list rect check, and an early-out when
        // there are no hotspots at all, see PollCursorForHover) and
        // stopped on Closed so it never outlives the window.
        _hoverPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HoverPollIntervalMs) };
        _hoverPollTimer.Tick += (_, _) =>
        {
            // REVIEWER HARDENING: this codebase deliberately guards every
            // timer call site (see MainWindow.xaml.cs SafeTickIntegratedOverlay's
            // doc — there is no DispatcherUnhandledException hook
            // installed anywhere in this app, so an unguarded tick throwing
            // would crash the whole process, Separate mode included, since
            // it's the same process). The safe fallback for THIS timer
            // specifically is always click-through — an exception here
            // must never leave the window stuck non-transparent.
            try
            {
                PollCursorForHover();
            }
            catch (Exception)
            {
                SetHitTestTransparent(true);
            }
        };
        _hoverPollTimer.Start();
        Closed += (_, _) =>
        {
            _hoverPollTimer.Stop();
            // The popup is a separate top-level window (HoverPopupWindow)
            // — it must be explicitly closed alongside this one, or it
            // would outlive it as an orphaned window (Hide() alone never
            // releases the HWND). Best-effort: app shutdown is already
            // tearing everything down at this point (see
            // IntegratedOverlayCoordinator.Shutdown), so a close failure
            // here has nothing meaningful left to report to.
            try { _popupWindow?.Close(); } catch { /* best-effort only */ }
        };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        _hitTestTransparent = true;
    }

    /// <summary>See NativeWindowPositioning.Reposition — physical pixels, exactly matching PioneerRxWindow.WindowBounds.</summary>
    public void RepositionPhysical(int x, int y, int width, int height) => NativeWindowPositioning.Reposition(_hwnd, x, y, width, height);

    /// <summary>See NativeWindowPositioning.MakeTopmost — call once, right after the first Show().</summary>
    public void EnsureTopmost() => NativeWindowPositioning.MakeTopmost(_hwnd);

    /// <summary>
    /// Rebuilds the box layer from scratch every call — simplest correct
    /// approach given boxes come and go field-to-field/Rx-to-Rx and the
    /// full set is always small (at most the 13 FieldOrder fields).
    /// <paramref name="windowOriginPhysical"/> is this window's own
    /// current physical top-left (PioneerRx's WindowBounds.Location —
    /// same value just passed to RepositionPhysical); <paramref
    /// name="dpiScaleX"/>/<paramref name="dpiScaleY"/> come from
    /// GetDpiForWindow on PioneerRx's own HWND (see
    /// IntegratedOverlayCoordinator), so DpiRectConverter's math is
    /// always relative to whichever monitor PioneerRx (and this window)
    /// is actually on right now.
    ///
    /// READABILITY (owner feedback, round 2 item 2): after converting to
    /// DIPs, every box is padded outward (BoxLayoutAdjuster.ApplyPadding)
    /// so the border doesn't hug the text, then vertically-adjacent boxes
    /// have their facing edges snapped flush
    /// (BoxLayoutAdjuster.SnapFlushAdjacentEdges) so stacked fields (e.g.
    /// Patient Name/DOB/Address) share one boundary line instead of a
    /// sliver of background between them.
    ///
    /// ROUND 5 (owner: "make the left sides of the rectangles match up so
    /// they all line up when looking down ... some of them will be off by
    /// themselves"): a THIRD pass, BoxLayoutAdjuster.AlignColumnLeftEdges,
    /// runs AFTER the flush snap (see that method's own doc for why that
    /// ordering — not before/interleaved — is what composes cleanly) and
    /// snaps each detected visual column's left edges to the column's
    /// minimum X, widening rightward-of-minimum boxes rather than
    /// shrinking any of them (their right edges never move). Boxes with no
    /// left-edge neighbor are left completely alone.
    ///
    /// All three passes are pure DIP-space operations, applied in that
    /// order, AFTER DPI conversion so their thresholds are in DIPs
    /// regardless of monitor scaling — see BoxLayoutAdjuster's own tests
    /// for the geometry itself.
    ///
    /// ROUND 6 (owner: "make the green boxes just be a thicker left side
    /// only bar ... too distracting to have everything encircled. Leave
    /// red boxes the way they are") started this off GREEN-only; ROUND 7
    /// (owner, live pharmacy testing: red verdicts should render "exactly
    /// like green — solid left-edge bar, same 5 DIP width, same vertical
    /// merging within a column") finishes the job — the SAME adjusted
    /// geometry above is computed for every field regardless of color;
    /// this round just drops the color-specific RENDER branch entirely.
    /// Each color's adjusted rects are collected separately and handed to
    /// VerdictBarGeometry.DeriveMergedBarRects independently (left-edge
    /// bar per rect, merging any that are already flush-stacked WITHIN
    /// THAT COLOR into one continuous bar — see that class's doc for why
    /// merging, not just deriving, is what actually guarantees no seam; a
    /// green and a red bar are never merged into each other even if they
    /// happen to touch). Every upstream gate (DAW box rule, the
    /// hide-overlay toggle, click-through, the tab/Rx-identity staleness
    /// gates) already ran before this method was ever called — see
    /// IntegratedOverlayCoordinator.UpdateBoxes — so they apply to bars
    /// of either color exactly as they did to boxes, without this file
    /// needing to know anything about them.
    /// </summary>
    /// <param name="boxes">One entry per field with a resolved on-screen rect this tick — see IntegratedOverlayCoordinator.UpdateBoxes. <c>Field</c> carries the per-field metadata the hover popup and "Report error…" affordance need (Integrated/VerdictFieldInfo.cs); it is intentionally attached PER FIELD, not per merged bar (see the class doc's HOVER/RIGHT-CLICK section) — a single merged bar can visually span several stacked fields, and the popup must describe whichever ONE field the cursor is actually over.</param>
    /// <param name="reportingEnabled">OverlaySettings.RxVerifyReportKey is non-empty — see that property's doc: a right-click is simply never turned into a ReportErrorRequested when this is false, rather than opening a dialog that could only ever queue locally forever.</param>
    public void SetBoxes(IReadOnlyList<(System.Drawing.Rectangle PhysicalRect, bool IsGreen, VerdictFieldInfo Field)> boxes, System.Drawing.Point windowOriginPhysical, double dpiScaleX, double dpiScaleY, bool reportingEnabled)
    {
        BoxCanvas.Children.Clear();
        _hotspots.Clear();
        _hotspotFields.Clear();
        _reportingEnabled = reportingEnabled;
        _lastWindowOriginPhysical = windowOriginPhysical;
        _lastDpiScaleX = dpiScaleX;
        _lastDpiScaleY = dpiScaleY;

        var dipRects = boxes
            .Select(box => DpiRectConverter.ToDipRect(box.PhysicalRect, windowOriginPhysical, dpiScaleX, dpiScaleY))
            .ToList();
        var padded = BoxLayoutAdjuster.ApplyPadding(dipRects);
        var flush = BoxLayoutAdjuster.SnapFlushAdjacentEdges(padded);
        var adjusted = BoxLayoutAdjuster.AlignColumnLeftEdges(flush);

        var greenRects = new List<DipRect>();
        var redRects = new List<DipRect>();
        for (var i = 0; i < boxes.Count; i++)
        {
            (boxes[i].IsGreen ? greenRects : redRects).Add(adjusted[i]);

            // HOVER/RIGHT-CLICK AFFORDANCE: derived from this ONE field's
            // own adjusted rect (VerdictBarGeometry.DeriveBarRect), NOT
            // the merged multi-field bar it may end up visually part of
            // below — see AddHotspot and the class doc's HOVER section.
            AddHotspot(VerdictBarGeometry.DeriveBarRect(adjusted[i]), boxes[i].Field);
        }

        foreach (var bar in VerdictBarGeometry.DeriveMergedBarRects(greenRects))
        {
            AddBar(bar, GreenBrush);
        }

        foreach (var bar in VerdictBarGeometry.DeriveMergedBarRects(redRects))
        {
            AddBar(bar, RedBrush);
        }
    }

    /// <summary>ROUND 7: a solid left-edge bar for EITHER color — this element IS the colored bar itself, so it's a solid Background fill (no BorderBrush/Thickness at all), sized/positioned exactly to <paramref name="dip"/> (already the merged bar geometry from VerdictBarGeometry). IsHitTestVisible=false — purely visual; AddHotspot's own elements are what carry the click-through carve-out and Cursor-changing affordance.</summary>
    private void AddBar(DipRect dip, SolidColorBrush brush)
    {
        var bar = new Border
        {
            Background = brush,
            Width = Math.Max(0, dip.Width),
            Height = Math.Max(0, dip.Height),
            IsHitTestVisible = false
        };

        Canvas.SetLeft(bar, dip.X);
        Canvas.SetTop(bar, dip.Y);
        BoxCanvas.Children.Add(bar);
    }

    /// <summary>
    /// One invisible-but-hit-testable region per field, sized/positioned
    /// exactly over that field's OWN (unmerged) verdict-bar rect. Background
    /// is a real (if transparent) brush rather than left unset — WPF only
    /// hit-tests a Panel/Border's own bounds when its Background is an
    /// actual Brush, even a fully transparent one; an unset (null)
    /// Background lets clicks fall through to whatever's behind it, which
    /// is exactly what BoxCanvas itself relies on (see its XAML) for every
    /// OTHER pixel in this window. Recorded in _hotspots/_hotspotFields
    /// (same index) for PollCursorForHover's cursor-vs-hotspot check.
    ///
    /// REDESIGN (fix/hover-popup-live branch): no longer sets ToolTip or
    /// ContextMenu — see the class doc's HOVER section for why those were
    /// retired (unreliable live on this window's exotic styles) in favor
    /// of HoverStateMachine + HoverPopupWindow, both driven by the SAME
    /// poll that already reliably toggles WS_EX_TRANSPARENT. Cursor=Help
    /// is kept — it's the one piece of the ORIGINAL design confirmed
    /// working live (a simpler WM_SETCURSOR-driven mechanism, not
    /// ToolTipService/ContextMenu), and it costs nothing to keep as a
    /// visual "this is hoverable" affordance.
    /// </summary>
    private void AddHotspot(DipRect dip, VerdictFieldInfo field)
    {
        _hotspots.Add(dip);
        _hotspotFields.Add(field);

        var hotspot = new Border
        {
            Width = Math.Max(0, dip.Width),
            Height = Math.Max(0, dip.Height),
            Background = Brushes.Transparent,
            IsHitTestVisible = true,
            Cursor = System.Windows.Input.Cursors.Help
        };

        Canvas.SetLeft(hotspot, dip.X);
        Canvas.SetTop(hotspot, dip.Y);
        BoxCanvas.Children.Add(hotspot);
    }

    /// <summary>Lazily creates the custom hover popup — see HoverPopupWindow's own doc for why it exists (replaces WPF's ToolTipService entirely).</summary>
    private HoverPopupWindow EnsurePopupWindow() => _popupWindow ??= new HoverPopupWindow();

    /// <summary>
    /// Call whenever MainWindow's ReportErrorWindow opens or closes — see
    /// _reportDialogOpen's own doc. IntegratedOverlayCoordinator forwards
    /// this straight through from MainWindow's own dialog lifetime; there
    /// is no other bookkeeping here, deliberately, so the guard is always
    /// exactly as current as the caller's last call (never a separate
    /// "reset" step to forget).
    /// </summary>
    public void SetDialogOpen(bool open) => _reportDialogOpen = open;

    /// <summary>
    /// HOVER/RIGHT-CLICK AFFORDANCE tick — see the class doc's HOVER
    /// section for the full design (both the click-through toggle and the
    /// fix/hover-popup-live redesign). Checks IsVisible FIRST via
    /// HoverPollDecision — a hidden window (Hide() called from
    /// IntegratedOverlayCoordinator.HideBoxesIfShown, e.g. PioneerRx lost
    /// focus / a different Rx is now showing) must never have its
    /// click-through state OR its popup driven by wherever the cursor
    /// happens to be; HideAndResetHover already forces this the instant
    /// the window is hidden, but this check is what keeps every
    /// SUBSEQUENT poll tick (every ~60ms, for as long as the window stays
    /// hidden) from undoing that. Cheap early-out to "fully click-through,
    /// popup hidden, state machine reset" whenever there's nothing to
    /// hover (not visible, or no hotspots at all) or the cursor position
    /// can't be read.
    ///
    /// TIMING: passes HoverPollIntervalMs itself as the elapsed time for
    /// HoverStateMachine's dwell clock, rather than measuring an actual
    /// wall-clock delta between ticks — DispatcherTimer's real-world tick
    /// cadence is close enough to its configured Interval that the
    /// difference is immaterial against a 250ms dwell threshold, and a
    /// fixed value keeps this method (and the state machine it drives)
    /// simpler to reason about without a Stopwatch field to maintain.
    /// </summary>
    private void PollCursorForHover()
    {
        if (_hwnd == IntPtr.Zero || HoverPollDecision.ShouldForceTransparent(IsVisible, _hotspots.Count))
        {
            SetHitTestTransparent(true);
            _hoverStateMachine.Reset();
            _popupWindow?.HidePopup();
            return;
        }

        if (!GetCursorPos(out var cursor))
        {
            SetHitTestTransparent(true);
            _hoverStateMachine.Reset();
            _popupWindow?.HidePopup();
            return;
        }

        var cursorPhysical = new System.Drawing.Point(cursor.X, cursor.Y);
        var dip = CursorHitTest.ToDipPoint(cursorPhysical, _lastWindowOriginPhysical, _lastDpiScaleX, _lastDpiScaleY);
        var hotspotIndex = CursorHitTest.FindContainingRectIndex(dip.X, dip.Y, _hotspots);
        var isOverHotspot = hotspotIndex >= 0;

        // DIAG: poll-level state, logged ONLY on the enter/leave
        // transition (never every ~60ms tick) — proves the SAME hotspot
        // test that drives the (confirmed-working) hover popup is also
        // seeing the cursor correctly at the moment of a right-click
        // attempt.
        if (_diagLastOverHotspot != isOverHotspot)
        {
            _diagLastOverHotspot = isOverHotspot;
            OcrLogger.LogTiming(isOverHotspot
                ? $"[RIGHTCLICK-DIAG] hotspot ENTER index={hotspotIndex} fieldKey={_hotspotFields[hotspotIndex].FieldKey}"
                : "[RIGHTCLICK-DIAG] hotspot LEAVE");
        }

        SetHitTestTransparent(!isOverHotspot);

        // High-order bit of GetAsyncKeyState's return means "currently
        // down" — see that P/Invoke's own doc for why this, not a WPF
        // mouse event, is what drives right-click detection now.
        var isRightButtonDown = (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;

        // DIAG: RAW button edge, independent of hotspot/detector/dialog
        // gating and capped to the first few presses per session — this
        // is the ONE log line that answers "does GetAsyncKeyState see the
        // physical right-click AT ALL on this machine", regardless of
        // where the cursor happened to be. If this never appears in the
        // log after Will right-clicks, the problem is upstream of
        // everything else in this file (driver/remote-session/hardware).
        if (isRightButtonDown && !_diagRawButtonPrevDown && _diagRawButtonLogCount < DiagRawButtonLogLimit)
        {
            _diagRawButtonLogCount++;
            OcrLogger.LogTiming($"[RIGHTCLICK-DIAG] raw VK_RBUTTON down-transition #{_diagRawButtonLogCount} isOverHotspot={isOverHotspot} hotspotIndex={hotspotIndex}");
        }
        _diagRawButtonPrevDown = isRightButtonDown;

        var sample = new HoverPollSample(isOverHotspot, hotspotIndex, isRightButtonDown, _reportDialogOpen, TimeSpan.FromMilliseconds(HoverPollIntervalMs));
        var result = _hoverStateMachine.Update(sample);

        switch (result.PopupAction)
        {
            case HoverPopupAction.Show:
                EnsurePopupWindow().ShowFor(_hotspotFields[hotspotIndex], cursorPhysical, _lastDpiScaleX, _lastDpiScaleY);
                break;
            case HoverPopupAction.Hide:
                _popupWindow?.HidePopup();
                break;
        }

        if (result.RightClickTriggered)
        {
            var field = _hotspotFields[hotspotIndex];

            // DIAG: the detector fired — everything logged from here on
            // is rare (one right-click, not one per ~60ms tick), so each
            // gate is worth its own line rather than throttling.
            OcrLogger.LogTiming($"[RIGHTCLICK-DIAG] detector FIRED hotspotIndex={hotspotIndex} fieldKey={field.FieldKey} reportingEnabled={_reportingEnabled} dialogOpen={_reportDialogOpen} isPatientField={field.IsPatientField}");

            switch (RightClickOutcomeClassifier.Classify(_reportingEnabled, field.IsPatientField))
            {
                case RightClickOutcome.SuppressedReportingDisabled:
                    // THE LIKELY ROOT CAUSE (RXVERIFY-TROUBLESHOOT round
                    // 2): OverlaySettings.RxVerifyReportKey defaults to ""
                    // and has no in-app setting UI anywhere — see that
                    // property's own doc. If Will's workstation has never
                    // had it set in settings.json directly, EVERY
                    // right-click reaches here and is silently swallowed,
                    // by design, indistinguishable from "right-click is
                    // broken" — this line is what proves (or rules out)
                    // that exact scenario the next time he tries.
                    OcrLogger.LogTiming("[RIGHTCLICK-DIAG] suppressed: reportingEnabled=false (OverlaySettings.RxVerifyReportKey is unset — see its own doc; no in-app UI sets this today)");
                    ShowReportingDisabledNotice();
                    break;
                case RightClickOutcome.SuppressedPatientField:
                    OcrLogger.LogTiming($"[RIGHTCLICK-DIAG] suppressed: isPatientField=true fieldKey={field.FieldKey} (by design — see VerdictFieldInfo.IsPatientField's doc)");
                    break;
                case RightClickOutcome.Raised:
                    OcrLogger.LogTiming($"[RIGHTCLICK-DIAG] raising ReportErrorRequested fieldKey={field.FieldKey}");
                    ReportErrorRequested?.Invoke(this, new ReportErrorRequestInfo(field, cursorPhysical));
                    break;
            }
        }
    }

    /// <summary>
    /// feat/report-key-delivery: a right-click on a workstation with no
    /// RxVerifyReportKey configured used to be entirely silent from the
    /// pharmacist's point of view — the only trace was the
    /// [RIGHTCLICK-DIAG] log line just above, and nobody but Will/dev
    /// ever reads that log. That's indistinguishable from "right-click is
    /// just broken", which is the exact confusion this branch exists to
    /// close.
    ///
    /// REVIEW FIX: this fires from an even weaker foreground context than
    /// ReportErrorWindow ever did — a GetAsyncKeyState poll on a
    /// DispatcherTimer tick, with the physical right-click having landed
    /// on Pioneer's own window, not one of ours. MainWindow.xaml.cs
    /// OpenReportErrorDialog needed TWO rounds (Topmost=True +
    /// ContentRendered's Activate()/Topmost-pulse + ShowInTaskbar=True) to
    /// stop a plain Show()/ShowDialog() from opening invisibly BEHIND
    /// Pioneer, because every window this process owns is
    /// WS_EX_NOACTIVATE and so the process itself never holds real
    /// Windows foreground — Windows' anti-focus-stealing heuristics deny
    /// an implicit activation from a background process. A bare
    /// MessageBox.Show call here would be exactly as vulnerable, and a
    /// notice that pops invisibly is worse than no notice at all — the
    /// pharmacist would conclude right-click is broken again, the exact
    /// failure this branch exists to close. MessageBoxOptions.
    /// DefaultDesktopOnly sidesteps the whole problem rather than
    /// re-deriving ReportErrorWindow's two-round fix for a one-line
    /// message: it's a genuine system-modal dialog on the default
    /// desktop, guaranteed topmost/foreground regardless of which
    /// process/window requested it or what activation state that process
    /// is in — no Owner, no Activate(), no Topmost-pulse needed. Showing
    /// on the default desktop (not this app's own, since this app has
    /// none of its own) and stealing no window state back is exactly
    /// right for a rare, one-shot, read-and-dismiss notice like this one.
    /// </summary>
    private void ShowReportingDisabledNotice()
    {
        if (_reportingDisabledNoticeShowing) return;

        _reportingDisabledNoticeShowing = true;
        try
        {
            MessageBox.Show(
                "Error reporting isn't set up on this PC — run the pinned setup line from Manager HQ.",
                "Rx Verify — reporting not configured",
                MessageBoxButton.OK,
                MessageBoxImage.Information,
                MessageBoxResult.OK,
                MessageBoxOptions.DefaultDesktopOnly);
        }
        finally
        {
            _reportingDisabledNoticeShowing = false;
        }
    }

    /// <summary>
    /// Call this INSTEAD OF a bare Hide() everywhere this window is hidden
    /// (IntegratedOverlayCoordinator.HideBoxesIfShown) — clears
    /// _hotspots/_hotspotFields, resets the hover state machine, hides the
    /// popup, and forces WS_EX_TRANSPARENT back on, all BEFORE Hide()
    /// itself runs, so a hidden window can never carry stale hotspots, a
    /// lingering popup, or a cleared-transparency style into whatever it
    /// gets repositioned/shown over next (a different Pioneer window, a
    /// different Rx's field layout). See HoverPollDecision's doc for the
    /// full click-through failure mode this closes, and
    /// PollCursorForHover's own IsVisible check for the belt-and-suspenders
    /// layer that also covers every poll tick while it stays hidden.
    /// </summary>
    public void HideAndResetHover()
    {
        _hotspots.Clear();
        _hotspotFields.Clear();
        _hoverStateMachine.Reset();
        _popupWindow?.HidePopup();
        SetHitTestTransparent(true);
        // DIAG: forget the last-logged hotspot state too, so the poll's
        // enter/leave transition log doesn't stay silent after a later
        // Show() just because it happens to land on the same true/false
        // value this hide cycle last saw (see _diagLastOverHotspot's doc).
        _diagLastOverHotspot = null;
        Hide();
    }

    /// <summary>
    /// REVIEWER BLOCKER FIX (layer 3, belt-and-suspenders): call right
    /// before Show() (IntegratedOverlayCoordinator.UpdateBoxes) so this
    /// window can never become visible already non-transparent, regardless
    /// of whatever state history led here — HideAndResetHover and
    /// PollCursorForHover's IsVisible check already prevent that in
    /// practice, but a window that's ABOUT to become visible for the
    /// first time this "shown" streak should never depend on either of
    /// those alone having already run in the right order.
    /// </summary>
    public void ForceHitTestTransparent() => SetHitTestTransparent(true);

    /// <summary>Toggles ONLY the WS_EX_TRANSPARENT bit — WS_EX_LAYERED/WS_EX_NOACTIVATE/WS_EX_TOOLWINDOW are never touched (see the class doc's HOVER section). No-op (no SetWindowLong call at all) when the requested state already matches _hitTestTransparent, so a steady hover/steady click-through both cost nothing beyond the poll's own cursor read.</summary>
    private void SetHitTestTransparent(bool transparent)
    {
        if (_hwnd == IntPtr.Zero || _hitTestTransparent == transparent) return;

        var exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        var newStyle = transparent ? exStyle | WS_EX_TRANSPARENT : exStyle & ~WS_EX_TRANSPARENT;
        SetWindowLong(_hwnd, GWL_EXSTYLE, newStyle);
        _hitTestTransparent = transparent;
    }
}
