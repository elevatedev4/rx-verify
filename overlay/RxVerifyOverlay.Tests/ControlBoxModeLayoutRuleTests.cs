using RxVerifyOverlay.Integrated;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for ControlBoxModeLayoutRule — the fix for Will's live
/// test finding (W-T75, item 2): the compact Order-mode box must show
/// ONLY the Mode dropdown + Verify escape Button, nothing else.
/// </summary>
public class ControlBoxModeLayoutRuleTests
{
    [Fact]
    public void VerifyModeShowsOnlyNormalPanelAndCloseButton()
    {
        var layout = ControlBoxModeLayoutRule.Resolve(orderModeActive: false);

        Assert.True(layout.ShowNormalPanel);
        Assert.False(layout.ShowCompactOrderPanel);
        Assert.True(layout.ShowCloseButton);
        Assert.Equal(0, layout.ModeComboBoxSelectedIndex);
    }

    [Fact]
    public void OrderModeShowsOnlyTheCompactPanel()
    {
        var layout = ControlBoxModeLayoutRule.Resolve(orderModeActive: true);

        Assert.False(layout.ShowNormalPanel);
        Assert.True(layout.ShowCompactOrderPanel);
        Assert.False(layout.ShowCloseButton);
        Assert.Equal(1, layout.ModeComboBoxSelectedIndex);
    }

    [Fact]
    public void NormalPanelAndCompactPanelAreNeverBothVisible()
    {
        Assert.NotEqual(
            ControlBoxModeLayoutRule.Resolve(true).ShowNormalPanel,
            ControlBoxModeLayoutRule.Resolve(true).ShowCompactOrderPanel);

        Assert.NotEqual(
            ControlBoxModeLayoutRule.Resolve(false).ShowNormalPanel,
            ControlBoxModeLayoutRule.Resolve(false).ShowCompactOrderPanel);
    }
}
