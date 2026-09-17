using System;
using System.Collections.Generic;

namespace RxVerifyOverlay.Reports;

/// <summary>
/// One enumerated top-level window's identity — Title/ProcessId/Handle
/// only, no FlaUI/UIA dependency — so the "which window is actually the
/// Save As dialog" decision below can be unit tested without touching
/// the desktop at all. Built by PioneerReportDriver.EnumeratePioneerOwnedTopLevelWindows
/// from real AutomationElements at runtime.
/// </summary>
public readonly record struct WindowCandidate(string Title, int ProcessId, IntPtr Handle);

/// <summary>
/// Review fix (safety blocker 1 — PioneerReportDriver.FindSaveAsDialog
/// used to match ANY top-level window whose title merely contained "Save
/// As", with no process/ownership check at all: if another app had a
/// "Save As"-titled window open, this driver would type Pioneer's output
/// path into it and press Enter). Pure decision, unit tested in
/// RxVerifyOverlay.Tests/Reports/AutomationWindowSelectionTests.cs:
/// restrict candidates to Pioneer's own process id, prefer whichever one
/// is the OS foreground window, and refuse to guess (return null - the
/// caller then fails the report rather than typing anywhere) when
/// there's no such match or an unresolvable tie among several
/// non-foreground candidates.
/// </summary>
public static class SaveAsWindowSelector
{
    public static IntPtr? Choose(IReadOnlyList<WindowCandidate> candidates, int pioneerProcessId, IntPtr foregroundHandle)
    {
        var matches = new List<WindowCandidate>();
        foreach (var candidate in candidates)
        {
            if (candidate.ProcessId == pioneerProcessId
                && !string.IsNullOrEmpty(candidate.Title)
                && candidate.Title.Contains("Save As", StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(candidate);
            }
        }

        if (matches.Count == 0) return null;

        foreach (var candidate in matches)
        {
            if (candidate.Handle == foregroundHandle) return candidate.Handle;
        }

        // Exactly one same-process "Save As" window and it isn't
        // foreground (e.g. focus hasn't settled yet) is still safe to use
        // — it's unambiguous. More than one, with none foreground, is
        // ambiguous: refuse rather than guess which one to type into.
        return matches.Count == 1 ? matches[0].Handle : null;
    }
}

/// <summary>
/// Review fix (safety blocker 2 — PioneerReportDriver.ClosePreview used
/// to send Alt+F4 unconditionally, with only a same-PROCESS foreground
/// check beforehand; if focus had reverted to Pioneer's MAIN window
/// after Save As closed, Alt+F4 would close Pioneer itself, not just the
/// report preview). Pure decision, unit tested: Alt+F4 is only ever
/// permitted as a last-resort fallback, and only when the OS foreground
/// window is confirmed to be the SPECIFIC preview window handle — never
/// "some Pioneer-owned window" and never when the preview handle is
/// unknown. The primary close path (WM_CLOSE targeted at the preview's
/// own handle, or a UIA WindowPattern.Close() on it) is attempted first
/// by the caller and never needs this decision at all — this governs
/// only whether the Alt+F4 fallback is safe to use after that fails.
/// </summary>
public static class PreviewCloseDecision
{
    public static bool ShouldFallBackToAltF4(bool primaryCloseSucceeded, IntPtr? previewHandle, IntPtr foregroundHandle)
    {
        if (primaryCloseSucceeded) return false;
        if (previewHandle is null || previewHandle.Value == IntPtr.Zero) return false;
        if (foregroundHandle == IntPtr.Zero) return false;

        return foregroundHandle == previewHandle.Value;
    }
}

/// <summary>
/// Round 2 fix (Will's first real run: "UIA navigation to 'Analysis' &gt;
/// 'Financial Reports' failed"). Pure decision behind
/// PioneerReportDriver.OpenRibbonScreen's post-strategy confirmation
/// check: after each navigation attempt (UIA name match, keyboard
/// KeyTips, keyboard Alt+letter accelerators), the driver polls for
/// either a descendant of the main window matching one of these hints
/// (exact name) OR a top-level Pioneer-owned window whose title CONTAINS
/// one of them — this class is only the "does this title count" half,
/// unit tested with plain strings, no FlaUI/UIA involved.
/// </summary>
public static class RibbonScreenConfirmation
{
    /// <summary>The screen name itself, plus PioneerRx's own "Run &lt;screen&gt;" convention observed in Will's video for the Financial Reports list — generalized here so it applies to any ribbon screen this driver opens (Financial Reports, Payments, ...), not just the one that happened to fail first.</summary>
    public static IReadOnlyList<string> BuildConfirmationHints(string screenName) =>
        new[] { screenName, $"Run {screenName}" };

    public static bool NameContainsAnyHint(string? name, IReadOnlyList<string> hints)
    {
        if (string.IsNullOrEmpty(name)) return false;

        foreach (var hint in hints)
        {
            if (!string.IsNullOrEmpty(hint) && name.Contains(hint, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
