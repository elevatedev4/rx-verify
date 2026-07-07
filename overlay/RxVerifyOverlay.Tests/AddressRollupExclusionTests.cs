using System.Linq;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.ViewModels;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for the W-T10 item 2 requirement: an address difference
/// alone must NEVER move the Patient/Prescriber category status to
/// yellow or red. The mechanism (see Models/EngineModels.cs
/// FieldCategories.RollupExcludedFields and ViewModels/OverlayViewModel.cs
/// PopulateRows/RollUpCategory) filters patientAddress/prescriberAddress
/// rows OUT of the rollup input before calling CategoryRollup.RollUp,
/// while still leaving them in the category's Rows for display. These
/// tests exercise that exact filter + the same public CategoryRollup
/// helper the production code calls, so they fail if either the
/// exclusion set or the rollup rule drifts. Pure data/logic — no UIA,
/// no engine call, no PHI.
/// </summary>
public class AddressRollupExclusionTests
{
    [Fact]
    public void PatientAddressAndPrescriberAddressAreTheOnlyRollupExcludedFields()
    {
        Assert.Equal(
            new[] { "patientAddress", "prescriberAddress" }.OrderBy(f => f),
            FieldCategories.RollupExcludedFields.OrderBy(f => f));
    }

    [Fact]
    public void RedAddressAloneDoesNotDragCategoryRollupToRed()
    {
        var rowStatuses = new (string FieldKey, VerdictStatus Status)[]
        {
            ("patientName", VerdictStatus.Green),
            ("patientDOB", VerdictStatus.Green),
            ("patientAddress", VerdictStatus.Red) // e.g. a genuinely different street — still just informational
        };

        var rollupInput = rowStatuses
            .Where(r => !FieldCategories.RollupExcludedFields.Contains(r.FieldKey))
            .Select(r => r.Status);

        Assert.Equal(VerdictStatus.Green, CategoryRollup.RollUp(rollupInput));
    }

    [Fact]
    public void YellowAddressAloneDoesNotDragCategoryRollupToYellow()
    {
        var rowStatuses = new (string FieldKey, VerdictStatus Status)[]
        {
            ("prescriberName", VerdictStatus.Green),
            ("prescriberNpi", VerdictStatus.Green),
            ("prescriberPhone", VerdictStatus.Green),
            ("prescriberAddress", VerdictStatus.Yellow) // unit_differs / address_differs — still informational only
        };

        var rollupInput = rowStatuses
            .Where(r => !FieldCategories.RollupExcludedFields.Contains(r.FieldKey))
            .Select(r => r.Status);

        Assert.Equal(VerdictStatus.Green, CategoryRollup.RollUp(rollupInput));
    }

    [Fact]
    public void ANonAddressRedStillDragsTheCategoryToRedEvenWithAGreenAddress()
    {
        // Sanity check the exclusion is scoped to address only -- a real
        // mismatch elsewhere in the category must still surface.
        var rowStatuses = new (string FieldKey, VerdictStatus Status)[]
        {
            ("patientName", VerdictStatus.Red),
            ("patientDOB", VerdictStatus.Green),
            ("patientAddress", VerdictStatus.Green)
        };

        var rollupInput = rowStatuses
            .Where(r => !FieldCategories.RollupExcludedFields.Contains(r.FieldKey))
            .Select(r => r.Status);

        Assert.Equal(VerdictStatus.Red, CategoryRollup.RollUp(rollupInput));
    }
}
