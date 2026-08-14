namespace RxVerifyOverlay.Integrated;

/// <summary>
/// What happens to a right-click that RightClickDetector already
/// confirmed fired (cursor over a hotspot, fresh press, no dialog
/// already open) — extracted (RXVERIFY-TROUBLESHOOT, 2026-08 round 2) so
/// IntegratedBoxesWindow.PollCursorForHover's diagnostic logging and
/// actual behavior both read the SAME decision, pure and unit-tested,
/// rather than an inline if/else that log lines and behavior could
/// silently drift apart from.
/// </summary>
public enum RightClickOutcome
{
    /// <summary>OverlaySettings.RxVerifyReportKey is unset — see that property's own doc. THE likely root cause behind "right-click does nothing": there is no in-app UI to set this key today, so a workstation that never had it configured directly in settings.json silently swallows every right-click, indistinguishable from a broken feature.</summary>
    SuppressedReportingDisabled,

    /// <summary>One of the 3 patient-identity fields (VerdictFieldInfo.IsPatientField) — by design, never turned into a report (see that property's doc).</summary>
    SuppressedPatientField,

    /// <summary>Neither gate applies — ReportErrorRequested should actually be raised.</summary>
    Raised
}

/// <summary>See RightClickOutcome's own doc.</summary>
public static class RightClickOutcomeClassifier
{
    /// <summary>
    /// Order matters for diagnosis, not just correctness: reportingEnabled
    /// is checked FIRST because it's the far more likely real-world cause
    /// (no in-app way to configure it at all) — a field that's ALSO a
    /// patient field on a workstation with no report key configured
    /// should be logged/attributed to the reporting-disabled gate, not
    /// the patient-field one, so the diagnostic trail points at the fix
    /// that actually matters.
    /// </summary>
    public static RightClickOutcome Classify(bool reportingEnabled, bool isPatientField)
    {
        if (!reportingEnabled) return RightClickOutcome.SuppressedReportingDisabled;
        if (isPatientField) return RightClickOutcome.SuppressedPatientField;
        return RightClickOutcome.Raised;
    }
}
