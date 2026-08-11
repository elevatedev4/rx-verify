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
    // ROUND 5 (owner: "the borders need to be thicker, and I'd prefer for
    // them to be square rather than rounded"):
    //   - BoxStrokeThickness: 2 -> 3. Padding (BoxLayoutAdjuster.PaddingDip)
    //     is 2 DIP, so a 3px stroke's inner edge sits ~1px inside the raw
    //     field rect's own edge rather than exactly on it — a small,
    //     acceptable encroachment (still far tighter than round 2-3's 4px
    //     padding/2.5px stroke), not disproportionate enough to warrant
    //     going to 4. Revisit if it reads as too chunky live.
    //   - Corner rounding removed entirely (no BoxCornerRadius constant at
    //     all anymore, no CornerRadius set on the Border below — WPF's own
    //     default is already square/0, so omitting it is both "square"
    //     and self-documenting that this was a deliberate removal, not an
    //     oversight).
    private const double BoxStrokeThickness = 3;

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

        for (var i = 0; i < boxes.Count; i++)
        {
            var dip = adjusted[i];
            var isGreen = boxes[i].IsGreen;

            var border = new Border
            {
                BorderBrush = isGreen ? GreenBrush : RedBrush,
                BorderThickness = new Thickness(BoxStrokeThickness),
                // ROUND 5: no CornerRadius set at all — square corners,
                // WPF's own default. See BoxStrokeThickness's doc above.
                // Round 4 item 4: fully transparent middle, no fill at
                // all — explicit Brushes.Transparent (not left null) so
                // it's unambiguous this is intentional, not an oversight.
                // Doesn't affect click-through: IsHitTestVisible=false
                // below is what actually matters at the WPF level, and
                // the window itself is WS_EX_TRANSPARENT regardless (see
                // OnSourceInitialized) — neither depends on Background.
                Background = Brushes.Transparent,
                Width = Math.Max(0, dip.Width),
                Height = Math.Max(0, dip.Height),
                IsHitTestVisible = false
            };

            Canvas.SetLeft(border, dip.X);
            Canvas.SetTop(border, dip.Y);
            BoxCanvas.Children.Add(border);
        }
    }
}
