using System;
using System.Collections.Generic;
using System.Linq;

namespace RxVerifyOverlay.Reports;

/// <summary>
/// One captured descendant element — ControlType/Name/AutomationId/
/// ClassName/Depth ONLY, no FlaUI/AutomationElement dependency, so the
/// capping/formatting logic below is unit testable without touching
/// UIA/Windows at all. Built by PioneerReportDriver's own (FlaUI-
/// dependent) tree walk. NEVER carries a screen VALUE (edit/text
/// contents) — see UiaDumpFormatter's class doc for why.
/// </summary>
public readonly record struct UiaElementSnapshot(string ControlType, string Name, string AutomationId, string ClassName, int Depth);

/// <summary>One top-level window owned by Pioneer's process, for the diagnostic dump's window list — same (Title, ClassName)-only shape as UiaElementSnapshot, for the same reason.</summary>
public readonly record struct TopLevelWindowSnapshot(string Title, string ClassName);

/// <summary>
/// Round 2 fix (Will's first real run failed navigating "Analysis &gt;
/// Financial Reports" with no way to tell WHY — no UIA dump of Pioneer
/// exists anywhere in this repo). Formats a compact, capped diagnostic
/// dump PioneerReportDriver writes into the run log (never the app's own
/// log destinations skipped) whenever a navigation/lookup step fails, so
/// the NEXT run is self-diagnosing without a live debugger attached.
///
/// PHI SAFETY (GOAL brief step 1: "Never log field VALUES from Pioneer
/// screens"): every method here only ever formats ControlType/Name/
/// AutomationId/ClassName/Title — structural UI metadata, never an Edit/
/// Text control's on-screen VALUE. Callers must only ever pass element
/// NAMES here (a ribbon button's label, a window's title-bar text), not
/// entered field contents — same posture as Uia/UiaTreeWalker.cs's own
/// DumpTree (Name is the control's label there too, never a patient
/// field's value).
///
/// Pure/no I/O by construction so every shape (empty list, over-cap,
/// blank names) is unit tested in RxVerifyOverlay.Tests/Reports/
/// UiaDiagnosticsTests.cs without any live UIA tree at all.
/// </summary>
public static class UiaDumpFormatter
{
    /// <summary>
    /// One line per element with a NON-EMPTY Name (GOAL brief: "skip
    /// empty-name elements"), indented by Depth, capped at
    /// <paramref name="maxLines"/> with a trailing "... N more" line if
    /// the eligible (non-empty-name) element count exceeds the cap.
    /// </summary>
    public static IReadOnlyList<string> FormatElementDump(IReadOnlyList<UiaElementSnapshot> elements, int maxLines)
    {
        var eligible = elements.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
        var take = Math.Min(eligible.Count, Math.Max(0, maxLines));

        var lines = new List<string>(take + 1);
        for (var i = 0; i < take; i++)
        {
            var element = eligible[i];
            var indent = new string(' ', Math.Max(0, element.Depth) * 2);
            var controlType = string.IsNullOrEmpty(element.ControlType) ? "<unknown>" : element.ControlType;
            var automationId = string.IsNullOrEmpty(element.AutomationId) ? "" : element.AutomationId;
            var className = string.IsNullOrEmpty(element.ClassName) ? "" : element.ClassName;
            lines.Add($"{indent}{controlType} name='{element.Name}' id='{automationId}' class='{className}'");
        }

        if (eligible.Count > take)
        {
            lines.Add($"... {eligible.Count - take} more element(s) not shown (cap {maxLines}).");
        }

        return lines;
    }

    /// <summary>One "- '&lt;title&gt;' [&lt;class&gt;]" line per top-level window, in the order given.</summary>
    public static IReadOnlyList<string> FormatTopLevelWindowList(IReadOnlyList<TopLevelWindowSnapshot> windows)
    {
        var lines = new List<string>(windows.Count);
        foreach (var window in windows)
        {
            var title = string.IsNullOrEmpty(window.Title) ? "<untitled>" : window.Title;
            var className = string.IsNullOrEmpty(window.ClassName) ? "<unknown class>" : window.ClassName;
            lines.Add($"  - '{title}' [{className}]");
        }

        return lines;
    }

    /// <summary>The dump's header line identifying which window everything below was walked from.</summary>
    public static string FormatMainWindowHeader(string name, string className, int processId, IntPtr handle)
    {
        var safeName = string.IsNullOrEmpty(name) ? "<untitled>" : name;
        var safeClass = string.IsNullOrEmpty(className) ? "<unknown class>" : className;
        return $"Main window: name='{safeName}' class='{safeClass}' pid={processId} handle=0x{handle.ToInt64():X}";
    }
}
