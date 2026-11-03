using System;
using System.Runtime.InteropServices;
using System.Text;

namespace RxVerifyOverlay.OrderAssist.Windows;

/// <summary>
/// Always-on, cheap "is any Pioneer order screen on the desktop" probe for
/// automatic Order/Verify switching. Same shape and precedent as
/// Integrated/LoginWindowMonitor: EnumWindows, cheapest filters first
/// (visible, then title prefix, only then the cached PioneerRx-process
/// check, minimized and cloaked), a 400 ms cache, UI-thread only (no
/// locking), every P/Invoke guarded. Unlike OrderAssistWindowLocator.Scan
/// this runs regardless of Order mode (Scan runs only while Order mode is
/// enabled), which is the whole point: it is what turns Order mode ON.
///
/// PHI: titles can carry patient data. They are compared in
/// OrderAssistWindowClassifier.IsOrderScreenTitle and dropped; nothing
/// here stores or logs a title (the host logs booleans only).
/// </summary>
public static class OrderScreenMonitor
{
    private const long CacheMs = 400;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hWnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    private const int DWMWA_CLOAKED = 14;

    private static long _lastCheckTick = long.MinValue;

    /// <summary>
    /// Takes a fresh sample if the 400 ms cache has expired. Returns true
    /// (and sets <paramref name="screenPresent"/>) only for a FRESH sample;
    /// false means "cached, nothing new" so the caller's edge/debounce
    /// logic is never fed the same observation twice. Never throws.
    /// <paramref name="screenPresent"/> is null when detection failed.
    /// </summary>
    public static bool TrySample(out bool? screenPresent)
    {
        screenPresent = null;
        try
        {
            var now = Environment.TickCount64;
            if (_lastCheckTick != long.MinValue && now - _lastCheckTick >= 0 && now - _lastCheckTick < CacheMs)
            {
                return false;
            }
            _lastCheckTick = now;

            try
            {
                screenPresent = Detect();
            }
            catch (Exception)
            {
                screenPresent = null;
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool? Detect()
    {
        var found = false;
        var buffer = new StringBuilder(256);

        var ok = EnumWindows((hWnd, _) =>
        {
            try
            {
                if (!IsWindowVisible(hWnd)) return true;

                buffer.Clear();
                var length = GetWindowTextW(hWnd, buffer, buffer.Capacity);
                if (length <= 0) return true;

                // Title first (cheap, no process lookup); the rest only for
                // the rare window that already matched.
                if (!OrderAssistWindowClassifier.IsOrderScreenTitle(buffer.ToString(0, Math.Min(length, buffer.Capacity)))) return true;
                if (IsIconic(hWnd) || IsCloaked(hWnd)) return true;
                if (!OrderAssistWindowLocator.IsOwnedByPioneerRxProcess(hWnd)) return true;

                found = true;
                return false; // stop enumerating
            }
            catch
            {
                // skip this window, keep enumerating
            }
            return true;
        }, IntPtr.Zero);

        // EnumWindows returns false both on failure and when the callback
        // stopped it early; a positive match is authoritative either way.
        if (found) return true;
        return ok ? false : (bool?)null;
    }

    private static bool IsCloaked(IntPtr hWnd)
    {
        try
        {
            return DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0;
        }
        catch
        {
            return false;
        }
    }
}
