using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace RxVerifyOverlay.OrderAssist.Windows;

/// <summary>
/// Finds whichever of the two Order Assist target Pioneer windows is
/// CURRENTLY the OS foreground window, by title only. Deliberately its
/// OWN small, self-contained set of P/Invoke declarations rather than
/// reusing Integrated/IntegratedOverlayCoordinator's (private, and
/// answering a different question — "which window is PioneerRx's MAIN
/// window" — that neither of these two windows is) or Uia/
/// PioneerRxWindow's (a UIA-based attach to the Pre-Check/Edit/New-Rx
/// screen specifically, via a shared automation session this module has
/// no business depending on). This keeps OrderAssist's window-finding
/// completely independent of the verify flow's own — see
/// OrderAssistCoordinator's class doc for why that independence matters
/// (turn-off/split-off-as-its-own-module later).
///
/// Kept intentionally simple: a plain title check on the single current
/// foreground window, no top-level-window enumeration, no UIA, no
/// caching — this only runs once per ~1s tick (see OrderAssistCoordinator),
/// a completely different, far cheaper cost profile than the verify
/// flow's own ~250ms tick.
/// </summary>
public static class OrderAssistWindowLocator
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    public readonly record struct TargetWindow(OrderAssistWindowKind Kind, IntPtr Handle, Rectangle Bounds);

    /// <summary>Null when the foreground window isn't one of the two Order Assist windows, its title can't be read, or its bounds can't be read/are degenerate — same "never trust a failed read, degrade to nothing" posture as the rest of this app's Win32 wrappers (e.g. Integrated/IntegratedOverlayCoordinator.cs EnumeratePioneerTopLevelWindows).</summary>
    public static TargetWindow? FindForegroundTarget()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;

        var kind = OrderAssistWindowClassifier.Classify(ReadWindowTitle(hwnd));
        if (kind == OrderAssistWindowKind.None) return null;

        if (!GetWindowRect(hwnd, out var rect)) return null;

        var bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        if (bounds.Width <= 0 || bounds.Height <= 0) return null;

        return new TargetWindow(kind, hwnd, bounds);
    }

    private static string? ReadWindowTitle(IntPtr hwnd)
    {
        try
        {
            var length = GetWindowTextLength(hwnd);
            if (length <= 0) return null;

            var builder = new StringBuilder(length + 1);
            GetWindowText(hwnd, builder, builder.Capacity);
            return builder.ToString();
        }
        catch
        {
            return null;
        }
    }
}
