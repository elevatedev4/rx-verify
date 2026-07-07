using System.Linq;
using RxVerifyOverlay.Models;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for the FieldCategories mapping (Models/EngineModels.cs)
/// that groups the 13 FieldOrder.Fields into the overlay's 4 compact-
/// table categories (Patient/Prescriber/Rx/Sig — sig split out of Rx and
/// into its own last-listed category per W-T9 item 6). Pure data checks
/// — no UIA, no engine call, no synthetic PHI needed.
/// </summary>
public class FieldCategoriesTests
{
    [Fact]
    public void EveryFieldOrderFieldHasACategory()
    {
        foreach (var field in FieldOrder.Fields)
        {
            Assert.True(
                FieldCategories.CategoryByField.ContainsKey(field),
                $"'{field}' is in FieldOrder.Fields but has no FieldCategories mapping — it would silently vanish from the compact table.");
        }
    }

    [Fact]
    public void PatientCategoryContainsNameDobAndAddress()
    {
        Assert.Equal(FieldCategories.Patient, FieldCategories.CategoryByField["patientName"]);
        Assert.Equal(FieldCategories.Patient, FieldCategories.CategoryByField["patientDOB"]);
        Assert.Equal(FieldCategories.Patient, FieldCategories.CategoryByField["patientAddress"]);
    }

    [Fact]
    public void PrescriberCategoryContainsAllFourSplitFields()
    {
        var prescriberFields = new[] { "prescriberName", "prescriberNpi", "prescriberPhone", "prescriberAddress" };
        foreach (var field in prescriberFields)
        {
            Assert.Equal(FieldCategories.Prescriber, FieldCategories.CategoryByField[field]);
        }
    }

    [Fact]
    public void RxCategoryContainsDrugQuantityRefillsAndWrittenDateButNotSig()
    {
        // daysSupply intentionally absent -- removed entirely per Will's
        // live-test feedback (not in FieldOrder.Fields at all anymore).
        // sig intentionally absent -- split into its own Sig category
        // per W-T9 item 6, so sig's fuzzy match variance never drags the
        // Rx category rollup to red (see FieldCategories.CategoryByField
        // doc).
        var rxFields = new[] { "dateWritten", "drug", "quantity", "refills" };
        foreach (var field in rxFields)
        {
            Assert.Equal(FieldCategories.Rx, FieldCategories.CategoryByField[field]);
        }

        Assert.NotEqual(FieldCategories.Rx, FieldCategories.CategoryByField["sig"]);
    }

    [Fact]
    public void SigHasItsOwnCategoryAndIsListedLast()
    {
        Assert.Equal(FieldCategories.Sig, FieldCategories.CategoryByField["sig"]);
        Assert.Equal(FieldCategories.Sig, FieldCategories.Order[^1]);
    }

    [Fact]
    public void DaysSupplyIsNotAFieldAtAll()
    {
        Assert.DoesNotContain("daysSupply", FieldOrder.Fields);
        Assert.False(FieldCategories.CategoryByField.ContainsKey("daysSupply"));
    }

    [Fact]
    public void CategoryOrderIsPatientThenPrescriberThenRxThenSig()
    {
        Assert.Equal(new[] { FieldCategories.Patient, FieldCategories.Prescriber, FieldCategories.Rx, FieldCategories.Sig }, FieldCategories.Order);
    }

    [Fact]
    public void EveryFieldOrderFieldMapsToAKnownCategoryName()
    {
        var knownCategories = FieldCategories.Order.ToHashSet();
        foreach (var field in FieldOrder.Fields)
        {
            var category = FieldCategories.CategoryByField[field];
            Assert.Contains(category, knownCategories);
        }
    }
}
