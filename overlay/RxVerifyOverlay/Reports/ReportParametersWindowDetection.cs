using System;
using System.Collections.Generic;

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
/// GOAL brief step 1/3's pure decision: given a "before" set of window
/// handles (captured immediately before a row-open action) and an "after"
/// snapshot (captured on each poll tick afterward), which windows are
/// NEW. No FlaUI/UIA/Windows dependency at all - unit tested directly
/// with plain IntPtr/NativeWindowSnapshot values (see
/// RxVerifyOverlay.Tests/Reports/ReportParametersWindowDetectionTests.cs
/// and the scratch net8.0 project W-T92 round 6 used to run these same
/// assertions off Windows).
/// </summary>
public static class NewWindowDetector
{
    /// <summary>True (NewWindowAppeared) the moment ANY window in <paramref name="after"/> has a non-zero handle not present in <paramref name="before"/> - GOAL brief step 3's own rule ("a new window means the row opened"), independent of that window's title.</summary>
    public static NewWindowDetectionResult Detect(IReadOnlyCollection<IntPtr> before, IReadOnlyCollection<NativeWindowSnapshot> after) =>
        NewWindows(before, after).Count > 0 ? NewWindowDetectionResult.NewWindowAppeared : NewWindowDetectionResult.None;

    /// <summary>Every window in <paramref name="after"/> whose handle is non-zero and wasn't in <paramref name="before"/>, in <paramref name="after"/>'s own order - the caller (WaitForNewPioneerWindow) logs each one and takes the first as "the" new window.</summary>
    public static IReadOnlyList<NativeWindowSnapshot> NewWindows(IReadOnlyCollection<IntPtr> before, IReadOnlyCollection<NativeWindowSnapshot> after)
    {
        var result = new List<NativeWindowSnapshot>();
        foreach (var window in after)
        {
            if (window.Handle != IntPtr.Zero && !before.Contains(window.Handle))
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
