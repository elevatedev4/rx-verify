using System;
using System.Collections.Generic;
using System.Linq;

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
/// <summary>
/// Round 3 fix (owner's first real run: FindMainWindow resolved to pid
/// 24828, handle 0x0, name='&lt;untitled&gt;' — a Pioneer HELPER process with
/// no real window at all — while the actual UI was pid 20664; the old
/// loop just took the FIRST desktop-enumeration match whose process name
/// or window Name happened to look right, with no ranking). One
/// enumerated top-level window's candidacy data — Title/ProcessId/Handle/
/// ClassName/Width/Height plus the two impure facts the caller already
/// had to compute anyway (IsPioneerProcess via Process.GetProcessById,
/// IsPrecheckFamily via FieldMap.TargetWindowTitlePrefixes) — so the
/// actual ranking decision below is pure and unit testable (see
/// RxVerifyOverlay.Tests/Reports/AutomationWindowSelectionTests.cs
/// MainWindowSelectorTests) without touching FlaUI/UIA/Windows at all.
/// Built by PioneerReportDriver.TryFindMainWindowOnce from real
/// AutomationElements at runtime.
/// </summary>
public readonly record struct MainWindowCandidate(
    string Title, int ProcessId, IntPtr Handle, string ClassName, int Width, int Height,
    bool IsPioneerProcess, bool IsPrecheckFamily);

/// <summary>
/// The pure ranking decision behind PioneerReportDriver.FindMainWindow:
/// eligible candidates are Pioneer-owned (by process name OR a title
/// containing "Pioneer"), not one of the Pre-Check/Edit/New-Rx family
/// windows Uia/PioneerRxWindow.cs already owns, and have BOTH a non-zero
/// native handle AND a non-empty title (excludes exactly the round-3 bug:
/// a same-process helper window with handle=0x0/untitled). Among those,
/// prefer the largest VISIBLE WindowsForms10.* window (Pioneer's real
/// shell is a WinForms app) — falling back to the largest eligible
/// candidate of any class if none matches that class prefix, rather than
/// returning nothing just because a class name looked unexpected.
/// </summary>
public static class MainWindowSelector
{
    public static MainWindowCandidate? Choose(IReadOnlyList<MainWindowCandidate> candidates)
    {
        var eligible = candidates
            .Where(c => !c.IsPrecheckFamily
                        && c.Handle != IntPtr.Zero
                        && !string.IsNullOrEmpty(c.Title)
                        && (c.IsPioneerProcess || c.Title.Contains("Pioneer", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (eligible.Count == 0) return null;

        var winFormsCandidates = eligible
            .Where(c => c.ClassName.StartsWith("WindowsForms10.", StringComparison.Ordinal))
            .ToList();

        var pool = winFormsCandidates.Count > 0 ? winFormsCandidates : eligible;

        return pool.OrderByDescending(c => (long)Math.Max(0, c.Width) * Math.Max(0, c.Height)).First();
    }
}

/// <summary>
/// Round 3 fix (GOAL brief step 1: "log every candidate ... the
/// main-window title is a screen name, log it only when it matches a
/// known screen-name allow-list ... else '[redacted]'"). Deliberately
/// separate allow-list from UiaNameRedaction/ReportRowNameRedaction
/// (control-type based / catalog-row-name based respectively) — a
/// PioneerRx main window's TITLE is the current SCREEN name, which could
/// in principle carry something patient-specific on a screen this app
/// doesn't otherwise expect (the whole reason FindMainWindow logs pid/
/// handle/class/title-LENGTH unconditionally but the title text only
/// when it's a screen name this app already knows about).
/// </summary>
public static class MainWindowTitleRedaction
{
    private static readonly HashSet<string> KnownScreenNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Run Financial Reports", "Financial Reports",
        "Run Payments", "Payments",
    };

    public static string RedactIfNeeded(string? title)
    {
        if (string.IsNullOrEmpty(title)) return "<untitled>";
        return KnownScreenNames.Contains(title) ? title : UiaNameRedaction.RedactedName;
    }
}

/// <summary>One formatted candidate log line — pid/handle/class/title-length always, title text only per MainWindowTitleRedaction. Kept as its own pure class (rather than inline string interpolation in PioneerReportDriver) purely so the exact format is unit tested.</summary>
public static class MainWindowCandidateLog
{
    public static string Format(MainWindowCandidate candidate)
    {
        var titleLength = candidate.Title.Length;
        var titleForLog = MainWindowTitleRedaction.RedactIfNeeded(candidate.Title);
        return $"  candidate pid={candidate.ProcessId} handle=0x{candidate.Handle.ToInt64():X} class='{candidate.ClassName}' title_len={titleLength} title='{titleForLog}'";
    }
}

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
