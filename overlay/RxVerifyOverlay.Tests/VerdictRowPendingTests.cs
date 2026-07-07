using RxVerifyOverlay.Models;
using RxVerifyOverlay.ViewModels;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for VerdictRowViewModel.IsPending (ViewModels/OverlayViewModel.cs)
/// — the flag MainWindow.xaml uses to swap the drug row's status glyph
/// for a spinner while its background lookup is still running (see
/// OverlayViewModel.RefreshAsync's two-phase refresh, added per Will's
/// live-test feedback about Refresh lag). Pure logic, no UIA/engine/PHI —
/// mirrors rx-verify src/engine/index.ts PENDING_DRUG_LOOKUP_REASON_CODE
/// via Models/EngineModels.cs ReasonCodes.PendingDrugLookup.
/// </summary>
public class VerdictRowPendingTests
{
    [Fact]
    public void IsPendingTrueOnlyWhenReasonCodeIsPendingDrugLookup()
    {
        var row = new VerdictRowViewModel
        {
            FieldKey = "drug",
            Status = VerdictStatus.Yellow,
            ReasonCode = ReasonCodes.PendingDrugLookup
        };

        Assert.True(row.IsPending);
    }

    [Fact]
    public void IsPendingFalseForARealYellowVerdict()
    {
        // A genuine not_provided/unverified yellow (e.g. from a field
        // the engine actually couldn't compare) must NOT be mistaken for
        // "still computing" — only the exact pending_lookup reason code
        // means that.
        var row = new VerdictRowViewModel
        {
            FieldKey = "drug",
            Status = VerdictStatus.Yellow,
            ReasonCode = "unknown_drug"
        };

        Assert.False(row.IsPending);
    }

    [Fact]
    public void IsPendingFalseForGreenOrRed()
    {
        var green = new VerdictRowViewModel { FieldKey = "drug", Status = VerdictStatus.Green, ReasonCode = "exact_match" };
        var red = new VerdictRowViewModel { FieldKey = "drug", Status = VerdictStatus.Red, ReasonCode = "drug_mismatch" };

        Assert.False(green.IsPending);
        Assert.False(red.IsPending);
    }
}
