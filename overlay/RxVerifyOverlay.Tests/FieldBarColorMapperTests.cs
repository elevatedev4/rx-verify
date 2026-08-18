using RxVerifyOverlay.Models;
using RxVerifyOverlay.ViewModels;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for FieldBarColorMapper (ViewModels/FieldBarColorMapper.cs)
/// and VerdictRowViewModel.BarColor — the field-bar color split added per
/// Will's live-test feedback (2026-08-17, verbatim: "Make the field bar
/// yellow if it was not able to be read, red if it's wrong", corrected to
/// "Actually make it gray if it wasn't able to be read"). Green/Red pass
/// straight through from VerdictStatus; only Yellow is split further by
/// ReasonCode into GRAY (couldn't be read/compared at all) vs. YELLOW
/// (read both sides, engine wants a human glance).
/// </summary>
public class FieldBarColorMapperTests
{
    [Fact]
    public void GreenStatusIsAlwaysAGreenBar()
    {
        Assert.Equal(FieldBarColor.Green, FieldBarColorMapper.Classify(VerdictStatus.Green, "exact_match"));
    }

    [Fact]
    public void RedStatusIsAlwaysARedBarRegardlessOfReasonCode()
    {
        Assert.Equal(FieldBarColor.Red, FieldBarColorMapper.Classify(VerdictStatus.Red, "date_mismatch"));
        Assert.Equal(FieldBarColor.Red, FieldBarColorMapper.Classify(VerdictStatus.Red, "drug_mismatch"));
        Assert.Equal(FieldBarColor.Red, FieldBarColorMapper.Classify(VerdictStatus.Red, "surname_mismatch"));
    }

    // GRAY bucket — the field genuinely could not be read/compared: either
    // side was empty ("not_provided", shared across every comparator) or
    // present text couldn't be parsed into a comparable value.
    [Theory]
    [InlineData("not_provided")]
    [InlineData("unparseable_date")]
    [InlineData("unparseable_quantity")]
    public void UnreadableReasonCodesRenderGray(string reasonCode)
    {
        Assert.Equal(FieldBarColor.Gray, FieldBarColorMapper.Classify(VerdictStatus.Yellow, reasonCode));
    }

    // YELLOW (stays) bucket — both sides WERE read into real values; the
    // engine is deliberately flagging the comparison for a human glance,
    // not reporting an absence. Covers every non-not_provided yellow
    // reasonCode this engine currently emits (src/normalize/name.ts,
    // src/normalize/address.ts, src/quantity/index.ts, src/sig/index.ts,
    // src/drug/index.ts).
    [Theory]
    [InlineData("suffix_dropped")]
    [InlineData("middle_name_present")]
    [InlineData("surname_partial")]
    [InlineData("given_name_partial")]
    [InlineData("nickname_match")]
    [InlineData("address_differs")]
    [InlineData("unit_differs")]
    [InlineData("phone_differs")]
    [InlineData("phone_ocr_suspect")]
    [InlineData("quantity_adjusted")]
    [InlineData("sig_ambiguous")]
    [InlineData("unknown_drug")]
    [InlineData("pack_size")]
    [InlineData("strength_unverified")]
    [InlineData("generic_substitution")]
    public void ReadButUncertainReasonCodesStayYellow(string reasonCode)
    {
        Assert.Equal(FieldBarColor.Yellow, FieldBarColorMapper.Classify(VerdictStatus.Yellow, reasonCode));
    }

    [Fact]
    public void PendingDrugLookupPlaceholderStaysYellowNotGray()
    {
        // Transient "still computing" state (see VerdictRowViewModel.IsPending)
        // — must never render as unreadable-gray; it will be replaced by a
        // real verdict within the same refresh.
        Assert.Equal(FieldBarColor.Yellow, FieldBarColorMapper.Classify(VerdictStatus.Yellow, ReasonCodes.PendingDrugLookup));
    }

    [Fact]
    public void NullReasonCodeOnYellowFallsBackToYellowNotGray()
    {
        // Defensive default: absence of a reasonCode string is not itself
        // proof the field was unreadable — only the known unreadable codes
        // are gray, everything else (including unset/unknown) stays yellow.
        Assert.Equal(FieldBarColor.Yellow, FieldBarColorMapper.Classify(VerdictStatus.Yellow, null));
    }

    [Fact]
    public void VerdictRowViewModelBarColorMatchesTheMapper()
    {
        var grayRow = new VerdictRowViewModel { FieldKey = "quantity", Status = VerdictStatus.Yellow, ReasonCode = "not_provided" };
        var yellowRow = new VerdictRowViewModel { FieldKey = "patientName", Status = VerdictStatus.Yellow, ReasonCode = "nickname_match" };
        var greenRow = new VerdictRowViewModel { FieldKey = "drug", Status = VerdictStatus.Green, ReasonCode = "exact_match" };
        var redRow = new VerdictRowViewModel { FieldKey = "sig", Status = VerdictStatus.Red, ReasonCode = "sig_mismatch" };

        Assert.Equal(FieldBarColor.Gray, grayRow.BarColor);
        Assert.Equal(FieldBarColor.Yellow, yellowRow.BarColor);
        Assert.Equal(FieldBarColor.Green, greenRow.BarColor);
        Assert.Equal(FieldBarColor.Red, redRow.BarColor);
    }
}
