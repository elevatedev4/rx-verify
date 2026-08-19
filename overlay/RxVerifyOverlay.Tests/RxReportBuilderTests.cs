using System;
using RxVerifyOverlay.Diagnostics;
using RxVerifyOverlay.Integrated;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.Reporting;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for RxReportBuilder (Reporting/RxReportPayload.cs) — the
/// pure function behind Integrated/ReportErrorWindow.xaml.cs's Submit
/// button, and the belt-and-suspenders enforcement point for the owner's
/// "NO patient fields in the payload" design decision (HQ-ENDPOINT-SPEC.md)
/// — see VerdictFieldInfo.IsPatientField's doc for the UI-level guard this
/// backs up. All field values below are synthetic.
/// </summary>
public class RxReportBuilderTests
{
    private static readonly DateTime CreatedAt = new(2026, 8, 13, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NonPatientFieldKeepsSourceAndEnteredVerbatim()
    {
        var field = new VerdictFieldInfo(
            FieldKey: "quantity",
            DisplayName: "Quantity",
            Status: VerdictStatus.Red,
            SourceValue: "60",
            EnteredValue: "90",
            Explanation: "Quantity mismatch",
            ReasonCode: "qty_mismatch");

        var payload = RxReportBuilder.Build(field, "Should be 60, not 90", "abc123 2026-08-13T00:00:00Z", "deadbee", CreatedAt);

        Assert.Equal("60", payload.Source);
        Assert.Equal("90", payload.Entered);
        Assert.Equal("quantity", payload.Field);
        Assert.Equal("red", payload.Status);
        Assert.Equal("qty_mismatch", payload.ReasonCode);
        Assert.Equal("Quantity mismatch", payload.Explanation);
        Assert.Equal("Should be 60, not 90", payload.Correction);
        Assert.Equal("rx-verify", payload.App);
        Assert.Equal("abc123 2026-08-13T00:00:00Z", payload.EngineBuild);
        Assert.Equal("deadbee", payload.Commit);
        Assert.Equal(CreatedAt, payload.CreatedAt);
    }

    [Theory]
    [InlineData("patientName")]
    [InlineData("patientDOB")]
    [InlineData("patientAddress")]
    public void PatientFieldsAlwaysRedactSourceAndEntered(string patientFieldKey)
    {
        var field = new VerdictFieldInfo(
            FieldKey: patientFieldKey,
            DisplayName: "whatever",
            Status: VerdictStatus.Yellow,
            SourceValue: "SYNTHETIC-SOURCE-VALUE",
            EnteredValue: "SYNTHETIC-ENTERED-VALUE",
            Explanation: "not provided",
            ReasonCode: "not_provided");

        var payload = RxReportBuilder.Build(field, "some correction text", null, null, CreatedAt);

        Assert.Equal(RxLogFormatter.RedactedValue, payload.Source);
        Assert.Equal(RxLogFormatter.RedactedValue, payload.Entered);
        Assert.DoesNotContain("SYNTHETIC", payload.Source);
        Assert.DoesNotContain("SYNTHETIC", payload.Entered);
    }

    [Fact]
    public void StatusIsLowercasedToMatchTheEngineWireConvention()
    {
        var field = new VerdictFieldInfo("drug", "Drug", VerdictStatus.Green, "Lisinopril", "Lisinopril", "", "");

        var payload = RxReportBuilder.Build(field, "", null, null, CreatedAt);

        Assert.Equal("green", payload.Status);
    }

    [Fact]
    public void NullEngineBuildAndCommitPassThroughAsNull()
    {
        var field = new VerdictFieldInfo("sig", "Sig / Directions", VerdictStatus.Green, "take 1 tab daily", "take 1 tab daily", "", "");

        var payload = RxReportBuilder.Build(field, "", engineBuild: null, commit: null, CreatedAt);

        Assert.Null(payload.EngineBuild);
        Assert.Null(payload.Commit);
    }

    [Fact]
    public void EmptyCorrectionBecomesEmptyStringNotNull()
    {
        var field = new VerdictFieldInfo("refills", "Refills", VerdictStatus.Red, "2", "3", "", "");

        var payload = RxReportBuilder.Build(field, correction: "", null, null, CreatedAt);

        Assert.Equal("", payload.Correction);
    }

    [Fact]
    public void SourceInputModeDefaultsToNullWhenNotPassed()
    {
        // 2026-08-17 fix round, item 2: existing call sites/tests that
        // don't care about this diagnostic shouldn't need updating.
        var field = new VerdictFieldInfo("refills", "Refills", VerdictStatus.Red, "2", "3", "", "");

        var payload = RxReportBuilder.Build(field, "some correction", null, null, CreatedAt);

        Assert.Null(payload.SourceInputMode);
    }

    [Fact]
    public void SourceInputModePassesThroughVerbatim()
    {
        var field = new VerdictFieldInfo("refills", "Refills", VerdictStatus.Red, "2", "3", "", "");

        var payload = RxReportBuilder.Build(field, "some correction", null, null, CreatedAt, sourceInputMode: "uia");

        Assert.Equal("uia", payload.SourceInputMode);
    }

    [Fact]
    public void RefillsTotalFillsDiagnosticsPassThroughFromTheField()
    {
        // The diagnostic lives on VerdictFieldInfo (populated by
        // Integrated/IntegratedOverlayCoordinator.cs UpdateBoxes only for
        // the refills row — see that class's doc) and Build just carries
        // it straight into the payload, unredacted (label text only,
        // never a value — see RxReportPayload.RefillsTotalFillsLabelPrefix
        // doc).
        var field = new VerdictFieldInfo(
            "refills", "Refills", VerdictStatus.Yellow, "(not provided)", "2", "not provided", "not_provided",
            RefillsTotalFillsLabelSeen: true,
            RefillsTotalFillsLabelPrefix: "Total fills: ");

        var payload = RxReportBuilder.Build(field, "should be 2, source shows Total Fills 3", null, null, CreatedAt, sourceInputMode: "uia");

        Assert.True(payload.RefillsTotalFillsLabelSeen);
        Assert.Equal("Total fills: ", payload.RefillsTotalFillsLabelPrefix);
    }

    [Fact]
    public void RefillsTotalFillsDiagnosticsDefaultToNullForOtherFields()
    {
        var field = new VerdictFieldInfo("quantity", "Quantity", VerdictStatus.Red, "60", "90", "Quantity mismatch", "qty_mismatch");

        var payload = RxReportBuilder.Build(field, "correction", null, null, CreatedAt, sourceInputMode: "uia");

        Assert.Null(payload.RefillsTotalFillsLabelSeen);
        Assert.Null(payload.RefillsTotalFillsLabelPrefix);
    }

    [Fact]
    public void LogTailDefaultsToNullWhenNotPassed()
    {
        // Same "existing call sites/tests shouldn't need updating" posture
        // as SourceInputMode above.
        var field = new VerdictFieldInfo("refills", "Refills", VerdictStatus.Red, "2", "3", "", "");

        var payload = RxReportBuilder.Build(field, "some correction", null, null, CreatedAt);

        Assert.Null(payload.LogTail);
    }

    [Fact]
    public void LogTailPassesThroughVerbatim()
    {
        // Build does no I/O and no further scrubbing of this string — the
        // caller (Integrated/ReportErrorWindow.xaml.cs) is responsible for
        // having already run it through Diagnostics/LogTailBuilder.
        // BuildSafeTail before it ever reaches here.
        var field = new VerdictFieldInfo("refills", "Refills", VerdictStatus.Red, "2", "3", "", "");
        const string tail = "[2026-08-17 10:00:00.000] Timing: detect->render 100ms (...)";

        var payload = RxReportBuilder.Build(field, "some correction", null, null, CreatedAt, logTail: tail);

        Assert.Equal(tail, payload.LogTail);
    }

    [Fact]
    public void EmptyLogTailBecomesNullNotEmptyString()
    {
        // LogTailBuilder.BuildSafeTail returns "" (never null) when nothing
        // safe was found — Build normalizes that to null so the payload
        // matches SourceInputMode/EngineBuild's existing "omit rather than
        // print empty" convention.
        var field = new VerdictFieldInfo("refills", "Refills", VerdictStatus.Red, "2", "3", "", "");

        var payload = RxReportBuilder.Build(field, "some correction", null, null, CreatedAt, logTail: "");

        Assert.Null(payload.LogTail);
    }

    // 2026-08-19 policy change (Will verbatim: "On reporting an error on
    // patient address, it won't let me type anything in the box... Fix
    // that"): a patient field's typed Correction is now sent AS TYPED,
    // UNLESS PatientFieldCorrectionGuard.ContainsPatientValueFragment
    // finds it leaks the field's own real captured Source/Entered value —
    // see RxVerifyOverlay.Tests/PatientFieldCorrectionGuardTests.cs for
    // the guard logic itself; these tests pin down Build's USE of it
    // (previously the replacement was unconditional — see this section's
    // git history for that older, stricter shape).
    [Theory]
    [InlineData("patientName")]
    [InlineData("patientDOB")]
    [InlineData("patientAddress")]
    public void PatientFieldSafeDescriptiveCorrectionIsSentAsTyped(string patientFieldKey)
    {
        var field = new VerdictFieldInfo(
            FieldKey: patientFieldKey,
            DisplayName: "whatever",
            Status: VerdictStatus.Yellow,
            SourceValue: "JORDAN QUINCY TESTPATIENT",
            EnteredValue: "JORDAN Q TESTPATIENT",
            Explanation: "not provided",
            ReasonCode: "not_provided");

        const string safeCorrection = "the two spellings do not match, please verify against the script";
        var payload = RxReportBuilder.Build(field, safeCorrection, null, null, CreatedAt);

        Assert.Equal(safeCorrection, payload.Correction);
    }

    [Theory]
    [InlineData("patientName")]
    [InlineData("patientDOB")]
    [InlineData("patientAddress")]
    public void PatientFieldCorrectionContainingTheExactSourceValueIsWithheld(string patientFieldKey)
    {
        var field = new VerdictFieldInfo(
            FieldKey: patientFieldKey,
            DisplayName: "whatever",
            Status: VerdictStatus.Yellow,
            SourceValue: "JORDAN QUINCY TESTPATIENT",
            EnteredValue: "JORDAN Q TESTPATIENT",
            Explanation: "not provided",
            ReasonCode: "not_provided");

        var payload = RxReportBuilder.Build(field, "should be JORDAN QUINCY TESTPATIENT not the entered version", null, null, CreatedAt);

        Assert.Equal(RxReportBuilder.PatientFieldCorrectionWithheldText, payload.Correction);
        Assert.DoesNotContain("JORDAN", payload.Correction);
    }

    [Fact]
    public void PatientFieldCorrectionContainingOnlyAFragmentOfTheEnteredValueIsWithheld()
    {
        var field = new VerdictFieldInfo(
            "patientAddress", "Patient Address", VerdictStatus.Yellow,
            "123 MAIN STREET APT 4", "123 MAIN ST APT 4", "not provided", "not_provided");

        var payload = RxReportBuilder.Build(field, "entered says main street apt but should be different", null, null, CreatedAt);

        Assert.Equal(RxReportBuilder.PatientFieldCorrectionWithheldText, payload.Correction);
    }

    [Fact]
    public void PatientFieldCorrectionWithRearrangedWordsOfTheSourceValueIsStillWithheld()
    {
        var field = new VerdictFieldInfo(
            "patientName", "Patient Name", VerdictStatus.Yellow,
            "TESTPATIENT JORDAN QUINCY", "JORDAN Q TESTPATIENT", "not provided", "not_provided");

        var payload = RxReportBuilder.Build(field, "QUINCY JORDAN TESTPATIENT is the correct order", null, null, CreatedAt);

        Assert.Equal(RxReportBuilder.PatientFieldCorrectionWithheldText, payload.Correction);
    }

    [Fact]
    public void PatientFieldEmptyCorrectionPassesThroughAsEmptyNotWithheld()
    {
        // 2026-08-19: an empty correction has nothing to leak — the
        // withheld stand-in is reserved for text that actually contains
        // the patient's data, not "any patient-field correction at all"
        // (the old, unconditional policy this replaces).
        var field = new VerdictFieldInfo(
            "patientAddress", "Patient Address", VerdictStatus.Yellow, "SYNTHETIC-SOURCE", "SYNTHETIC-ENTERED", "not provided", "not_provided");

        var payload = RxReportBuilder.Build(field, "", null, null, CreatedAt);

        Assert.Equal("", payload.Correction);
    }

    [Fact]
    public void PatientFieldSourceAndEnteredRedactionIsUnaffectedByTheCorrectionOutcomeEitherWay()
    {
        // Source/Entered redaction is a completely separate rule from the
        // Correction guard — confirms neither a safe nor a withheld
        // correction changes it.
        var field = new VerdictFieldInfo(
            "patientAddress", "Patient Address", VerdictStatus.Yellow,
            "JORDAN QUINCY TESTPATIENT", "JORDAN Q TESTPATIENT", "not provided", "not_provided");

        var safePayload = RxReportBuilder.Build(field, "safe description here", null, null, CreatedAt);
        var withheldPayload = RxReportBuilder.Build(field, "should be JORDAN QUINCY TESTPATIENT", null, null, CreatedAt);

        Assert.Equal(RxLogFormatter.RedactedValue, safePayload.Source);
        Assert.Equal(RxLogFormatter.RedactedValue, safePayload.Entered);
        Assert.Equal(RxLogFormatter.RedactedValue, withheldPayload.Source);
        Assert.Equal(RxLogFormatter.RedactedValue, withheldPayload.Entered);
    }
}
