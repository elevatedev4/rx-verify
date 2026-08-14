using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using RxVerifyOverlay.Uia;

namespace RxVerifyOverlay.OrderAssist.Windows;

/// <summary>
/// Finds whichever of the two Order Assist target Pioneer windows is the
/// current Order Assist target, by title. Deliberately its OWN small,
/// self-contained set of P/Invoke declarations rather than reusing
/// Integrated/IntegratedOverlayCoordinator's (private, and answering a
/// different question — "which window is PioneerRx's MAIN window" — that
/// neither of these two windows is) or Uia/PioneerRxWindow's (a UIA-based
/// attach to the Pre-Check/Edit/New-Rx screen specifically, via a shared
/// automation session this module has no business depending on). This
/// keeps OrderAssist's window-finding completely independent of the
/// verify flow's own — see OrderAssistCoordinator's class doc for why
/// that independence matters (turn-off/split-off-as-its-own-module
/// later).
///
/// POPUP FIX (owner's live pharmacy report, 2026-08-14 — see
/// OrderAssistWindowSelectionRule's own doc for the full bug/fix): this
/// USED to be foreground-only ("Kept intentionally simple: ... no
/// top-level-window enumeration"), which is exactly what broke when the
/// "Recommended Order - Catalog Item Substitution Selection" dialog
/// floats above the still-open "Create Recommended Orders" window and
/// neither ends up foreground. <see cref="Scan"/> now enumerates every
/// one of PioneerRx's own top-level windows (EnumWindows, same pattern as
/// Integrated/IntegratedOverlayCoordinator.cs EnumeratePioneerTopLevelWindows)
/// and hands them to OrderAssistWindowSelectionRule.Choose, which keeps
/// foreground as a fast path but no longer the only one. Still a
/// completely different, far cheaper cost profile than the verify flow's
/// own ~250ms tick — this only runs once per ~1s tick (see
/// OrderAssistCoordinator) — so paying for one Process.GetProcessById
/// per unique pid (cached, see <see cref="_pidIsPioneerCache"/>) across
/// every visible top-level window on the desktop is still fine.
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

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    public readonly record struct TargetWindow(OrderAssistWindowKind Kind, IntPtr Handle, Rectangle Bounds);

    /// <summary>
    /// One Scan() call's full result: the chosen target (if any — see
    /// OrderAssistWindowSelectionRule.Choose) AND, independent of whether
    /// anything matched, the title of every CURRENTLY VISIBLE top-level
    /// window owned by a PioneerRx process — this is remote-debugging
    /// infrastructure (branch brief item: "next failure report must be
    /// diagnosable from his logs"), letting OrderAssistCoordinator log
    /// exactly what Pioneer windows existed on a tick where nothing
    /// matched, without needing a second live repro.
    ///
    /// PHI CAVEAT: unlike the two Order Assist target windows (drug/
    /// inventory data only), OTHER visible PioneerRx windows Will has open
    /// at the same time (e.g. a Pre-Check/Edit Rx screen) CAN have a
    /// patient name in their own title bar. VisiblePioneerWindowTitles is
    /// only ever written to the same local, never-transmitted %TEMP% log
    /// file Ocr/OcrLogger.cs already documents this exact caveat for (raw
    /// OCR text) — see OrderAssistCoordinator's own logging call sites.
    /// </summary>
    public readonly record struct ScanResult(TargetWindow? Target, IReadOnlyList<string> VisiblePioneerWindowTitles);

    /// <summary>Cached wrapper around the FieldMap.TargetProcessNames check, keyed by pid — same pattern and same rationale as Integrated/IntegratedOverlayCoordinator.cs's own _pidIsPioneerCache (EnumeratePioneerTopLevelWindows runs this per VISIBLE top-level window on the whole desktop, every tick — caching means Process.GetProcessById, which opens a process handle, only ever pays once per unique pid). Never proactively evicted — see that class's doc for why a stale entry self-corrects (process exit) or is a vanishingly rare, accepted edge case (pid reuse).</summary>
    private static readonly Dictionary<uint, bool> PidIsPioneerCache = new();

    /// <summary>
    /// Enumerates every visible, non-minimized top-level window owned by a
    /// PioneerRx process, classifies each by title, and hands the result
    /// to OrderAssistWindowSelectionRule.Choose (foreground fast path,
    /// else first-in-Z-order eligible match — see that class's doc).
    /// Candidates are built in EnumWindows' own enumeration order, which
    /// is top-to-bottom Z order in practice (same assumption
    /// Integrated/IntegratedOverlayCoordinator.cs's own EnumWindows use
    /// already makes implicitly by not caring about order at all — this
    /// is the one place in this app that DOES rely on it, since two
    /// target-kind windows can legitimately both be open at once, one
    /// floating above the other).
    ///
    /// COST ORDERING (cheapest filter first, mirrors
    /// EnumeratePioneerTopLevelWindows' own reviewer-fixed ordering):
    /// IsWindowVisible (no process lookup) before the cached pid check,
    /// before GetWindowRect. Title is read for every visible PioneerRx
    /// window regardless of Kind, not just target-kind matches, purely
    /// for the diagnostic title list below — the classify call itself is
    /// cheap (no extra P/Invoke beyond the GetWindowText already needed
    /// for that list).
    /// </summary>
    public static ScanResult Scan()
    {
        var foregroundHandle = GetForegroundWindow();
        var candidates = new List<OrderAssistWindowSelectionRule.Candidate>();
        var visiblePioneerTitles = new List<string>();

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true; // keep enumerating -- cheapest possible check

            if (!IsOwnedByPioneerRxProcess(hwnd)) return true; // keep enumerating -- not ours at all

            var title = ReadWindowTitle(hwnd);
            visiblePioneerTitles.Add(string.IsNullOrWhiteSpace(title) ? "(untitled)" : title!);

            var kind = OrderAssistWindowClassifier.Classify(title);
            var isMinimized = IsIconic(hwnd);
            var hasRect = GetWindowRect(hwnd, out var rect);
            var bounds = hasRect ? Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom) : Rectangle.Empty;

            candidates.Add(new OrderAssistWindowSelectionRule.Candidate(hwnd, kind, IsVisible: true, IsMinimized: isMinimized, Bounds: bounds));

            return true; // keep enumerating -- need every candidate, not just the first
        }, IntPtr.Zero);

        var chosen = OrderAssistWindowSelectionRule.Choose(candidates, foregroundHandle);
        TargetWindow? target = chosen is { } c ? new TargetWindow(c.Kind, c.Handle, c.Bounds) : null;

        return new ScanResult(target, visiblePioneerTitles);
    }

    /// <summary>Same check/pattern as Integrated/IntegratedOverlayCoordinator.cs IsOwnedByPioneerRx, but CACHED by pid (see PidIsPioneerCache's doc) since this now runs once per visible top-level window on the desktop, not once per tick.</summary>
    private static bool IsOwnedByPioneerRxProcess(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var processId);

        if (PidIsPioneerCache.TryGetValue(processId, out var cached)) return cached;

        bool isPioneer;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            isPioneer = FieldMap.TargetProcessNames.Any(name => string.Equals(process.ProcessName, name, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            isPioneer = false;
        }

        PidIsPioneerCache[processId] = isPioneer;
        return isPioneer;
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
