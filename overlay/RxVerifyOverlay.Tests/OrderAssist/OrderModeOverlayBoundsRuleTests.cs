using RxVerifyOverlay.OrderAssist.Geometry;
using Xunit;

namespace RxVerifyOverlay.Tests.OrderAssist;

/// <summary>
/// Unit tests for OrderModeOverlayBoundsRule — the pure arithmetic behind
/// the "overlay on order mode is covering some buttons ... doesn't cover
/// the New button" fix (owner, 2026-08-17). See
/// OrderAssistCoordinator.OrderModeBottomInsetDip's own doc for the full
/// context (the highlight window is otherwise repositioned to exactly the
/// target Pioneer window's own bounds, right down to where its own action
/// buttons live).
/// </summary>
public class OrderModeOverlayBoundsRuleTests
{
    [Fact]
    public void TrimsTheInsetConvertedToPhysicalPixelsAtOneToOneScale()
    {
        var height = OrderModeOverlayBoundsRule.TrimmedHeightPhysical(targetHeightPhysical: 600, bottomInsetDip: 48, scale: 1.0);

        Assert.Equal(552, height);
    }

    [Fact]
    public void ScalesTheInsetWithDpiBeforeSubtracting()
    {
        // 150% DPI (a common real-world scale) -- 48 DIP becomes 72 physical pixels.
        var height = OrderModeOverlayBoundsRule.TrimmedHeightPhysical(targetHeightPhysical: 600, bottomInsetDip: 48, scale: 1.5);

        Assert.Equal(528, height);
    }

    [Fact]
    public void FloorsAtZeroWhenTheInsetExceedsTheTargetHeight()
    {
        var height = OrderModeOverlayBoundsRule.TrimmedHeightPhysical(targetHeightPhysical: 30, bottomInsetDip: 48, scale: 1.0);

        Assert.Equal(0, height);
    }

    [Fact]
    public void FloorsAtZeroRatherThanGoingNegativeAtTheExactBoundary()
    {
        var height = OrderModeOverlayBoundsRule.TrimmedHeightPhysical(targetHeightPhysical: 48, bottomInsetDip: 48, scale: 1.0);

        Assert.Equal(0, height);
    }

    [Fact]
    public void ZeroInsetLeavesTheFullHeightUntouched()
    {
        var height = OrderModeOverlayBoundsRule.TrimmedHeightPhysical(targetHeightPhysical: 600, bottomInsetDip: 0, scale: 1.0);

        Assert.Equal(600, height);
    }
}
