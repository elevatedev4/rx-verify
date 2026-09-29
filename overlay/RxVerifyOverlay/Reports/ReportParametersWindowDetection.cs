using System;
using System.Collections.Generic;
using System.Linq;

namespace RxVerifyOverlay.Reports;

/// <summary>
/// W-T92 round 6 (Will's build da39140 run: the "Report Parameters" popup
/// DID open after the row double-click, but PioneerReportDriver's old
/// detection - a title match for the literal string "Report Parameters" -
/// never recognized it, so the driver assumed the row hadn't opened, fell
/// through to its keyboard-navigation fallback, and sent arrow keys into
/// what was now the popup's own date field ("scrolling through the
/// months"), eventually aborting with a "Pioneer lost focus" that was
/// really just a downstream symptom of the same missed popup). One native
/// top-level window's identity - Handle/ClassName/size/owner plus the
/// window's title LENGTH ONLY (never the title text itself - see
/// PioneerReportDriver.EnumerateVisibleTopLevelWindowsForProcess's own
/// doc for why nothing here can ever capture or leak field/patient
/// content) - so "did a new window appear" can be decided without ever
/// reading, matching, or depending on what any window is titled. Built by
/// PioneerReportDriver.EnumerateVisibleTopLevelWindowsForProcess from a
/// raw Win32 EnumWindows pass, independent of FlaUI/UIA entirely.
/// </summary>
public readonly record struct NativeWindowSnapshot(IntPtr Handle, string ClassName, int TitleLength, int Width, int Height, IntPtr Owner);

/// <summary>
/// Review fix (BLOCKING, round 6 review): the first cut of NewWindowDetector
/// treated ANY new handle as "the row opened" - a transient window that
/// has nothing to do with the report grid (a tooltip, a combo/auto-suggest
/// dropdown, an IME status window, ...) would trigger the exact same
/// false-positive hardStop the original title-matching bug produced, just
/// for a different reason: TrySelectAndOpenReportRow would cache THAT
/// window's handle and skip strategies 2/3 forever, and if the transient
/// window happened to accept foreground, date keystrokes would go into it
/// silently. Pure, unit-tested class-name/size checks - see
/// RxVerifyOverlay.Tests/Reports/ReportParametersWindowDetectionTests.cs.
/// </summary>
public static class TransientWindowFilter
{
    /// <summary>
    /// Window class-name substrings (case-insensitive) known to be
    /// transient UI chrome, never a real Pioneer dialog/popup: a tooltip
    /// (tooltips_class32), a combo-box's own dropdown list or an
    /// auto-suggest popup, IME status windows, and the handful of
    /// invisible/utility window classes Windows itself creates
    /// (BroadcastEventWindow, GDI+ Hook) that can still pass
    /// IsWindowVisible momentarily.
    /// </summary>
    private static readonly string[] TransientClassNameSubstrings =
    {
        "tooltips_class32",
        "Auto-Suggest Dropdown",
        "ComboLBox",
        "MSCTFIME",
        "IME",
        "BroadcastEventWindow",
        "GDI+ Hook"
    };

    /// <summary>A real "Report Parameters" popup is a full dialog with date fields/buttons on it - anything smaller than this in either dimension is presumed chrome (a tooltip, a 1x1 message-only window, a thin dropdown sliver), not a dialog someone could type a date into.</summary>
    private const int MinRealWindowDimension = 100;

    public static bool IsTransient(NativeWindowSnapshot window)
    {
        if (window.Width < MinRealWindowDimension || window.Height < MinRealWindowDimension) return true;

        foreach (var substring in TransientClassNameSubstrings)
        {
            if (window.ClassName.Contains(substring, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }
}

/// <summary>
/// GOAL brief step 1/3's pure decision: given a "before" set of window
/// handles (captured immediately before a row-open action) and an "after"
/// snapshot (captured on each poll tick afterward), which windows are
/// NEW - and, per the round-6 review fix, real (TransientWindowFilter
/// excludes tooltip/dropdown/IME/tiny chrome so it can never trigger the
/// same false-positive hardStop the original title-matching bug did, just
/// for a transient window instead of a title miss). No FlaUI/UIA/Windows
/// dependency at all - unit tested directly with plain
/// IntPtr/NativeWindowSnapshot values (see
/// RxVerifyOverlay.Tests/Reports/ReportParametersWindowDetectionTests.cs
/// and the scratch net8.0 project W-T92 round 6 used to run these same
/// assertions off Windows).
/// </summary>
public static class NewWindowDetector
{
    /// <summary>True (NewWindowAppeared) the moment ANY non-transient window in <paramref name="after"/> has a non-zero handle not present in <paramref name="before"/> - GOAL brief step 3's own rule ("a new window means the row opened"), independent of that window's title.</summary>
    public static NewWindowDetectionResult Detect(IReadOnlyCollection<IntPtr> before, IReadOnlyCollection<NativeWindowSnapshot> after) =>
        NewWindows(before, after).Count > 0 ? NewWindowDetectionResult.NewWindowAppeared : NewWindowDetectionResult.None;

    /// <summary>Every NEW (handle non-zero, not in <paramref name="before"/>) window in <paramref name="after"/> that TransientWindowFilter does NOT consider chrome, largest-by-area first - so a real popup is always preferred over some other new-but-smaller real window, and a tooltip/dropdown/IME window never appears at all. The caller (WaitForNewPioneerWindow) logs each returned window and takes the first (largest) as "the" new window.</summary>
    public static IReadOnlyList<NativeWindowSnapshot> NewWindows(IReadOnlyCollection<IntPtr> before, IReadOnlyCollection<NativeWindowSnapshot> after)
    {
        var candidates = new List<NativeWindowSnapshot>();
        foreach (var window in after)
        {
            if (window.Handle != IntPtr.Zero && !before.Contains(window.Handle) && !TransientWindowFilter.IsTransient(window))
            {
                candidates.Add(window);
            }
        }

        return candidates
            .OrderByDescending(w => (long)Math.Max(0, w.Width) * Math.Max(0, w.Height))
            .ToList();
    }

    /// <summary>Every NEW window <see cref="NewWindows"/> filtered OUT as transient chrome - WaitForNewPioneerWindow logs these too ("ignored transient: ...") so a real miss is still diagnosable from the run log, without ever letting one of them count as the popup.</summary>
    public static IReadOnlyList<NativeWindowSnapshot> IgnoredTransientWindows(IReadOnlyCollection<IntPtr> before, IReadOnlyCollection<NativeWindowSnapshot> after)
    {
        var result = new List<NativeWindowSnapshot>();
        foreach (var window in after)
        {
            if (window.Handle != IntPtr.Zero && !before.Contains(window.Handle) && TransientWindowFilter.IsTransient(window))
            {
                result.Add(window);
            }
        }

        return result;
    }
}

/// <summary>What NewWindowDetector.Detect found - see that method's own doc.</summary>
public enum NewWindowDetectionResult
{
    None,
    NewWindowAppeared
}

/// <summary>One formatted native-window-snapshot log line - class/title-length/size/owner-handle, never a title. Kept as its own pure class (same precedent as MainWindowCandidateLog in AutomationWindowSelection.cs) so the exact format is unit tested.</summary>
public static class NativeWindowSnapshotLog
{
    public static string Format(NativeWindowSnapshot window) =>
        $"class='{window.ClassName}' title_len={window.TitleLength} size={window.Width}x{window.Height} owner=0x{window.Owner.ToInt64():X} handle=0x{window.Handle.ToInt64():X}";

    /// <summary>Review fix companion: the "ignored transient: class (WxH)" line WaitForNewPioneerWindow logs for every window TransientWindowFilter excluded - deliberately shorter than Format (no handle/owner) since these were never candidates, just noise worth naming so a real miss is still diagnosable.</summary>
    public static string FormatIgnoredTransient(NativeWindowSnapshot window) =>
        $"ignored transient: {window.ClassName} ({window.Width}x{window.Height})";
}

/// <summary>
/// GOAL brief step 2 (W-T92 round 6: "'Pioneer lost focus' must mean the
/// foreground window belongs to a different PROCESS than Pioneer - any
/// window of the Pioneer PID (main window, the popup, its dialogs) counts
/// as focused"). Pure decision behind
/// PioneerReportDriver.IsForegroundOwnedByPioneer - that method was
/// already comparing process ids (not a specific window handle), so this
/// factors the existing correct behavior out into its own unit-tested
/// class rather than changing it.
/// </summary>
public static class ForegroundOwnershipRule
{
    public static bool IsStillFocused(int foregroundProcessId, int pioneerProcessId) =>
        pioneerProcessId != 0 && foregroundProcessId == pioneerProcessId;
}
