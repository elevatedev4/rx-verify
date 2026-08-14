using System.Drawing;

namespace RxVerifyOverlay.Integrated;

/// <summary>
/// Pure geometry behind HoverPopupWindow's on-screen clamping (owner
/// follow-up, RXVERIFY-TROUBLESHOOT 2026-08): ShowFor originally
/// positioned the popup at a fixed cursor + offset with no bounds check
/// at all, so hovering a verdict bar near a monitor's right or bottom
/// edge could render the popup partially or entirely off-screen —
/// unreadable, or (worse) landing on a DIFFERENT monitor than the one
/// Pioneer/the cursor is actually on.
///
/// Pushes the proposed rect back inside <paramref name="monitorBounds"/>
/// on whichever edge(s) it overflows; never resizes it. Checks
/// right/bottom overflow FIRST, then left/top — that order matters for
/// the (never expected in practice, but defined rather than left
/// unspecified) degenerate case of a popup wider or taller than the
/// monitor itself: clamping right/bottom first would push X/Y negative
/// past the monitor's own left/top edge, and the left/top clamp that
/// runs after is what pulls it back to the monitor's origin instead of
/// leaving it hanging off the far side.
/// </summary>
public static class PopupBoundsClamp
{
    public static Point Clamp(Rectangle proposed, Rectangle monitorBounds)
    {
        var x = proposed.X;
        var y = proposed.Y;

        if (proposed.Right > monitorBounds.Right) x = monitorBounds.Right - proposed.Width;
        if (proposed.Bottom > monitorBounds.Bottom) y = monitorBounds.Bottom - proposed.Height;

        if (x < monitorBounds.Left) x = monitorBounds.Left;
        if (y < monitorBounds.Top) y = monitorBounds.Top;

        return new Point(x, y);
    }
}
