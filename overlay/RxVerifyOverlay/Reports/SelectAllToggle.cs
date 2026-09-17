using System.Collections.Generic;

namespace RxVerifyOverlay.Reports;

/// <summary>
/// Pure logic behind ReportsWindow's "Select all" / "Unselect all"
/// toggle button (GOAL brief round 2, step 4) — kept UI-free so it's
/// unit testable without WPF, same pattern as AutomationWindowSelection.cs's
/// SaveAsWindowSelector/PreviewCloseDecision. ReportsWindow.xaml.cs is the
/// only caller: it projects its ObservableCollection&lt;ReportRowViewModel&gt;
/// into (IsEnabled, IsSelected) tuples and applies the result back.
///
/// Only ENABLED rows (the non-greyed reports — see ReportRowViewModel.
/// IsEnabled, false only for the POS-daily placeholder) ever count toward
/// "all selected" or get toggled; a disabled row's own IsSelected is
/// never read or written by this class.
/// </summary>
public static class SelectAllToggle
{
    public const string SelectAllLabel = "Select all";
    public const string UnselectAllLabel = "Unselect all";

    /// <summary>
    /// True only when there is at least one enabled row AND every enabled
    /// row is currently selected — the state that flips the button's
    /// label to "Unselect all". Zero enabled rows (shouldn't happen today
    /// — POS daily is the only disabled entry — but the catalog could
    /// grow) is treated as "not all selected" rather than vacuously true,
    /// so the button never claims "Unselect all" with nothing to unselect.
    /// </summary>
    public static bool AreAllEnabledSelected(IReadOnlyList<(bool IsEnabled, bool IsSelected)> rows)
    {
        var sawEnabledRow = false;
        foreach (var row in rows)
        {
            if (!row.IsEnabled) continue;
            sawEnabledRow = true;
            if (!row.IsSelected) return false;
        }

        return sawEnabledRow;
    }

    /// <summary>The label the button should show right now, given the rows' current state.</summary>
    public static string LabelFor(IReadOnlyList<(bool IsEnabled, bool IsSelected)> rows) =>
        AreAllEnabledSelected(rows) ? UnselectAllLabel : SelectAllLabel;

    /// <summary>
    /// What every enabled row's IsSelected should become when the button
    /// is clicked right now (evaluated BEFORE the click is applied) — the
    /// opposite of the current all-selected state, so one click always
    /// either selects every enabled row or clears every enabled row, never
    /// a per-row toggle.
    /// </summary>
    public static bool NextSelectedState(IReadOnlyList<(bool IsEnabled, bool IsSelected)> rows) =>
        !AreAllEnabledSelected(rows);
}
