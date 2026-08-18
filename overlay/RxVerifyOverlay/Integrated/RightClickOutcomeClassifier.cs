namespace RxVerifyOverlay.Integrated;

/// <summary>
/// Which dialog PREFILL MODE a right-click that RightClickDetector already
/// confirmed fired (cursor over a hotspot, fresh press, no dialog already
/// open) should open — extracted (RXVERIFY-TROUBLESHOOT, 2026-08 round 2)
/// so IntegratedBoxesWindow.PollCursorForHover's diagnostic logging and
/// actual behavior both read the SAME decision, pure and unit-tested,
/// rather than an inline if/else that log lines and behavior could
/// silently drift apart from.
///
/// 2026-08-18 (owner: "right-click must work on EVERY field"): this used
/// to be a 3-way Classify(reportingEnabled, isPatientField) that could
/// SUPPRESS the click entirely — SuppressedReportingDisabled when
/// OverlaySettings.RxVerifyReportKey was unset, SuppressedPatientField for
/// the 3 patient-identity fields (see git history for that shape). Both
/// suppressions are gone: a right-click on ANY field now always opens
/// Integrated/ReportErrorWindow. A missing report key instead opens the
/// dialog with Submit disabled and an inline note (see
/// IntegratedBoxesWindow.PollCursorForHover and ReportErrorWindow's own
/// doc) — reportingEnabled is threaded straight through
/// ReportErrorRequestInfo for that, it no longer needs classifying here.
/// This type now exists purely to pick the PATIENT-FIELD PREFILL MODE
/// (redacted Source/Entered + withheld correction, see RaisedPatientField's
/// own doc) — never whether to raise at all.
/// </summary>
public enum RightClickOutcome
{
    /// <summary>Not a patient field — ReportErrorWindow shows the field's real Source/Entered values and accepts a free-text Correction, same as always.</summary>
    Raised,

    /// <summary>One of the 3 patient-identity fields (VerdictFieldInfo.IsPatientField). ReportErrorWindow still opens — it shows a redacted "[hidden — patient field]" placeholder instead of the real Source/Entered, and Reporting/RxReportBuilder.cs drops whatever the pharmacist typed into Correction, replacing it with a fixed reason code (see that builder's doc) so a correction for e.g. patientName can never BE the patient name in the submitted payload.</summary>
    RaisedPatientField
}

/// <summary>See RightClickOutcome's own doc.</summary>
public static class RightClickOutcomeClassifier
{
    public static RightClickOutcome Classify(bool isPatientField) =>
        isPatientField ? RightClickOutcome.RaisedPatientField : RightClickOutcome.Raised;
}
