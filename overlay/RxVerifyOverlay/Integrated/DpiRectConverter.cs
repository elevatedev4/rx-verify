using System.Drawing;

namespace RxVerifyOverlay.Integrated;

/// <summary>
/// A DIP (device-independent pixel) rectangle relative to some window's
/// own client-area origin — plain data, no WPF dependency, so
/// DpiRectConverter (and its tests) don't need a System.Windows
/// reference. IntegratedBoxesWindow (the only production caller) copies
/// these four numbers straight onto a Border's Canvas.Left/Canvas.Top/
/// Width/Height.
/// </summary>
public readonly record struct DipRect(double X, double Y, double Width, double Height);

/// <summary>
/// Pure DPI math for the integrated boxes layer — converts a field's
/// physical-pixel screen rect (from UIA's BoundingRectangle, always
/// physical regardless of any process's own DPI awareness) into a DIP
/// rect relative to the boxes window's own top-left, using the DPI scale
/// of whichever monitor that window (and PioneerRx) is currently on. See
/// IntegratedBoxesWindow, the only production caller.
///
/// CLASSIC BUG this guards against: UIA's BoundingRectangle is ALWAYS in
/// physical screen pixels. WPF layout (Canvas.Left/Top, Width/Height) is
/// ALWAYS in device-independent units (1/96"). Treating one as the other
/// silently mis-draws boxes on any monitor running above 100% scaling
/// (125%/150% are common on real workstations) even though it looks
/// correct at 100%. No WPF/UIA dependency here at all (just
/// System.Drawing.Rectangle/Point, both plain structs) so this is
/// covered by fast xUnit tests — see
/// RxVerifyOverlay.Tests/DpiRectConverterTests.cs.
/// </summary>
public static class DpiRectConverter
{
    /// <summary>
    /// <paramref name="fieldPhysicalRect"/> and
    /// <paramref name="windowOriginPhysical"/> are both physical pixels in
    /// the SAME (virtual desktop) coordinate space — windowOriginPhysical
    /// is the boxes window's own top-left (i.e. PioneerRx's
    /// WindowBounds.Location), so the result is relative to that window's
    /// client-area origin, ready to assign directly to a child element's
    /// Canvas.Left/Top/Width/Height inside it.
    /// <paramref name="dpiScaleX"/>/<paramref name="dpiScaleY"/> are the
    /// boxes window's OWN current DPI scale (e.g. from
    /// VisualTreeHelper.GetDpi(this) or GetDpiForWindow(hwnd)/96.0) —
    /// using the window's own scale (never a fixed 1.0, never assuming
    /// the primary monitor's) is what makes this correct on a secondary
    /// monitor running a different scale factor than the primary one.
    /// </summary>
    public static DipRect ToDipRect(Rectangle fieldPhysicalRect, Point windowOriginPhysical, double dpiScaleX, double dpiScaleY)
    {
        var x = (fieldPhysicalRect.X - windowOriginPhysical.X) / dpiScaleX;
        var y = (fieldPhysicalRect.Y - windowOriginPhysical.Y) / dpiScaleY;
        var width = fieldPhysicalRect.Width / dpiScaleX;
        var height = fieldPhysicalRect.Height / dpiScaleY;
        return new DipRect(x, y, width, height);
    }
}
