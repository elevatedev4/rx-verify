using System.Collections.Generic;
using RxVerifyOverlay.Diagnostics;
using RxVerifyOverlay.Uia;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for AutoDiagnosticNoteBuilder (Diagnostics/
/// AutoDiagnosticNoteBuilder.cs) — the free-text Correction note attached
/// to an automatic refills diagnostic report. All values below are
/// synthetic.
/// </summary>
public class AutoDiagnosticNoteBuilderTests
{
    [Fact]
    public void StartsWithTheFixedAutoDiagnosticPrefix()
    {
        var note = AutoDiagnosticNoteBuilder.Build(
            RefillsBoxRenderState.NotProvided, "(not provided)", "no-value-paired",
            RxScreenMode.PreCheck, "unknown", "abc1234", null);

        Assert.StartsWith("AUTO-DIAGNOSTIC refills unassessed — ", note);
    }

    [Fact]
    public void IncludesTheRenderStateMissReasonScreenModeAndDocumentClassification()
    {
        var note = AutoDiagnosticNoteBuilder.Build(
            RefillsBoxRenderState.UnparseableQuantity, "garbled", "validation-failed:not-numeric",
            RxScreenMode.EditRx, "refill approval", "abc1234", null);

        Assert.Contains("boxState=UnparseableQuantity", note);
        Assert.Contains("missReason=validation-failed:not-numeric", note);
        Assert.Contains("screenMode=EditRx", note);
        Assert.Contains("document=refill approval", note);
        Assert.Contains("commit=abc1234", note);
    }

    [Fact]
    public void FallsBackToPlaceholderTextForMissingOptionalValues()
    {
        var note = AutoDiagnosticNoteBuilder.Build(
            RefillsBoxRenderState.NotOnScreen, null, null,
            RxScreenMode.Unknown, "unknown", null, null);

        Assert.Contains("engineRefillsValue=undefined", note);
        Assert.Contains("missReason=none", note);
        Assert.Contains("commit=unknown", note);
        Assert.Contains("nearbyOcrWords=[(none captured)]", note);
    }

    [Fact]
    public void JoinsRefillsOcrRegionWordsSpaceSeparated()
    {
        var words = new List<string> { "Fulfillment", "status", "pending" };

        var note = AutoDiagnosticNoteBuilder.Build(
            RefillsBoxRenderState.NotProvided, "(not provided)", "no-value-paired",
            RxScreenMode.PreCheck, "unknown", "abc1234", words);

        Assert.Contains("nearbyOcrWords=[Fulfillment status pending]", note);
    }

    [Fact]
    public void NeverIncludesAnythingBeyondTheGivenSynthenticDiagnosticFields()
    {
        // PHI-safety smoke check: nothing but the documented fields ever
        // appears — no patient/prescriber name field exists as an input
        // to this builder at all, so there's nothing for it to leak.
        var words = new List<string> { "Fulfillment" };
        var note = AutoDiagnosticNoteBuilder.Build(
            RefillsBoxRenderState.NotProvided, "3", "no-value-paired",
            RxScreenMode.NewRx, "new Rx", "deadbee", words);

        Assert.Equal(
            "AUTO-DIAGNOSTIC refills unassessed — boxState=NotProvided; engineRefillsValue=3; missReason=no-value-paired; screenMode=NewRx; document=new Rx; commit=deadbee; nearbyOcrWords=[Fulfillment]",
            note);
    }
}
