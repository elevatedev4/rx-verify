using RxVerifyOverlay.Models;
using RxVerifyOverlay.Uia;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for FieldMap.EnteredAutomationIdByField (Uia/FieldMap.cs) —
/// the mapping INTEGRATED DISPLAY MODE (Integrated/
/// IntegratedOverlayCoordinator.cs) relies on to capture each entered
/// field's on-screen rect via FieldReader.ReadEnteredFieldRects(). If this
/// mapping ever drifted out of sync with Models.FieldOrder.Fields (e.g. a
/// new field added to one but not the other), a verdict row would either
/// have no box to draw (silently, no crash) or the reverse — this test
/// exists so that drift is caught immediately in CI instead of silently
/// at runtime.
/// </summary>
public class FieldMapEnteredRectMappingTests
{
    [Fact]
    public void HasExactlyOneEntryPerFieldOrderField()
    {
        foreach (var field in FieldOrder.Fields)
        {
            Assert.True(FieldMap.EnteredAutomationIdByField.ContainsKey(field),
                $"FieldMap.EnteredAutomationIdByField is missing an entry for FieldOrder field '{field}'.");
        }

        Assert.Equal(FieldOrder.Fields.Count, FieldMap.EnteredAutomationIdByField.Count);
    }

    [Fact]
    public void EveryMappedAutomationIdIsNonEmpty()
    {
        foreach (var (field, automationId) in FieldMap.EnteredAutomationIdByField)
        {
            Assert.False(string.IsNullOrWhiteSpace(automationId), $"Field '{field}' maps to a blank AutomationId.");
        }
    }

    [Theory]
    [InlineData("patientName", FieldMap.EnteredPatientQuickSearchId)]
    [InlineData("patientDOB", FieldMap.EnteredPatientDobId)]
    [InlineData("patientAddress", FieldMap.EnteredPatientAddressId)]
    [InlineData("prescriberName", FieldMap.EnteredPrescriberQuickSearchId)]
    [InlineData("prescriberNpi", FieldMap.EnteredPrescriberNpiId)]
    [InlineData("prescriberPhone", FieldMap.EnteredPrescriberPhoneId)]
    [InlineData("prescriberAddress", FieldMap.EnteredPrescriberAddressId)]
    [InlineData("dateWritten", FieldMap.EnteredWrittenDateId)]
    [InlineData("quantity", FieldMap.EnteredQuantityId)]
    [InlineData("refills", FieldMap.EnteredRefillsId)]
    [InlineData("daw", FieldMap.EnteredDawId)]
    [InlineData("drug", FieldMap.EnteredItemQuickSearchId)]
    [InlineData("sig", FieldMap.EnteredDirectionsId)]
    public void MapsEachFieldToTheSameAutomationIdReadEnteredUses(string field, string expectedAutomationId)
    {
        // Deliberately the SAME AutomationId constants FieldReader.
        // ReadEntered() reads each field's VALUE from — this mapping must
        // never independently drift from that (see FieldMap.cs's doc on
        // EnteredAutomationIdByField).
        Assert.True(FieldMap.EnteredAutomationIdByField.TryGetValue(field, out var actual));
        Assert.Equal(expectedAutomationId, actual);
    }
}
