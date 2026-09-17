using System.Collections.Generic;
using RxVerifyOverlay.Reports;
using Xunit;

namespace RxVerifyOverlay.Tests.Reports;

/// <summary>
/// Unit tests for Reports/SelectAllToggle.cs — ReportsWindow's "Select
/// all"/"Unselect all" button logic. No WPF involved; rows are plain
/// (IsEnabled, IsSelected) tuples.
/// </summary>
public class SelectAllToggleTests
{
    private static List<(bool IsEnabled, bool IsSelected)> Rows(params (bool IsEnabled, bool IsSelected)[] rows) =>
        new(rows);

    [Fact]
    public void AreAllEnabledSelected_TrueWhenEveryEnabledRowIsSelected()
    {
        var rows = Rows((true, true), (true, true), (false, false));

        Assert.True(SelectAllToggle.AreAllEnabledSelected(rows));
    }

    [Fact]
    public void AreAllEnabledSelected_FalseWhenAnEnabledRowIsNotSelected()
    {
        var rows = Rows((true, true), (true, false));

        Assert.False(SelectAllToggle.AreAllEnabledSelected(rows));
    }

    [Fact]
    public void AreAllEnabledSelected_IgnoresDisabledRowsEitherWay()
    {
        var allSelectedIgnoringDisabled = Rows((true, true), (false, false));
        var oneDisabledButSelectedAnyway = Rows((true, true), (false, true));

        Assert.True(SelectAllToggle.AreAllEnabledSelected(allSelectedIgnoringDisabled));
        Assert.True(SelectAllToggle.AreAllEnabledSelected(oneDisabledButSelectedAnyway));
    }

    [Fact]
    public void AreAllEnabledSelected_FalseWhenThereAreNoEnabledRowsAtAll()
    {
        var rows = Rows((false, false), (false, true));

        Assert.False(SelectAllToggle.AreAllEnabledSelected(rows));
    }

    [Fact]
    public void AreAllEnabledSelected_FalseForAnEmptyList()
    {
        Assert.False(SelectAllToggle.AreAllEnabledSelected(new List<(bool, bool)>()));
    }

    [Fact]
    public void LabelFor_IsUnselectAllWhenEveryEnabledRowIsSelected()
    {
        var rows = Rows((true, true), (true, true));

        Assert.Equal(SelectAllToggle.UnselectAllLabel, SelectAllToggle.LabelFor(rows));
    }

    [Fact]
    public void LabelFor_IsSelectAllWhenAtLeastOneEnabledRowIsNotSelected()
    {
        var rows = Rows((true, true), (true, false));

        Assert.Equal(SelectAllToggle.SelectAllLabel, SelectAllToggle.LabelFor(rows));
    }

    [Fact]
    public void NextSelectedState_IsFalseWhenEverythingIsAlreadySelected()
    {
        var rows = Rows((true, true), (true, true));

        Assert.False(SelectAllToggle.NextSelectedState(rows));
    }

    [Fact]
    public void NextSelectedState_IsTrueWhenNotEverythingIsSelectedYet()
    {
        var rows = Rows((true, true), (true, false));

        Assert.True(SelectAllToggle.NextSelectedState(rows));
    }

    [Fact]
    public void NextSelectedState_IsTrueWhenNothingIsSelected()
    {
        var rows = Rows((true, false), (true, false));

        Assert.True(SelectAllToggle.NextSelectedState(rows));
    }
}
