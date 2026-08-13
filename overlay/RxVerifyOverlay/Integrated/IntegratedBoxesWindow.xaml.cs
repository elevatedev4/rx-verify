using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

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
    // HOVER/RIGHT-CLICK AFFORDANCE (verdict-tooltips-reports branch):
    // WS_EX_TRANSPARENT above is a WHOLE-WINDOW Win32 style — while it's
    // set, Windows routes every mouse message straight to whatever is
    // underneath, and WPF never sees so much as a MouseMove for this
    // window, so its own ToolTip/ContextMenu machinery (what the hover
    // tooltip and "Report error…" menu are built on, see AddHotspot) can
    // never fire no matter what's in the visual tree. There is no WPF- or
    // even per-element Win32 way to make ONE child region interactive
    // while the rest of the same top-level window stays click-through —
    // the extended style applies to the whole HWND.
    //
    // The fix: poll the cursor's OS-level screen position on a short
    // timer (_hoverPollTimer) and dynamically CLEAR WS_EX_TRANSPARENT only
    // while the cursor sits over one of the current per-field verdict-bar
    // hotspots (_hotspots, rebuilt every SetBoxes call), restoring it the
    // instant the cursor is elsewhere. Once cleared, real mouse messages
    // reach this window and WPF's own hit-testing takes over precisely —
    // the hotspot Border elements (AddHotspot) are the only hit-testable
    // content in the canvas (see BoxCanvas's XAML — its own Background is
    // deliberately unset so empty space never swallows a click), so a
    // click that lands outside every hotspot but still within this poll's
    // coarse "clear transparency" window has nothing to hit and is
    // effectively a no-op rather than a stray interaction with Pioneer.
    //
    // KNOWN, ACCEPTED TRADEOFF (flagged, not silently introduced): this
    // narrows the "spec hard requirement" click-through guarantee at the
    // class doc's top from "always, everywhere" to "everywhere except the
    // ~5-DIP-wide verdict bars themselves, and only while the cursor is
    // already sitting on one" — there was no way to add a hover/right-click
    // affordance ON the bars without SOME carve-out. The poll interval
    // (HoverPollIntervalMs) bounds how stale that carve-out's edges can be;
    // a click landing exactly as the cursor crosses a hotspot's boundary
    // could in principle be swallowed instead of reaching Pioneer for up
    // to one poll interval — see this branch's own report for the
    // residual-risk writeup. WS_EX_NOACTIVATE/WS_EX_TOOLWINDOW are never
    // toggled (only the TRANSPARENT bit) — hovering/clicking a bar must
    // still never steal keyboard focus/activation from Pioneer.
    // ------------------------------------------------------------------
    private const int HoverPollIntervalMs = 60;

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    /// <summary>Managed-side mirror of whether WS_EX_TRANSPARENT is currently set, so PollCursorForHover can skip the SetWindowLong round-trip on every tick when nothing changed (only ever toggled by SetHitTestTransparent). Starts true — matches the baseline OnSourceInitialized establishes.</summary>
    private bool _hitTestTransparent = true;

    /// <summary>The current tick's per-field hotspot rects, in the same window-relative DIP space as VerdictBarGeometry's bars — rebuilt every SetBoxes call, read by PollCursorForHover via CursorHitTest.IsWithinAnyRect. Empty (never null) before the first SetBoxes call.</summary>
    private readonly List<DipRect> _hotspots = new();

    private readonly DispatcherTimer _hoverPollTimer;

    private System.Drawing.Point _lastWindowOriginPhysical;
    private double _lastDpiScaleX = 1.0;
    private double _lastDpiScaleY = 1.0;

    /// <summary>Raised when the pharmacist picks "Report error…" from a verdict bar's hotspot context menu — IntegratedOverlayCoordinator forwards this up to MainWindow.xaml.cs, which opens Integrated/ReportErrorWindow prefilled with the field's current verdict data. Never raised for a patient field (VerdictFieldInfo.IsPatientField) or when reporting is disabled (see SetBoxes' reportingEnabled parameter) — the menu item simply isn't added in either case, see BuildHotspotContextMenu.</summary>
    public event EventHandler<VerdictFieldInfo>? ReportErrorRequested;

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
        Closed += (_, _) => _hoverPollTimer.Stop();
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
    /// <param name="boxes">One entry per field with a resolved on-screen rect this tick — see IntegratedOverlayCoordinator.UpdateBoxes. <c>Field</c> carries the per-field metadata the hover tooltip and "Report error…" menu need (Integrated/VerdictFieldInfo.cs); it is intentionally attached PER FIELD, not per merged bar (see the class doc's HOVER/RIGHT-CLICK section and BuildHotspots below) — a single merged bar can visually span several stacked fields, and the tooltip must describe whichever ONE field the cursor is actually over.</param>
    /// <param name="reportingEnabled">OverlaySettings.RxVerifyReportKey is non-empty — see that property's doc: "Report error…" is omitted from every hotspot's context menu entirely when this is false, rather than shown as a button that can only ever queue locally forever.</param>
    public void SetBoxes(IReadOnlyList<(System.Drawing.Rectangle PhysicalRect, bool IsGreen, VerdictFieldInfo Field)> boxes, System.Drawing.Point windowOriginPhysical, double dpiScaleX, double dpiScaleY, bool reportingEnabled)
    {
        BoxCanvas.Children.Clear();
        _hotspots.Clear();
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
            AddHotspot(VerdictBarGeometry.DeriveBarRect(adjusted[i]), boxes[i].Field, reportingEnabled);
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

    /// <summary>ROUND 7: a solid left-edge bar for EITHER color — this element IS the colored bar itself, so it's a solid Background fill (no BorderBrush/Thickness at all), sized/positioned exactly to <paramref name="dip"/> (already the merged bar geometry from VerdictBarGeometry). IsHitTestVisible=false — purely visual; AddHotspot's own elements are what carry the tooltip/context menu.</summary>
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
    /// exactly over that field's OWN (unmerged) verdict-bar rect, carrying
    /// the hover ToolTip and (when applicable) the "Report error…"
    /// ContextMenu. Background is a real (if transparent) brush rather
    /// than left unset — WPF only hit-tests a Panel/Border's own bounds
    /// when its Background is an actual Brush, even a fully transparent
    /// one; an unset (null) Background lets clicks fall through to
    /// whatever's behind it, which is exactly what BoxCanvas itself relies
    /// on (see its XAML) for every OTHER pixel in this window. Recorded in
    /// _hotspots (DIP rect only, no element reference needed) for
    /// PollCursorForHover's coarse cursor-vs-hotspot check.
    /// </summary>
    private void AddHotspot(DipRect dip, VerdictFieldInfo field, bool reportingEnabled)
    {
        _hotspots.Add(dip);

        var hotspot = new Border
        {
            Width = Math.Max(0, dip.Width),
            Height = Math.Max(0, dip.Height),
            Background = Brushes.Transparent,
            IsHitTestVisible = true,
            Cursor = System.Windows.Input.Cursors.Help,
            ToolTip = BuildTooltipContent(field)
        };

        var contextMenu = BuildHotspotContextMenu(field, reportingEnabled);
        if (contextMenu is not null)
        {
            hotspot.ContextMenu = contextMenu;
        }

        Canvas.SetLeft(hotspot, dip.X);
        Canvas.SetTop(hotspot, dip.Y);
        BoxCanvas.Children.Add(hotspot);
    }

    /// <summary>Field name / source / entered / status / explanation, verbatim — see VerdictFieldInfo's PHI CAUTION doc for why no redaction happens here (on-screen only, never leaves the workstation).</summary>
    private static object BuildTooltipContent(VerdictFieldInfo field)
    {
        var panel = new StackPanel { MaxWidth = 320 };

        panel.Children.Add(new TextBlock { Text = field.DisplayName, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 2) });
        panel.Children.Add(new TextBlock { Text = $"Status: {field.Status}" });
        panel.Children.Add(new TextBlock { Text = $"Source: {field.SourceValue}", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = $"Entered: {field.EnteredValue}", TextWrapping = TextWrapping.Wrap });

        if (!string.IsNullOrEmpty(field.Explanation))
        {
            panel.Children.Add(new TextBlock
            {
                Text = field.Explanation,
                TextWrapping = TextWrapping.Wrap,
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(0, 4, 0, 0)
            });
        }

        return panel;
    }

    /// <summary>
    /// Null (no context menu at all) for a patient field (VerdictFieldInfo.
    /// IsPatientField — see its doc) or when reporting is disabled
    /// (reportingEnabled false, i.e. no RxVerifyReportKey configured yet —
    /// see OverlaySettings.RxVerifyReportKey's doc) — in either case there
    /// is nothing this menu could safely or usefully do, so it's omitted
    /// entirely rather than shown disabled.
    /// </summary>
    private ContextMenu? BuildHotspotContextMenu(VerdictFieldInfo field, bool reportingEnabled)
    {
        if (!reportingEnabled || field.IsPatientField) return null;

        var reportMenuItem = new MenuItem { Header = "Report error…" };
        reportMenuItem.Click += (_, _) => ReportErrorRequested?.Invoke(this, field);

        var menu = new ContextMenu();
        menu.Items.Add(reportMenuItem);
        return menu;
    }

    /// <summary>
    /// HOVER/RIGHT-CLICK AFFORDANCE tick — see the class doc's HOVER
    /// section for the full design. REVIEWER BLOCKER FIX: checks
    /// IsVisible FIRST via HoverPollDecision — a hidden window (Hide()
    /// called from IntegratedOverlayCoordinator.HideBoxesIfShown, e.g.
    /// PioneerRx lost focus / a different Rx is now showing) must never
    /// have its click-through state driven by wherever the cursor happens
    /// to be; HideAndResetHover already forces this the instant the
    /// window is hidden, but this check is what keeps every SUBSEQUENT
    /// poll tick (every ~60ms, for as long as the window stays hidden)
    /// from undoing that. Cheap early-out to "fully click-through"
    /// whenever there's nothing to hover (not visible, or no hotspots at
    /// all) or the cursor position can't be read.
    /// </summary>
    private void PollCursorForHover()
    {
        if (_hwnd == IntPtr.Zero || HoverPollDecision.ShouldForceTransparent(IsVisible, _hotspots.Count))
        {
            SetHitTestTransparent(true);
            return;
        }

        if (!GetCursorPos(out var cursor))
        {
            SetHitTestTransparent(true);
            return;
        }

        var cursorPhysical = new System.Drawing.Point(cursor.X, cursor.Y);
        var dip = CursorHitTest.ToDipPoint(cursorPhysical, _lastWindowOriginPhysical, _lastDpiScaleX, _lastDpiScaleY);
        var isOverHotspot = CursorHitTest.IsWithinAnyRect(dip.X, dip.Y, _hotspots);

        SetHitTestTransparent(!isOverHotspot);
    }

    /// <summary>
    /// REVIEWER BLOCKER FIX: call this INSTEAD OF a bare Hide() everywhere
    /// this window is hidden (IntegratedOverlayCoordinator.HideBoxesIfShown)
    /// — clears _hotspots and forces WS_EX_TRANSPARENT back on BEFORE
    /// Hide() itself runs, so a hidden window can never carry stale
    /// hotspots or a cleared-transparency style into whatever it gets
    /// repositioned/shown over next (a different Pioneer window, a
    /// different Rx's field layout). See HoverPollDecision's doc for the
    /// full failure mode this closes, and PollCursorForHover's own
    /// IsVisible check for the belt-and-suspenders layer that also covers
    /// every poll tick while it stays hidden.
    /// </summary>
    public void HideAndResetHover()
    {
        _hotspots.Clear();
        SetHitTestTransparent(true);
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
