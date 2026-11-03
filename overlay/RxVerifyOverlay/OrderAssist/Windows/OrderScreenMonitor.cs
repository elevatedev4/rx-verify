using System;
using System.Runtime.InteropServices;
using System.Text;

namespace RxVerifyOverlay.OrderAssist.Windows;

/// <summary>
/// Always-on, cheap "is the pharmacist on a Pioneer order screen" probe for
/// automatic Order/Verify switching. Same precedent as
/// Integrated/LoginWindowMonitor (400 ms cache, UI-thread only so no locking,
/// every P/Invoke guarded) but FOREGROUND-based: one GetForegroundWindow plus,
/// only when the foreground window is a PioneerRx window, its title and its
/// root owner's title. See OrderScreenForegroundRule for the semantics
/// (a stale order window behind the main window does not count; clicking
/// this overlay or another app is "no information"). Runs regardless of Order
/// mode -- OrderAssistWindowLocator.Scan only runs while Order mode is on,
/// and this is what turns it on.
///
/// PHI: titles can carry patient data. They are compared and dropped; nothing
/// here stores or logs a title.
/// </summary>
public static class OrderScreenMonitor
{
    private const long CacheMs = 400;

    /// <summary>GA_ROOTOWNER: the owner-chain root of a window (a dialog's owning top-level window).</summary>
    private const uint GA_ROOTOWNER = 3;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    private static long _lastCheckTick = long.MinValue;

    /// <summary>
    /// Takes a fresh sample if the 400 ms cache has expired. Returns true
    /// (and sets <paramref name="screenPresent"/>) only for a FRESH sample;
    /// false means "cached, nothing new" so the caller's edge/debounce logic
    /// is never fed the same observation twice. Never throws.
    /// <paramref name="screenPresent"/> is null when there is no information
    /// (see OrderScreenForegroundRule).
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
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return OrderScreenForegroundRule.Evaluate(ForegroundOwner.Unknown, null, null);

        GetWindowThreadProcessId(foreground, out var pid);
        if (pid == (uint)Environment.ProcessId) return OrderScreenForegroundRule.Evaluate(ForegroundOwner.OverlayProcess, null, null);

        if (!OrderAssistWindowLocator.IsOwnedByPioneerRxProcess(foreground))
        {
            return OrderScreenForegroundRule.Evaluate(ForegroundOwner.OtherApp, null, null);
        }

        var foregroundTitle = ReadTitle(foreground);
        string? rootOwnerTitle = null;
        var root = GetAncestor(foreground, GA_ROOTOWNER);
        if (root != IntPtr.Zero && root != foreground) rootOwnerTitle = ReadTitle(root);

        return OrderScreenForegroundRule.Evaluate(ForegroundOwner.PioneerRx, foregroundTitle, rootOwnerTitle);
    }

    private static string? ReadTitle(IntPtr hWnd)
    {
        try
        {
            var buffer = new StringBuilder(256);
            var length = GetWindowTextW(hWnd, buffer, buffer.Capacity);
            return length <= 0 ? null : buffer.ToString(0, Math.Min(length, buffer.Capacity));
        }
        catch
        {
            return null;
        }
    }
}
