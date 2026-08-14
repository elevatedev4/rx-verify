using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace RxVerifyOverlay.Integrated;

/// <summary>
/// Shared Win32 "which monitor is this physical point on, and what are
/// its bounds" lookup — extracted (RXVERIFY-TROUBLESHOOT, 2026-08) so
/// HoverPopupWindow (needs the FULL monitor rect, so the hover popup can
/// render right up to a monitor's physical edge) and MainWindow's
/// ReportErrorWindow positioning (needs the WORK AREA rect instead, so
/// the report dialog never overlaps the taskbar) don't each carry their
/// own duplicate MonitorFromPoint/GetMonitorInfo P/Invoke declarations.
/// Both then hand whichever rect they asked for to PopupBoundsClamp —
/// the actual clamp math is identical either way, only the input rect
/// differs.
/// </summary>
internal static class MonitorGeometry
{
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint pt, uint dwFlags);

    // CharSet.Unicode explicit — user32.dll exports GetMonitorInfoA/W,
    // not a literal "GetMonitorInfo"; leaving CharSet unset risks an
    // EntryPointNotFoundException at runtime (same reasoning as
    // OrderAssistWindowLocator's GetWindowText P/Invoke elsewhere in this
    // codebase).
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    /// <summary>Full physical bounds of whichever monitor <paramref name="pointPhysical"/> is on/nearest to (MONITOR_DEFAULTTONEAREST — always resolves to a real monitor even if the point is technically outside every monitor's rect for an instant during a fast mouse move), or null on the failure of either Win32 call.</summary>
    public static Rectangle? GetMonitorBounds(Point pointPhysical) => Query(pointPhysical) is { } info ? ToRect(info.rcMonitor) : null;

    /// <summary>WORK AREA (excludes the taskbar) of whichever monitor <paramref name="pointPhysical"/> is on/nearest to, or null on the failure of either Win32 call.</summary>
    public static Rectangle? GetWorkArea(Point pointPhysical) => Query(pointPhysical) is { } info ? ToRect(info.rcWork) : null;

    private static MonitorInfo? Query(Point pointPhysical)
    {
        var monitor = MonitorFromPoint(new NativePoint { X = pointPhysical.X, Y = pointPhysical.Y }, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return null;

        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        return GetMonitorInfo(monitor, ref info) ? info : null;
    }

    private static Rectangle ToRect(NativeRect r) => Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
}
