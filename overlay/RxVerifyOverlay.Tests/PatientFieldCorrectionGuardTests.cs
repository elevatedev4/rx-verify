using RxVerifyOverlay.Reporting;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for PatientFieldCorrectionGuard (Reporting/PatientFieldCorrectionGuard.cs)
/// — the pure containment check behind the 2026-08-19 policy change (Will
/// verbatim: "it won't let me type anything in the box... Fix that")
/// allowing patient-field report corrections to be typed and transmitted,
/// UNLESS they leak the field's own real captured value. All names/
/// addresses/DOBs below are synthetic placeholders, never real patient
/// data.
/// </summary>
public class PatientFieldCorrectionGuardTests
{
    [Fact]
    public void TripsOnTheExactSourceValue()
    {
        var tripped = PatientFieldCorrectionGuard.ContainsPatientValueFragment(
            "should be JORDAN QUINCY TESTPATIENT not the entered version",
            sourceValue: "JORDAN QUINCY TESTPATIENT",
            enteredValue: "JORDAN Q TESTPATIENT");

        Assert.True(tripped);
    }

    [Fact]
    public void TripsOnTheExactEnteredValue()
    {
        var tripped = PatientFieldCorrectionGuard.ContainsPatientValueFragment(
            "the box actually shows JORDAN Q TESTPATIENT",
            sourceValue: "JORDAN QUINCY TESTPATIENT",
            enteredValue: "JORDAN Q TESTPATIENT");

        Assert.True(tripped);
    }

    [Fact]
    public void TripsOnJustAFragmentOfTheEnteredValue()
    {
        // Only a PIECE of the entered value ("main street") appears — the
        // >= 5 contiguous character rule is deliberately generous, not
        // requiring an exact/full match.
        var tripped = PatientFieldCorrectionGuard.ContainsPatientValueFragment(
            "entered says main street apt but should be different",
            sourceValue: "123 MAIN STREET APT 4",
            enteredValue: "123 MAIN ST APT 4");

        Assert.True(tripped);
    }

    [Fact]
    public void TripsWhenTheSameWordsAreRearranged()
    {
        // Same words as the source value, reordered — each word's own
        // internal character run still matches regardless of overall
        // word order, since the check is a plain substring scan, not an
        // exact/whole-string comparison.
        var tripped = PatientFieldCorrectionGuard.ContainsPatientValueFragment(
            "QUINCY JORDAN TESTPATIENT is the correct order",
            sourceValue: "TESTPATIENT JORDAN QUINCY",
            enteredValue: "JORDAN Q TESTPATIENT");

        Assert.True(tripped);
    }

    [Fact]
    public void TripsThroughReformattingLikeDashesAndCasing()
    {
        // Normalization strips punctuation/spaces and lowercases both
        // sides, so trivial reformatting can't dodge the check.
        var tripped = PatientFieldCorrectionGuard.ContainsPatientValueFragment(
            "should be jordan-quincy testpatient",
            sourceValue: "JORDAN QUINCY TESTPATIENT",
            enteredValue: "JORDAN Q TESTPATIENT");

        Assert.True(tripped);
    }

    [Fact]
    public void SafeDescriptiveTextDoesNotTrip()
    {
        var tripped = PatientFieldCorrectionGuard.ContainsPatientValueFragment(
            "the two spellings do not match, please verify against the script",
            sourceValue: "JORDAN QUINCY TESTPATIENT",
            enteredValue: "JORDAN Q TESTPATIENT");

        Assert.False(tripped);
    }

    [Fact]
    public void EmptyCorrectionNeverTrips()
    {
        // Nothing typed means nothing to leak — the withheld stand-in is
        // reserved for text that actually contains the patient's data.
        Assert.False(PatientFieldCorrectionGuard.ContainsPatientValueFragment("", "SYNTHETIC-SOURCE", "SYNTHETIC-ENTERED"));
    }

    [Fact]
    public void NullCorrectionNeverTrips()
    {
        Assert.False(PatientFieldCorrectionGuard.ContainsPatientValueFragment(null, "SYNTHETIC-SOURCE", "SYNTHETIC-ENTERED"));
    }

    [Fact]
    public void ShortCapturedValueBelowTheFloorNeverTripsEvenIfTypedVerbatim()
    {
        // A captured value shorter than MinContiguousMatchLength (5) can
        // never form a matching window — see the class's own doc for why
        // this is an accepted, documented limitation (the on-screen
        // warning is the primary defense; this is a secondary backstop).
        var tripped = PatientFieldCorrectionGuard.ContainsPatientValueFragment(
            "patient is Xu, not someone else, please review the name",
            sourceValue: "Xu",
            enteredValue: "Xu");

        Assert.False(tripped);
    }

    [Fact]
    public void UnrelatedShortValuesNeverTrip()
    {
        Assert.False(PatientFieldCorrectionGuard.ContainsPatientValueFragment("quantity should be 90 not 60", "60", "90"));
    }

    [Fact]
    public void NullSourceAndEnteredNeverTrip()
    {
        Assert.False(PatientFieldCorrectionGuard.ContainsPatientValueFragment("some long descriptive correction text here", null, null));
    }

    [Fact]
    public void MinContiguousMatchLengthConstantIsFive()
    {
        // Pins the actual threshold so a future accidental tuning change
        // shows up as a failing test, not silent drift.
        Assert.Equal(5, PatientFieldCorrectionGuard.MinContiguousMatchLength);
    }
}
