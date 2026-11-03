using RxVerifyOverlay.OrderAssist.Windows;
using Xunit;

namespace RxVerifyOverlay.Tests.OrderAssist;

/// <summary>Unit tests for AutoOrderModeRule -- edge-triggered automatic Order/Verify switching.</summary>
public class AutoOrderModeRuleTests
{
    private const bool Order = true;
    private const bool Verify = false;

    /// <summary>Feeds enough consecutive "absent" samples to confirm a disappearance and returns the final action.</summary>
    private static AutoOrderModeAction ConfirmAbsent(AutoOrderModeRule rule, bool orderMode, bool suspended = false)
    {
        var action = AutoOrderModeAction.None;
        for (var i = 0; i < AutoOrderModeRule.AbsentSamplesBeforeRevert; i++)
        {
            action = rule.Observe(false, orderMode, suspended);
        }
        return action;
    }

    [Fact]
    public void ScreenAppearingInVerifyModeSwitchesToOrder()
    {
        var rule = new AutoOrderModeRule();
        Assert.Equal(AutoOrderModeAction.SwitchToOrder, rule.Observe(true, Verify, suspended: false));
        Assert.True(rule.ScreenPresent);
    }

    [Fact]
    public void ScreenStayingUpDoesNotRepeatTheSwitch()
    {
        var rule = new AutoOrderModeRule();
        rule.Observe(true, Verify, false);
        Assert.Equal(AutoOrderModeAction.None, rule.Observe(true, Order, false));
        Assert.Equal(AutoOrderModeAction.None, rule.Observe(true, Order, false));
    }

    [Fact]
    public void ScreenClosingInOrderModeSwitchesBackToVerifyAfterDebounce()
    {
        var rule = new AutoOrderModeRule();
        rule.Observe(true, Verify, false);

        for (var i = 0; i < AutoOrderModeRule.AbsentSamplesBeforeRevert - 1; i++)
        {
            Assert.Equal(AutoOrderModeAction.None, rule.Observe(false, Order, false));
        }
        Assert.Equal(AutoOrderModeAction.SwitchToVerify, rule.Observe(false, Order, false));
        Assert.False(rule.ScreenPresent);
    }

    [Fact]
    public void BriefGapBetweenSiblingOrderWindowsDoesNotFlapTheMode()
    {
        var rule = new AutoOrderModeRule();
        rule.Observe(true, Verify, false);

        // One missed sample (Create Recommended Orders closing as the
        // Catalog dialog opens), then the screen is back.
        Assert.Equal(AutoOrderModeAction.None, rule.Observe(false, Order, false));
        Assert.Equal(AutoOrderModeAction.None, rule.Observe(true, Order, false));

        // The absence streak was reset: a later single gap still does nothing.
        Assert.Equal(AutoOrderModeAction.None, rule.Observe(false, Order, false));
    }

    [Fact]
    public void ManualVerifyPickWhileScreenIsUpIsNotFought()
    {
        var rule = new AutoOrderModeRule();
        rule.Observe(true, Verify, false); // auto -> Order
        // Pharmacist picks Verify on the order screen; screen stays up for many ticks.
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(AutoOrderModeAction.None, rule.Observe(true, Verify, false));
        }
        // Screen closes: already Verify, nothing to do.
        Assert.Equal(AutoOrderModeAction.None, ConfirmAbsent(rule, Verify));
    }

    [Fact]
    public void ManualOrderPickWithNoScreenIsNotFought()
    {
        var rule = new AutoOrderModeRule();
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(AutoOrderModeAction.None, rule.Observe(false, Order, false));
        }
    }

    [Fact]
    public void ManualOrderModeStaysOrderWhenAScreenOpensThenRevertsWhenItCloses()
    {
        var rule = new AutoOrderModeRule();
        Assert.Equal(AutoOrderModeAction.None, rule.Observe(true, Order, false)); // already Order: no switch
        Assert.Equal(AutoOrderModeAction.SwitchToVerify, ConfirmAbsent(rule, Order));
    }

    [Fact]
    public void ScreenAlreadyUpAtFirstSampleCountsAsAnAppearance()
    {
        var rule = new AutoOrderModeRule();
        Assert.Equal(AutoOrderModeAction.SwitchToOrder, rule.Observe(true, Verify, false));
    }

    [Fact]
    public void UnknownSampleChangesNothing()
    {
        var rule = new AutoOrderModeRule();
        Assert.Equal(AutoOrderModeAction.None, rule.Observe(null, Verify, false));
        Assert.False(rule.ScreenPresent);

        rule.Observe(true, Verify, false);
        Assert.Equal(AutoOrderModeAction.None, rule.Observe(null, Order, false));
        Assert.True(rule.ScreenPresent);
        // The failed sample did not count toward the absence streak.
        Assert.Equal(AutoOrderModeAction.None, rule.Observe(false, Order, false));
    }

    [Fact]
    public void SuspendedForReportsIgnoresSamplesCompletely()
    {
        var rule = new AutoOrderModeRule();
        Assert.Equal(AutoOrderModeAction.None, rule.Observe(true, Verify, suspended: true));
        Assert.False(rule.ScreenPresent); // nothing consumed
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(AutoOrderModeAction.None, rule.Observe(false, Order, suspended: true));
        }
    }

    [Fact]
    public void ScreenThatAppearedWhileSuspendedSwitchesToOrderOnceResumed()
    {
        // The screen is still up when Reports closes: it is the active screen, so Order.
        var rule = new AutoOrderModeRule();
        rule.Observe(true, Verify, suspended: true);
        Assert.Equal(AutoOrderModeAction.SwitchToOrder, rule.Observe(true, Verify, suspended: false));
    }

    [Fact]
    public void OrderScreenClosedWhileReportsWasOpenRevertsToVerifyAfterReportsCloses()
    {
        var rule = new AutoOrderModeRule();

        // Order screen opens -> auto Order.
        Assert.Equal(AutoOrderModeAction.SwitchToOrder, rule.Observe(true, Verify, suspended: false));
        Assert.True(rule.AutoSwitched);

        // Reports opens; the order screen closes during it. Nothing happens, and nothing is lost.
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(AutoOrderModeAction.None, rule.Observe(false, Order, suspended: true));
        }
        Assert.True(rule.ScreenPresent);

        // Reports closes: the falling edge is confirmed on resume and Verify is restored.
        Assert.Equal(AutoOrderModeAction.None, rule.Observe(false, Order, suspended: false));
        Assert.Equal(AutoOrderModeAction.SwitchToVerify, rule.Observe(false, Order, suspended: false));
        Assert.False(rule.AutoSwitched);

        // And it only happens once.
        Assert.Equal(AutoOrderModeAction.None, rule.Observe(false, Verify, suspended: false));
    }

    [Fact]
    public void ManualVerifyPickClearsTheAutoFlag()
    {
        var rule = new AutoOrderModeRule();
        rule.Observe(true, Verify, false);
        Assert.True(rule.AutoSwitched);
        rule.Observe(true, Verify, false); // pharmacist picked Verify while on the screen
        Assert.False(rule.AutoSwitched);
    }

    [Fact]
    public void EachNewAppearanceSwitchesAgain()
    {
        var rule = new AutoOrderModeRule();
        Assert.Equal(AutoOrderModeAction.SwitchToOrder, rule.Observe(true, Verify, false));
        Assert.Equal(AutoOrderModeAction.SwitchToVerify, ConfirmAbsent(rule, Order));
        Assert.Equal(AutoOrderModeAction.SwitchToOrder, rule.Observe(true, Verify, false));
    }
}
