using RxVerifyOverlay.OrderAssist.Windows;
using Xunit;

namespace RxVerifyOverlay.Tests.OrderAssist;

/// <summary>OrderScreenForegroundRule: presence is judged from the FOREGROUND window. Synthetic titles only.</summary>
public class OrderScreenForegroundRuleTests
{
    [Theory]
    [InlineData("Create Recommended Orders", null)]
    [InlineData("Recommended Order - Catalog Item Substitution Selection", null)]
    [InlineData("", "Create Recommended Orders")]   // title-less child dialog owned by the order window
    [InlineData(null, "Create Recommended Orders")]
    [InlineData("Confirm", "Recommended Order - Catalog Item Substitution Selection")]
    public void PioneerForegroundOrderWindowOrItsOwnerIsPresent(string? foregroundTitle, string? rootOwnerTitle)
    {
        Assert.True(OrderScreenForegroundRule.Evaluate(ForegroundOwner.PioneerRx, foregroundTitle, rootOwnerTitle));
    }

    [Theory]
    [InlineData("Pre-Check Rx - 1234567 - Test, Patient", null)]
    [InlineData("PioneerRx", null)]
    [InlineData(null, null)]
    [InlineData("Confirm", "PioneerRx")]
    public void PioneerForegroundOtherWindowMeansNotOnAnOrderScreen(string? foregroundTitle, string? rootOwnerTitle)
    {
        // A "Recommended Order..." window left open BEHIND the main window does not count.
        Assert.False(OrderScreenForegroundRule.Evaluate(ForegroundOwner.PioneerRx, foregroundTitle, rootOwnerTitle));
    }

    [Theory]
    [InlineData(ForegroundOwner.OverlayProcess)]
    [InlineData(ForegroundOwner.OtherApp)]
    [InlineData(ForegroundOwner.Unknown)]
    public void NonPioneerForegroundIsNoInformationEvenIfTheTitleLooksLikeAnOrderScreen(ForegroundOwner owner)
    {
        Assert.Null(OrderScreenForegroundRule.Evaluate(owner, "Create Recommended Orders", "Recommended Order"));
    }

    [Fact]
    public void ClickingTheOverlayOrAnotherAppNeverFlipsTheMode()
    {
        var rule = new AutoOrderModeRule();
        Assert.Equal(AutoOrderModeAction.SwitchToOrder, rule.Observe(OrderScreenForegroundRule.Evaluate(ForegroundOwner.PioneerRx, "Create Recommended Orders", null), false, false));

        // User clicks the control box (our process) many times, then a browser.
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(AutoOrderModeAction.None, rule.Observe(OrderScreenForegroundRule.Evaluate(ForegroundOwner.OverlayProcess, "x", null), true, false));
            Assert.Equal(AutoOrderModeAction.None, rule.Observe(OrderScreenForegroundRule.Evaluate(ForegroundOwner.OtherApp, "x", null), true, false));
        }
        Assert.True(rule.ScreenPresent);
    }

    [Fact]
    public void MainWindowComingForwardRevertsToVerifyAfterTheDebounce()
    {
        var rule = new AutoOrderModeRule();
        rule.Observe(OrderScreenForegroundRule.Evaluate(ForegroundOwner.PioneerRx, "Create Recommended Orders", null), false, false);

        var main = OrderScreenForegroundRule.Evaluate(ForegroundOwner.PioneerRx, "Pre-Check Rx - 1 - Test, Patient", null);
        Assert.Equal(AutoOrderModeAction.None, rule.Observe(main, true, false));
        Assert.Equal(AutoOrderModeAction.SwitchToVerify, rule.Observe(main, true, false));
    }
}
