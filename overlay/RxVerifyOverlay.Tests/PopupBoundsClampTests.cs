using System.Drawing;
using RxVerifyOverlay.Integrated;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for PopupBoundsClamp (Integrated/PopupBoundsClamp.cs) —
/// the pure geometry behind HoverPopupWindow's on-screen clamping
/// (RXVERIFY-TROUBLESHOOT 2026-08 follow-up: the popup could previously
/// render off-screen near a monitor's edges).
/// </summary>
public class PopupBoundsClampTests
{
    private static readonly Rectangle Monitor = new(0, 0, 1920, 1080);

    [Fact]
    public void LeavesAnAlreadyOnScreenRectUntouched()
    {
        var proposed = new Rectangle(500, 400, 200, 100);

        var result = PopupBoundsClamp.Clamp(proposed, Monitor);

        Assert.Equal(new Point(500, 400), result);
    }

    [Fact]
    public void PullsBackInFromTheRightEdge()
    {
        // Cursor near the right edge — popup would overflow past x=1920.
        var proposed = new Rectangle(1850, 400, 200, 100);

        var result = PopupBoundsClamp.Clamp(proposed, Monitor);

        Assert.Equal(1920 - 200, result.X);
        Assert.Equal(400, result.Y); // Y untouched — only X overflowed
    }

    [Fact]
    public void PullsBackInFromTheBottomEdge()
    {
        var proposed = new Rectangle(500, 1050, 200, 100);

        var result = PopupBoundsClamp.Clamp(proposed, Monitor);

        Assert.Equal(500, result.X);
        Assert.Equal(1080 - 100, result.Y);
    }

    [Fact]
    public void PullsBackInFromBothTheRightAndBottomEdgesAtOnce()
    {
        // A hotspot right at the bottom-right corner (the common case for
        // the LAST field in a stacked column near a monitor's edge).
        var proposed = new Rectangle(1900, 1060, 200, 100);

        var result = PopupBoundsClamp.Clamp(proposed, Monitor);

        Assert.Equal(1920 - 200, result.X);
        Assert.Equal(1080 - 100, result.Y);
    }

    [Fact]
    public void NeverPushesPastTheLeftOrTopEdgeEvenForAPopupWiderThanTheMonitor()
    {
        // Degenerate case (never expected in practice, but defined): a
        // popup wider than the whole monitor must land at the monitor's
        // own left edge, not hang off it negative.
        var proposed = new Rectangle(1900, 1060, 2000, 1200);

        var result = PopupBoundsClamp.Clamp(proposed, Monitor);

        Assert.Equal(0, result.X);
        Assert.Equal(0, result.Y);
    }

    [Fact]
    public void HandlesAMonitorThatIsNotAtTheVirtualDesktopOrigin()
    {
        // A secondary monitor to the LEFT of the primary has negative
        // X coordinates in virtual-desktop space — clamping must respect
        // ITS bounds, not assume (0,0) is always the top-left.
        var secondaryMonitor = new Rectangle(-1920, 0, 1920, 1080);
        var proposed = new Rectangle(-100, 400, 200, 100); // overflows the RIGHT edge of the secondary monitor (-100+200=100 > 0)

        var result = PopupBoundsClamp.Clamp(proposed, secondaryMonitor);

        Assert.Equal(0 - 200, result.X);
        Assert.Equal(400, result.Y);
    }
}
