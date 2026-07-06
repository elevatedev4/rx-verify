using System.Linq;
using RxVerifyOverlay.Models;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for the FieldCategories mapping (Models/EngineModels.cs)
/// that groups the 10 FieldOrder.Fields into the overlay's 3 compact-
/// table categories (Patient/Prescriber/Rx). Pure data checks — no UIA,
/// no engine call, no synthetic PHI needed.
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
    public void PrescriberCategoryIsASingleBundledField()
    {
        Assert.Equal(FieldCategories.Prescriber, FieldCategories.CategoryByField["prescriber"]);
    }

    [Fact]
    public void RxCategoryContainsDrugSigQuantityDaysSupplyRefillsAndWrittenDate()
    {
        var rxFields = new[] { "dateWritten", "drug", "sig", "quantity", "daysSupply", "refills" };
        foreach (var field in rxFields)
        {
            Assert.Equal(FieldCategories.Rx, FieldCategories.CategoryByField[field]);
        }
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
