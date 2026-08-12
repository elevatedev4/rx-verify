using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

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
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
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
    public void SetBoxes(IReadOnlyList<(System.Drawing.Rectangle PhysicalRect, bool IsGreen)> boxes, System.Drawing.Point windowOriginPhysical, double dpiScaleX, double dpiScaleY)
    {
        BoxCanvas.Children.Clear();

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

    /// <summary>ROUND 7: a solid left-edge bar for EITHER color — this element IS the colored bar itself, so it's a solid Background fill (no BorderBrush/Thickness at all), sized/positioned exactly to <paramref name="dip"/> (already the merged bar geometry from VerdictBarGeometry).</summary>
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
}
