using RxVerifyOverlay.Integrated;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for RightClickOutcomeClassifier (Integrated/RightClickOutcomeClassifier.cs)
/// — the pure decision behind what happens to a right-click
/// RightClickDetector already confirmed fired. RXVERIFY-TROUBLESHOOT
/// round 2: OverlaySettings.RxVerifyReportKey being unset (no in-app UI
/// sets it) is the prime suspect for "right-click does nothing" — these
/// tests pin down that SuppressedReportingDisabled wins priority over
/// SuppressedPatientField so the diagnostic log always attributes a
/// suppressed click to the more likely real-world cause.
/// </summary>
public class RightClickOutcomeClassifierTests
{
    [Fact]
    public void RaisedWhenReportingEnabledAndNotAPatientField()
    {
        var outcome = RightClickOutcomeClassifier.Classify(reportingEnabled: true, isPatientField: false);

        Assert.Equal(RightClickOutcome.Raised, outcome);
    }

    [Fact]
    public void SuppressedReportingDisabledWhenReportingIsOff()
    {
        var outcome = RightClickOutcomeClassifier.Classify(reportingEnabled: false, isPatientField: false);

        Assert.Equal(RightClickOutcome.SuppressedReportingDisabled, outcome);
    }

    [Fact]
    public void SuppressedPatientFieldWhenReportingIsOnButFieldIsPatientData()
    {
        var outcome = RightClickOutcomeClassifier.Classify(reportingEnabled: true, isPatientField: true);

        Assert.Equal(RightClickOutcome.SuppressedPatientField, outcome);
    }

    [Fact]
    public void ReportingDisabledTakesPriorityOverPatientFieldWhenBothApply()
    {
        // Priority matters for DIAGNOSIS, not just correctness (both
        // inputs true still means "suppressed" either way) — attributing
        // it to the reporting gate points whoever reads the log at the
        // fix that's actually missing (no in-app way to set
        // RxVerifyReportKey at all), not a red herring about patient
        // fields.
        var outcome = RightClickOutcomeClassifier.Classify(reportingEnabled: false, isPatientField: true);

        Assert.Equal(RightClickOutcome.SuppressedReportingDisabled, outcome);
    }
}
