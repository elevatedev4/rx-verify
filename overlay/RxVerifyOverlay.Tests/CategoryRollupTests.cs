using System.Collections.Generic;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.ViewModels;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for CategoryRollup (ViewModels/OverlayViewModel.cs) — the
/// worst-status-wins rule that turns a category's field-row statuses
/// into its single header status. Pure logic, no UIA/engine/PHI.
/// </summary>
public class CategoryRollupTests
{
    [Fact]
    public void AllGreenRollsUpToGreen()
    {
        var statuses = new[] { VerdictStatus.Green, VerdictStatus.Green, VerdictStatus.Green };
        Assert.Equal(VerdictStatus.Green, CategoryRollup.RollUp(statuses));
    }

    [Fact]
    public void AnySingleYellowAmongGreensRollsUpToYellow()
    {
        var statuses = new[] { VerdictStatus.Green, VerdictStatus.Yellow, VerdictStatus.Green };
        Assert.Equal(VerdictStatus.Yellow, CategoryRollup.RollUp(statuses));
    }

    [Fact]
    public void AnySingleRedAmongGreensAndYellowsRollsUpToRed()
    {
        var statuses = new[] { VerdictStatus.Green, VerdictStatus.Yellow, VerdictStatus.Red };
        Assert.Equal(VerdictStatus.Red, CategoryRollup.RollUp(statuses));
    }

    [Fact]
    public void RedBeatsYellowRegardlessOfOrder()
    {
        var redFirst = new[] { VerdictStatus.Red, VerdictStatus.Yellow };
        var yellowFirst = new[] { VerdictStatus.Yellow, VerdictStatus.Red };
        Assert.Equal(VerdictStatus.Red, CategoryRollup.RollUp(redFirst));
        Assert.Equal(VerdictStatus.Red, CategoryRollup.RollUp(yellowFirst));
    }

    [Fact]
    public void EmptyRollsUpToGreen()
    {
        Assert.Equal(VerdictStatus.Green, CategoryRollup.RollUp(new List<VerdictStatus>()));
    }

    [Fact]
    public void SingleRedRollsUpToRed()
    {
        Assert.Equal(VerdictStatus.Red, CategoryRollup.RollUp(new[] { VerdictStatus.Red }));
    }
}
