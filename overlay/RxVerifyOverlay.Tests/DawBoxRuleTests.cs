using RxVerifyOverlay.Integrated;
using RxVerifyOverlay.Models;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for DawBoxRule (Integrated/DawBoxRule.cs) — the owner's
/// round-4 "don't draw a box around DAW unless it's actually in play"
/// truth table.
/// </summary>
public class DawBoxRuleTests
{
    private const string DawChecked = DawBoxRule.EnteredDawCheckedText;
    private const string DawNotChecked = "DAW not checked";
    private const string SubstitutionAllowed = "Substitution allowed";
    private const string SubstitutionNotAllowed = DawBoxRule.SourceSubstitutionNotAllowedText;

    [Fact]
    public void AllowedAndUncheckedDrawsNoBox()
    {
        // Source allows substitution, DAW was never checked — never a
        // decision point for this Rx, pure noise to box it.
        Assert.False(DawBoxRule.ShouldDrawBox(VerdictStatus.Green, DawNotChecked, SubstitutionAllowed));
    }

    [Fact]
    public void AllowedAndCheckedDrawsABox()
    {
        // Source allows substitution (compareDaw still returns green/
        // substitution_allowed here, same reasonCode as the unchecked
        // case above), but the pharmacist voluntarily checked DAW anyway
        // — that's a real, deliberate choice worth confirming.
        Assert.True(DawBoxRule.ShouldDrawBox(VerdictStatus.Green, DawChecked, SubstitutionAllowed));
    }

    [Fact]
    public void DisallowedSourceWithDawCheckedDrawsAGreenBoxNormally()
    {
        // Source disallows substitution AND DAW is checked — consistent,
        // green, but definitely "in play" — show it.
        Assert.True(DawBoxRule.ShouldDrawBox(VerdictStatus.Green, DawChecked, SubstitutionNotAllowed));
    }

    [Fact]
    public void DisallowedSourceWithDawNotCheckedDrawsARedBox()
    {
        // Source disallows substitution but DAW isn't checked — a real
        // mismatch (daw_required, red) — always shown regardless of the
        // "in play" question since status isn't Green.
        Assert.True(DawBoxRule.ShouldDrawBox(VerdictStatus.Red, DawNotChecked, SubstitutionNotAllowed));
    }

    [Theory]
    [InlineData(VerdictStatus.Yellow)]
    [InlineData(VerdictStatus.Red)]
    public void AnyNonGreenVerdictAlwaysDrawsABoxRegardlessOfValues(VerdictStatus status)
    {
        Assert.True(DawBoxRule.ShouldDrawBox(status, DawNotChecked, SubstitutionAllowed));
    }

    [Fact]
    public void MissingEnteredOrSourceValuesNeverForceANoBoxDecision()
    {
        // "(not provided)" on either side (not_provided, yellow) — status
        // isn't Green, so a box is drawn regardless of the in-play check.
        Assert.True(DawBoxRule.ShouldDrawBox(VerdictStatus.Yellow, "(not provided)", "(not provided)"));
    }
}
