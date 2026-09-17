using System;
using System.Collections.Generic;
using System.Linq;

namespace RxVerifyOverlay.Reports;

/// <summary>
/// One captured descendant element — ControlType/Name/AutomationId/
/// ClassName/Depth ONLY, no FlaUI/AutomationElement dependency, so the
/// capping/formatting/redaction logic below is unit testable without
/// touching UIA/Windows at all. Built by PioneerReportDriver's own
/// (FlaUI-dependent) tree walk. Name here is the RAW UIA Name — for a
/// value-bearing control (see UiaNameRedaction) that can legally be a
/// screen VALUE, not just a label — held only transiently in memory;
/// UiaDumpFormatter.FormatElementDump is what redacts it before any
/// line derived from it is ever written to a log file (see that class's
/// and UiaNameRedaction's own docs for why).
/// </summary>
public readonly record struct UiaElementSnapshot(string ControlType, string Name, string AutomationId, string ClassName, int Depth);

/// <summary>One top-level window owned by Pioneer's process, for the diagnostic dump's window list — same (Title, ClassName)-only shape as UiaElementSnapshot, for the same reason.</summary>
public readonly record struct TopLevelWindowSnapshot(string Title, string ClassName);

/// <summary>
/// Review fix (blocker 1, PHI): CaptureDescendantsRecursive walks EVERY
/// descendant of the main window, including whatever screen is showing
/// at the time - e.g. the Third Party Payments/claims search results
/// grid. WinForms DataGridView (and other legacy providers) commonly
/// expose a CELL'S VALUE as its UIA Name, not just a label - so an
/// unfiltered dump of that screen would write patient/claims data
/// straight into run-&lt;timestamp&gt;.log. This class is the single place
/// that decision is made, applied by UiaDumpFormatter.FormatElementDump
/// so it's exercised by the SAME pure unit tests as the rest of the
/// formatter (RxVerifyOverlay.Tests/Reports/UiaDiagnosticsTests.cs) -
/// nothing PHI-bearing needs to reach a live UIA tree to prove this is
/// safe.
///
/// ALLOWLIST, not a blocklist: only the control types below (chrome/
/// navigation elements whose Name is always a fixed UI LABEL - a button
/// caption, a tab title, a menu item) are considered safe to log as-is.
/// Everything else - Edit, Document, Text, DataItem, DataGrid, ListItem,
/// TreeItem, Custom, ComboBox, Spinner, Hyperlink, and any ControlType
/// not explicitly listed here at all - is redacted. Defaulting unknown/
/// unlisted control types to REDACTED (rather than assuming they're
/// safe) is deliberate: an exotic or future ControlType this class has
/// never seen is exactly the kind of gap that would otherwise let a
/// value slip through unnoticed.
/// </summary>
public static class UiaNameRedaction
{
    public const string RedactedName = "[redacted]";
    private const int MaxNameLength = 40;

    private static readonly HashSet<string> NameSafeControlTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Button", "TabItem", "MenuItem", "Menu", "MenuBar", "ToolBar", "Tab",
        "Pane", "Window", "Group", "Header", "HeaderItem", "SplitButton",
        "CheckBox", "RadioButton", "TitleBar"
    };

    public static bool IsNameSafeToLog(string? controlType) =>
        !string.IsNullOrEmpty(controlType) && NameSafeControlTypes.Contains(controlType);

    /// <summary>The Name to actually write to a log line for an element of this ControlType - the real Name (capped at 40 chars) if the ControlType is on the safe allowlist, "[redacted]" otherwise.</summary>
    public static string RedactIfNeeded(string? controlType, string? name)
    {
        if (!IsNameSafeToLog(controlType)) return RedactedName;

        if (string.IsNullOrEmpty(name) || name.Length <= MaxNameLength) return name ?? string.Empty;
        return name.Substring(0, MaxNameLength);
    }
}

/// <summary>
/// Round 2 fix (Will's first real run failed navigating "Analysis &gt;
/// Financial Reports" with no way to tell WHY — no UIA dump of Pioneer
/// exists anywhere in this repo). Formats a compact, capped diagnostic
/// dump PioneerReportDriver writes into the run log (never the app's own
/// log destinations skipped) whenever a navigation/lookup step fails, so
/// the NEXT run is self-diagnosing without a live debugger attached.
///
/// PHI SAFETY (GOAL brief step 1: "Never log field VALUES from Pioneer
/// screens"; hardened per review blocker 1): every element's Name is run
/// through UiaNameRedaction.RedactIfNeeded before it is ever formatted
/// into a line - a value-bearing control (or anything not on the known-
/// safe allowlist) is REPLACED, never merely truncated, so its actual
/// on-screen content never reaches a line this method returns. Callers
/// must still only ever pass element NAMES here (never something already
/// known to be a field's raw value) - this is defense in depth on top of
/// that, not a substitute for it.
///
/// Pure/no I/O by construction so every shape (empty list, over-cap,
/// blank names, redaction) is unit tested in RxVerifyOverlay.Tests/
/// Reports/UiaDiagnosticsTests.cs without any live UIA tree at all.
/// </summary>
public static class UiaDumpFormatter
{
    /// <summary>
    /// One line per element with a NON-EMPTY raw Name (GOAL brief: "skip
    /// empty-name elements"), indented by Depth, capped at
    /// <paramref name="maxLines"/> with a trailing "... N more" line if
    /// the eligible (non-empty-name) element count exceeds the cap. The
    /// Name actually written is UiaNameRedaction.RedactIfNeeded's result,
    /// never the raw element.Name directly (see class doc / blocker 1).
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
            var safeName = UiaNameRedaction.RedactIfNeeded(element.ControlType, element.Name);
            lines.Add($"{indent}{controlType} name='{safeName}' id='{automationId}' class='{className}'");
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
