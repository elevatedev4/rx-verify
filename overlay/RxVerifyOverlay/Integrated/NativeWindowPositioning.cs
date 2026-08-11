using System;
using System.Runtime.InteropServices;

namespace RxVerifyOverlay.Integrated;

/// <summary>
/// Shared native window-positioning helpers for BOTH integrated windows
/// (IntegratedBoxesWindow, ControlBoxWindow). Positioning via
/// SetWindowPos in PHYSICAL pixels — never WPF's Left/Top/Width/Height,
/// which are DIPs — sidesteps the "what DPI context is this window's
/// Left/Top currently interpreted in" question entirely for OUTER window
/// placement: PioneerRxWindow.WindowBounds is already physical pixels
/// (straight from UIA's BoundingRectangle), so lining up with it exactly
/// needs no conversion at all. DPI conversion is still needed for laying
/// out CONTENT inside these windows (see DpiRectConverter) — that's a
/// separate concern from where the window itself sits on screen.
/// </summary>
internal static class NativeWindowPositioning
{
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;

    private static readonly IntPtr HwndTopmost = new(-1);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    /// <summary>Moves/resizes without disturbing z-order or stealing activation — safe to call every refresh tick. A no-op if the window's HWND doesn't exist yet (e.g. called before the first Show()).</summary>
    public static void Reposition(IntPtr hwnd, int x, int y, int width, int height)
    {
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, SWP_NOACTIVATE | SWP_NOZORDER);
    }

    /// <summary>Establishes HWND_TOPMOST once, right after a window is first shown — later Reposition calls use SWP_NOZORDER so they don't need to re-fight z-order every tick.</summary>
    public static void MakeTopmost(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
    }
}
