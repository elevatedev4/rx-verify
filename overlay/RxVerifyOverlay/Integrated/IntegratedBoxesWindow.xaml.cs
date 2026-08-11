using System;
using System.Collections.Generic;
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

    private const double BoxStrokeThickness = 2.5;
    private const double BoxCornerRadius = 4;

    // Same green/red as MainWindow.xaml's GreenBrush/RedBrush — the boxes
    // layer deliberately never uses yellow (see BoxColorMapper: Yellow
    // collapses to red/"check it" here, per the owner's binary-glance
    // spec). Fill is a faint (~9% opacity) tint, not solid, so the field
    // text underneath stays readable — see item 1's "no fill (or <=10%
    // opacity fill)".
    private static readonly SolidColorBrush GreenBrush = new(Color.FromRgb(0x2E, 0x7D, 0x32));
    private static readonly SolidColorBrush RedBrush = new(Color.FromRgb(0xC6, 0x28, 0x28));
    private static readonly SolidColorBrush GreenFillBrush = new(Color.FromArgb(0x18, 0x2E, 0x7D, 0x32));
    private static readonly SolidColorBrush RedFillBrush = new(Color.FromArgb(0x18, 0xC6, 0x28, 0x28));

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
    /// </summary>
    public void SetBoxes(IReadOnlyList<(System.Drawing.Rectangle PhysicalRect, bool IsGreen)> boxes, System.Drawing.Point windowOriginPhysical, double dpiScaleX, double dpiScaleY)
    {
        BoxCanvas.Children.Clear();

        foreach (var box in boxes)
        {
            var dip = DpiRectConverter.ToDipRect(box.PhysicalRect, windowOriginPhysical, dpiScaleX, dpiScaleY);

            var border = new Border
            {
                BorderBrush = box.IsGreen ? GreenBrush : RedBrush,
                BorderThickness = new Thickness(BoxStrokeThickness),
                CornerRadius = new CornerRadius(BoxCornerRadius),
                Background = box.IsGreen ? GreenFillBrush : RedFillBrush,
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
