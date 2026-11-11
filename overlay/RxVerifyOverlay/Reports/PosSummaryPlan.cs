using System;
using System.Collections.Generic;
using System.Linq;

namespace RxVerifyOverlay.Reports;

// POS Summary, read off Will's macro ("POS sales.mex", loop "Run POS reports").
// Will (HQ W-Q9, 2026-11-11): "The macro file has all the keystrokes and
// clicks that are used." The loop has NO navigation before its first key:
// it types straight away, and its last step waits for a window titled
// "Search Drawers" before the next day. So the start screen is the
// "Search Drawers" window, already open. Per day:
//   <ALT> <TAB> <TAB> <date> <TAB> <date> <ALT>o <ARROW DOWN> <ENTER>
//   -> a Save As appears ("Save as"): file name POS_<mm-dd-yy>, Save,
//      "Confirm Save As" -> overwrite, then the "Sales Summary Report"
//      window is closed (<ALT><F4>) and "Search Drawers" is waited for.
// Everything here is pure (no UIA / Win32) and unit tested.

public enum PosKey
{
    Alt,
    Tab,
    TypeDate,
    AltO,
    ArrowDown,
    Enter
}

/// <summary>Which window must be the OS foreground window right before a key is sent.</summary>
public enum PosForegroundRule
{
    /// <summary>The verified "Search Drawers" window itself (title re-checked).</summary>
    SearchDrawersWindow,

    /// <summary>
    /// The Search Drawers window, OR a NEW Pioneer window (not in the
    /// baseline taken before Alt+o) that is not Pioneer's main window - the
    /// menu popup Alt+o opens. Never the main window, never a window that
    /// was already there.
    /// </summary>
    SearchDrawersOrNewPioneerWindow
}

public readonly record struct PosKeyStep(PosKey Key, string? Text, PosForegroundRule Rule, int SettleMs);

public static class PosSummaryPlan
{
    public const string SearchDrawersTitle = "Search Drawers";
    public const string ReportWindowTitle = "Sales Summary Report";

    /// <summary>What the user is told when the start screen is not open. Plain words, no window titles from the machine.</summary>
    public const string OpenSearchDrawersMessage =
        "POS Summary starts on Pioneer's Search Drawers screen. Open Search Drawers and leave it in front, then run again. Nothing was sent to Pioneer.";

    /// <summary>The macro's keys for one day, in order, with its 100 ms waits after each date.</summary>
    public static IReadOnlyList<PosKeyStep> BuildKeys(DateTime day)
    {
        var date = DailySalesPlan.DateText(day);
        return new[]
        {
            new PosKeyStep(PosKey.Alt, null, PosForegroundRule.SearchDrawersWindow, 100),
            new PosKeyStep(PosKey.Tab, null, PosForegroundRule.SearchDrawersWindow, 100),
            new PosKeyStep(PosKey.Tab, null, PosForegroundRule.SearchDrawersWindow, 100),
            new PosKeyStep(PosKey.TypeDate, date, PosForegroundRule.SearchDrawersWindow, 100),
            new PosKeyStep(PosKey.Tab, null, PosForegroundRule.SearchDrawersWindow, 100),
            new PosKeyStep(PosKey.TypeDate, date, PosForegroundRule.SearchDrawersWindow, 100),
            new PosKeyStep(PosKey.AltO, null, PosForegroundRule.SearchDrawersWindow, 100),
            new PosKeyStep(PosKey.ArrowDown, null, PosForegroundRule.SearchDrawersOrNewPioneerWindow, 100),
            new PosKeyStep(PosKey.Enter, null, PosForegroundRule.SearchDrawersOrNewPioneerWindow, 100)
        };
    }

    /// <summary>
    /// May a key be sent right now? <paramref name="foreground"/> is the OS
    /// foreground window (null when unknown/invisible). The Search Drawers
    /// window qualifies only while its title still reads "Search Drawers"
    /// (<paramref name="drawersTitleStillMatches"/>) - so even when that
    /// screen IS Pioneer's main window, no key goes to it once the screen
    /// has changed. For the menu keys a NEW non-main Pioneer window (the
    /// popup Alt+o opened) also qualifies. Pioneer's main window never
    /// qualifies on any other basis.
    /// </summary>
    public static bool KeyTargetAllowed(
        PosForegroundRule rule, SaveAsCandidate? foreground, IntPtr drawersHandle, bool drawersTitleStillMatches,
        IntPtr mainHandle, int pioneerProcessId, IReadOnlyCollection<IntPtr>? baselineHandles)
    {
        if (foreground is null) return false;
        var fg = foreground.Value;
        if (fg.Handle == IntPtr.Zero || !fg.IsVisible) return false;

        if (drawersHandle != IntPtr.Zero && fg.Handle == drawersHandle) return drawersTitleStillMatches;
        if (rule == PosForegroundRule.SearchDrawersWindow) return false;

        if (!SaveAsDialogMatcher.BelongsToPioneer(fg, pioneerProcessId)) return false;
        if (mainHandle != IntPtr.Zero && fg.Handle == mainHandle) return false;
        return baselineHandles is not null && !baselineHandles.Contains(fg.Handle);
    }

    public static bool TitleContains(string? title, string needle) =>
        !string.IsNullOrEmpty(title) && title.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The "Search Drawers" window: visible, Pioneer pid or owner, title
    /// contains "Search Drawers". The foreground one wins, then enumeration
    /// order. Null when it is not open. The title is only compared, never logged.
    /// </summary>
    public static SaveAsCandidate? FindSearchDrawers(IReadOnlyList<SaveAsCandidate> all, int pioneerProcessId, IntPtr foregroundHandle)
    {
        var matches = all
            .Where(c => c.IsVisible && c.Handle != IntPtr.Zero
                        && SaveAsDialogMatcher.BelongsToPioneer(c, pioneerProcessId)
                        && TitleContains(c.Title, SearchDrawersTitle))
            .ToList();
        if (matches.Count == 0) return null;

        var foreground = matches.Where(c => foregroundHandle != IntPtr.Zero && c.Handle == foregroundHandle).Select(c => (SaveAsCandidate?)c).FirstOrDefault();
        return foreground ?? matches[0];
    }

    /// <summary>
    /// The "Sales Summary Report" window that the POS export left behind:
    /// visible, Pioneer pid or owner, title contains the report name, NEW
    /// since the baseline, and not one of the excluded handles (main window,
    /// Search Drawers). The window the macro closes with Alt+F4.
    /// </summary>
    public static SaveAsCandidate? FindReportWindow(
        IReadOnlyList<SaveAsCandidate> all, int pioneerProcessId,
        IReadOnlyCollection<IntPtr>? baselineHandles, IReadOnlyCollection<IntPtr>? excludedHandles)
    {
        foreach (var c in all)
        {
            if (!c.IsVisible || c.Handle == IntPtr.Zero) continue;
            if (!SaveAsDialogMatcher.BelongsToPioneer(c, pioneerProcessId)) continue;
            if (excludedHandles is not null && excludedHandles.Contains(c.Handle)) continue;
            if (baselineHandles is null || baselineHandles.Contains(c.Handle)) continue;
            if (!TitleContains(c.Title, ReportWindowTitle)) continue;
            return c;
        }

        return null;
    }
}
