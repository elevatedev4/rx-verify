using System;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;
using RxVerifyOverlay.Ocr;

namespace RxVerifyOverlay.Integrated;

/// <summary>
/// Win32 half of the "hide the overlay on the PioneerRx login screen"
/// feature: enumerates visible top-level windows and asks
/// LoginWindowGate whether any title contains "Logon to PioneerRx".
/// Polled from the existing ~250ms integrated tick
/// (IntegratedOverlayCoordinator.TickCore) and the Order Assist tick
/// (OrderAssistCoordinator.TickAsync) — no new timer or hook. Both run on
/// the UI thread, so the cached result needs no locking; the cache just
/// keeps two ticks within the same window from enumerating twice.
///
/// CRASH-SAFETY: every P/Invoke is inside try/catch; any failure returns
/// false (overlay shown) and is never remembered as "hidden". Nothing
/// blocks: EnumWindows + GetWindowText on top-level windows of other
/// processes read the cached caption and do not message the target.
///
/// PHI: window titles are only compared, never stored or logged. The only
/// log line is the edge "login window visible: true/false".
/// </summary>
public static class LoginWindowMonitor
{
    private const long CacheMs = 400;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hWnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    private const int DWMWA_CLOAKED = 14;

    private static readonly LoginGateState State = new();
    private static long _lastCheckTick = long.MinValue;
    private static bool _lastResult;

    /// <summary>
    /// True when the overlay should be hidden because a visible window
    /// titled "...Logon to PioneerRx..." exists. Never throws; any failure
    /// returns false (overlay shown).
    /// </summary>
    public static bool IsLoginWindowVisible()
    {
        try
        {
            var now = Environment.TickCount64;
            if (_lastCheckTick != long.MinValue && now - _lastCheckTick >= 0 && now - _lastCheckTick < CacheMs)
            {
                return _lastResult;
            }

            bool? detected;
            try
            {
                detected = Detect();
            }
            catch (Exception)
            {
                detected = null; // fail-safe: unknown -> overlay shown
            }

            var transition = State.Observe(detected);
            _lastCheckTick = now;
            _lastResult = State.IsHidden;

            if (transition != LoginGateTransition.None)
            {
                try { OcrLogger.LogTiming($"[LOGIN-GATE] login window visible: {(transition == LoginGateTransition.Hide ? "true" : "false")}"); }
                catch { /* logging must never affect visibility */ }
            }

            return _lastResult;
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

                // Cheapest filter first: title match. Everything else
                // (minimized, cloaked, owning process) is checked only for
                // the rare window that already matched.
                if (!LoginWindowGate.IsLoginTitle(buffer.ToString(0, Math.Min(length, buffer.Capacity)))) return true;

                if (!LoginWindowGate.IsLoginWindowCandidate(isVisible: true, IsIconic(hWnd), IsCloaked(hWnd), "Logon to PioneerRx")) return true;
                if (IsOwnedByExcludedProcess(hWnd)) return true;

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

    /// <summary>DWMWA_CLOAKED != 0 means hidden by the shell (e.g. another virtual desktop). A failed query counts as NOT cloaked.</summary>
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

    /// <summary>True for Rx Verify's own windows and browser/mail-client windows. Any lookup failure counts as NOT excluded, so a failure can never silently disable the feature.</summary>
    private static bool IsOwnedByExcludedProcess(IntPtr hWnd)
    {
        try
        {
            GetWindowThreadProcessId(hWnd, out var pid);
            if (pid == 0) return false;
            if (pid == (uint)Environment.ProcessId) return true;

            using var process = Process.GetProcessById((int)pid);
            return LoginWindowGate.IsExcludedProcessName(process.ProcessName);
        }
        catch
        {
            return false;
        }
    }
}
