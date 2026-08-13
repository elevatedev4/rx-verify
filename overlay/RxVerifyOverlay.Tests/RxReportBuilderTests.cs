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
}
