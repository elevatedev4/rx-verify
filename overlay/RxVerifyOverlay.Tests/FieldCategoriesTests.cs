using System.Linq;
using RxVerifyOverlay.Models;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for the FieldCategories mapping (Models/EngineModels.cs)
/// that groups the 13 FieldOrder.Fields into the overlay's 3 compact-
/// table categories (Patient/Prescriber/Rx — sig now folded into Rx,
/// listed last alongside drug, both being inherently fuzzy matches).
/// Pure data checks — no UIA, no engine call, no synthetic PHI needed.
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
    public void RxCategoryContainsDrugQuantityRefillsWrittenDateDawAndSig()
    {
        // daysSupply intentionally absent -- removed entirely per Will's
        // live-test feedback (not in FieldOrder.Fields at all anymore).
        // sig is now folded INTO Rx (no longer its own category) per the
        // pharmacist owner's follow-up request -- drug and sig sit last
        // within Rx since both are inherently fuzzy matches.
        var rxFields = new[] { "dateWritten", "quantity", "refills", "daw", "drug", "sig" };
        foreach (var field in rxFields)
        {
            Assert.Equal(FieldCategories.Rx, FieldCategories.CategoryByField[field]);
        }
    }

    [Fact]
    public void DaysSupplyIsNotAFieldAtAll()
    {
        Assert.DoesNotContain("daysSupply", FieldOrder.Fields);
        Assert.False(FieldCategories.CategoryByField.ContainsKey("daysSupply"));
    }

    [Fact]
    public void CategoryOrderIsPatientThenPrescriberThenRx()
    {
        Assert.Equal(new[] { FieldCategories.Patient, FieldCategories.Prescriber, FieldCategories.Rx }, FieldCategories.Order);
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
