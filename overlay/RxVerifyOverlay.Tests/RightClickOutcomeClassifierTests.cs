using RxVerifyOverlay.Integrated;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for RightClickOutcomeClassifier (Integrated/RightClickOutcomeClassifier.cs)
/// — the pure decision behind which dialog prefill mode a right-click
/// RightClickDetector already confirmed fired should open.
///
/// 2026-08-18 (owner: "right-click must work on EVERY field"): this used
/// to pin a 3-way Classify(reportingEnabled, isPatientField) that could
/// SUPPRESS the click entirely. Both suppressions are gone — right-click
/// now always raises ReportErrorRequested, for every field, on every
/// workstation regardless of OverlaySettings.RxVerifyReportKey. These
/// tests now pin the much simpler remaining job: picking the
/// patient-field-redacted prefill mode vs. the normal one.
/// </summary>
public class RightClickOutcomeClassifierTests
{
    [Fact]
    public void RaisedWhenNotAPatientField()
    {
        var outcome = RightClickOutcomeClassifier.Classify(isPatientField: false);

        Assert.Equal(RightClickOutcome.Raised, outcome);
    }

    [Fact]
    public void RaisedPatientFieldWhenFieldIsPatientData()
    {
        // Never suppressed anymore — still raised, just flagged so the
        // dialog knows to redact Source/Entered and withhold Correction.
        var outcome = RightClickOutcomeClassifier.Classify(isPatientField: true);

        Assert.Equal(RightClickOutcome.RaisedPatientField, outcome);
    }
}
