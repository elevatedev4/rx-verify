using RxVerifyOverlay.Integrated;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for BoxLayoutAdjuster (Integrated/BoxLayoutAdjuster.cs) —
/// the pure geometry behind the owner's round-2 readability feedback:
/// more breathing room inside each box, and no visible gap between boxes
/// that are stacked directly on top of each other (e.g. the Patient
/// category's Name/DOB/Address rows).
/// </summary>
public class BoxLayoutAdjusterPaddingTests
{
    [Fact]
    public void PaddingExpandsEachSideByTheGivenAmount()
    {
        var rect = new DipRect(X: 100, Y: 200, Width: 50, Height: 20);

        var padded = BoxLayoutAdjuster.ApplyPadding(rect, padding: 4);

        Assert.Equal(96, padded.X);
        Assert.Equal(196, padded.Y);
        Assert.Equal(58, padded.Width);
        Assert.Equal(28, padded.Height);
    }

    [Fact]
    public void DefaultPaddingIsTheThinRound4Outset()
    {
        // Locks in the round-4 shrink (4px "breathing room" -> 2px thin
        // outset, so the colored stroke overlaps Pioneer's own native
        // field border instead of sitting outside it) as the actual
        // SHIPPED default, not just something the constant's doc claims.
        var rect = new DipRect(X: 100, Y: 200, Width: 50, Height: 20);

        var padded = BoxLayoutAdjuster.ApplyPadding(rect);

        Assert.Equal(2, BoxLayoutAdjuster.PaddingDip);
        Assert.Equal(98, padded.X);
        Assert.Equal(198, padded.Y);
        Assert.Equal(54, padded.Width);
        Assert.Equal(24, padded.Height);
    }

    [Fact]
    public void PaddingListOverloadAppliesToEveryRectIndependently()
    {
        var rects = new[]
        {
            new DipRect(0, 0, 10, 10),
            new DipRect(100, 100, 20, 20)
        };

        var padded = BoxLayoutAdjuster.ApplyPadding(rects, padding: 2);

        Assert.Equal(new DipRect(-2, -2, 14, 14), padded[0]);
        Assert.Equal(new DipRect(98, 98, 24, 24), padded[1]);
    }
}

public class BoxLayoutAdjusterFlushEdgeTests
{
    [Fact]
    public void TwoStackedRectsGetAFlushSharedEdge()
    {
        // Two patient-field rows, one directly under the other, with a
        // small gap (well under the threshold) between them.
        var top = new DipRect(X: 10, Y: 0, Width: 100, Height: 20);
        var bottom = new DipRect(X: 10, Y: 24, Width: 100, Height: 20); // 4px gap

        var adjusted = BoxLayoutAdjuster.SnapFlushAdjacentEdges(new[] { top, bottom });

        var adjustedTop = adjusted[0];
        var adjustedBottom = adjusted[1];

        // Facing edges coincide exactly.
        Assert.Equal(adjustedBottom.Y, adjustedTop.Y + adjustedTop.Height, precision: 10);

        // The shared edge sits at the midpoint of the original gap.
        Assert.Equal(22, adjustedTop.Y + adjustedTop.Height, precision: 10);

        // Outer edges (top of the top rect, bottom of the bottom rect) are untouched.
        Assert.Equal(top.Y, adjustedTop.Y);
        Assert.Equal(bottom.Y + bottom.Height, adjustedBottom.Y + adjustedBottom.Height);
    }

    [Fact]
    public void HorizontallySeparateRectsAreUntouchedRegardlessOfVerticalGap()
    {
        // Same vertical arrangement as the flush case, but shifted so
        // they no longer overlap horizontally at all (e.g. two
        // side-by-side category columns) — must never be snapped
        // together just because they're vertically close.
        var left = new DipRect(X: 0, Y: 0, Width: 50, Height: 20);
        var right = new DipRect(X: 200, Y: 24, Width: 50, Height: 20);

        var adjusted = BoxLayoutAdjuster.SnapFlushAdjacentEdges(new[] { left, right });

        Assert.Equal(left, adjusted[0]);
        Assert.Equal(right, adjusted[1]);
    }

    [Fact]
    public void ThreeStackedRectsGetBothBoundariesFlush()
    {
        var first = new DipRect(X: 0, Y: 0, Width: 100, Height: 20);
        var second = new DipRect(X: 0, Y: 25, Width: 100, Height: 20); // 5px gap
        var third = new DipRect(X: 0, Y: 50, Width: 100, Height: 20);  // 5px gap

        var adjusted = BoxLayoutAdjuster.SnapFlushAdjacentEdges(new[] { first, second, third });

        var a = adjusted[0];
        var b = adjusted[1];
        var c = adjusted[2];

        Assert.Equal(b.Y, a.Y + a.Height, precision: 10);
        Assert.Equal(c.Y, b.Y + b.Height, precision: 10);

        // Outer edges untouched.
        Assert.Equal(0, a.Y);
        Assert.Equal(70, c.Y + c.Height);
    }

    [Fact]
    public void BoxesThatWouldOverlapAfterPaddingAreClampedToNonOverlapping()
    {
        // Simulates the post-padding case: two rects whose padded
        // versions actually overlap slightly (a "negative gap") rather
        // than just sitting close — must still end up with one shared
        // boundary, never a real overlap.
        var top = new DipRect(X: 0, Y: 0, Width: 100, Height: 24);
        var bottom = new DipRect(X: 0, Y: 20, Width: 100, Height: 24); // overlaps top by 4px

        var adjusted = BoxLayoutAdjuster.SnapFlushAdjacentEdges(new[] { top, bottom });

        var adjustedTop = adjusted[0];
        var adjustedBottom = adjusted[1];

        Assert.Equal(adjustedBottom.Y, adjustedTop.Y + adjustedTop.Height, precision: 10);
        Assert.True(adjustedTop.Y + adjustedTop.Height <= adjustedBottom.Y + 1e-9); // never overlapping
    }

    [Fact]
    public void GapAboveThresholdLeavesBothRectsUntouched()
    {
        var top = new DipRect(X: 0, Y: 0, Width: 100, Height: 20);
        var bottom = new DipRect(X: 0, Y: 100, Width: 100, Height: 20); // 80px gap, well over the ~7px round-4 threshold

        var adjusted = BoxLayoutAdjuster.SnapFlushAdjacentEdges(new[] { top, bottom });

        Assert.Equal(top, adjusted[0]);
        Assert.Equal(bottom, adjusted[1]);
    }

    [Fact]
    public void DefaultFlushGapThresholdIsHalvedToMatchTheThinnerRound4Padding()
    {
        // Round 4 halved PaddingDip (4 -> 2), so a given pair now closes
        // roughly half as much of its raw gap via padding alone — this
        // threshold shrank in step (14 -> 7) so pairs that used to land
        // just inside the old threshold don't now land just outside it
        // and silently stop snapping.
        Assert.Equal(7, BoxLayoutAdjuster.FlushGapThresholdDip);

        var top = new DipRect(X: 0, Y: 0, Width: 100, Height: 20);
        var justInside = new DipRect(X: 0, Y: 27, Width: 100, Height: 20);  // 7px gap -> snaps
        var justOutside = new DipRect(X: 0, Y: 27.1, Width: 100, Height: 20); // just over 7px -> untouched

        var snapped = BoxLayoutAdjuster.SnapFlushAdjacentEdges(new[] { top, justInside });
        Assert.Equal(snapped[1].Y, snapped[0].Y + snapped[0].Height, precision: 10);

        var untouched = BoxLayoutAdjuster.SnapFlushAdjacentEdges(new[] { top, justOutside });
        Assert.Equal(top, untouched[0]);
        Assert.Equal(justOutside, untouched[1]);
    }

    [Fact]
    public void PaddingThenFlushSnapProducesNoGapBetweenAdjacentFields()
    {
        // End-to-end: the exact pipeline IntegratedBoxesWindow runs —
        // pad first, then snap — using realistic field-row dimensions
        // (raw UIA rects a few px apart vertically, as PioneerRx actually
        // lays out stacked fields).
        var nameRow = new DipRect(X: 50, Y: 100, Width: 200, Height: 16);
        var dobRow = new DipRect(X: 50, Y: 120, Width: 200, Height: 16); // 4px raw gap

        var padded = BoxLayoutAdjuster.ApplyPadding(new[] { nameRow, dobRow });
        var flush = BoxLayoutAdjuster.SnapFlushAdjacentEdges(padded);

        Assert.Equal(flush[1].Y, flush[0].Y + flush[0].Height, precision: 10);
    }
}

/// <summary>
/// Unit tests for BoxLayoutAdjuster.AlignColumnLeftEdges — the owner's
/// round-5 "make the left sides of the rectangles match up ... some of
/// them will be off by themselves" feedback.
/// </summary>
public class BoxLayoutAdjusterLeftEdgeAlignmentTests
{
    [Fact]
    public void ThreeStackedBoxesWithNearLeftsAllSnapToTheMinimum()
    {
        // Exactly the owner's own example: lefts 100/103/101, stacked
        // (small vertical gaps) — all three are within the 8 DIP
        // tolerance of each other, so they form one column and all move
        // to the group's minimum (100).
        var first = new DipRect(X: 100, Y: 0, Width: 200, Height: 20);
        var second = new DipRect(X: 103, Y: 25, Width: 200, Height: 20);
        var third = new DipRect(X: 101, Y: 50, Width: 200, Height: 20);

        var aligned = BoxLayoutAdjuster.AlignColumnLeftEdges(new[] { first, second, third });

        Assert.Equal(100, aligned[0].X);
        Assert.Equal(100, aligned[1].X);
        Assert.Equal(100, aligned[2].X);
    }

    [Fact]
    public void RightEdgesNeverMoveOnlyLeftEdgesShiftAndWidthAbsorbsTheDifference()
    {
        // Owner explicitly didn't ask for right edges to move — confirms
        // X + Width (the right edge) is mathematically identical before
        // and after for every box that gets shifted.
        var first = new DipRect(X: 100, Y: 0, Width: 200, Height: 20);
        var second = new DipRect(X: 103, Y: 25, Width: 197, Height: 20); // different width, same right edge target math

        var originalRightEdges = new[] { first.X + first.Width, second.X + second.Width };

        var aligned = BoxLayoutAdjuster.AlignColumnLeftEdges(new[] { first, second });

        Assert.Equal(originalRightEdges[0], aligned[0].X + aligned[0].Width, precision: 10);
        Assert.Equal(originalRightEdges[1], aligned[1].X + aligned[1].Width, precision: 10);
        // The box that WAS the minimum (first, X=100) is untouched entirely.
        Assert.Equal(first, aligned[0]);
    }

    [Fact]
    public void ALoneBoxFarRightIsUntouched()
    {
        var column = new[]
        {
            new DipRect(X: 100, Y: 0, Width: 200, Height: 20),
            new DipRect(X: 101, Y: 25, Width: 200, Height: 20)
        };
        var farRight = new DipRect(X: 900, Y: 10, Width: 100, Height: 20); // no one else's left edge is anywhere near this

        var aligned = BoxLayoutAdjuster.AlignColumnLeftEdges(new[] { column[0], column[1], farRight });

        Assert.Equal(farRight, aligned[2]);
    }

    [Fact]
    public void NearLeftsButFarApartVerticallyAreTreatedAsDifferentColumnsAndStayUntouched()
    {
        // DECISION (explicitly left to our judgment): vertical proximity
        // DOES gate the grouping. Two fields with coincidentally similar
        // left edges but on opposite ends of the panel are NOT "the ones
        // that should be lined up" — grouping them would create a visual
        // relationship that doesn't reflect how the fields are actually
        // laid out. Gap here (400 DIP) is far beyond
        // ColumnVerticalGapToleranceDip (40).
        var top = new DipRect(X: 100, Y: 0, Width: 200, Height: 20);
        var farBelow = new DipRect(X: 102, Y: 400, Width: 200, Height: 20);

        var aligned = BoxLayoutAdjuster.AlignColumnLeftEdges(new[] { top, farBelow });

        Assert.Equal(top, aligned[0]);
        Assert.Equal(farBelow, aligned[1]);
    }

    [Fact]
    public void NearLeftsWithAModerateGapUnderTheVerticalToleranceStillGroup()
    {
        // Confirms ColumnVerticalGapToleranceDip has real slack — a gap
        // comfortably bridging a normal category-to-category transition
        // (e.g. last Patient field to first Prescriber field) still
        // counts as the same column, not just literally-touching pairs.
        var top = new DipRect(X: 100, Y: 0, Width: 200, Height: 20);
        var moderateGapBelow = new DipRect(X: 102, Y: 50, Width: 200, Height: 20); // 30px gap, under the 40 tolerance

        var aligned = BoxLayoutAdjuster.AlignColumnLeftEdges(new[] { top, moderateGapBelow });

        Assert.Equal(100, aligned[0].X);
        Assert.Equal(100, aligned[1].X);
    }

    [Fact]
    public void TransitiveChainGroupsALongColumnEvenWhenTheEndsAreFarApart()
    {
        // First and last are ~90px apart vertically (over the 40
        // tolerance if checked DIRECTLY), but each CONSECUTIVE pair is
        // well within tolerance, so the whole chain still becomes one
        // column via transitive membership.
        var a = new DipRect(X: 100, Y: 0, Width: 200, Height: 20);
        var b = new DipRect(X: 101, Y: 30, Width: 200, Height: 20);  // 10px gap from a
        var c = new DipRect(X: 99, Y: 60, Width: 200, Height: 20);   // 10px gap from b
        var d = new DipRect(X: 100, Y: 90, Width: 200, Height: 20);  // 10px gap from c

        var aligned = BoxLayoutAdjuster.AlignColumnLeftEdges(new[] { a, b, c, d });

        Assert.All(aligned, rect => Assert.Equal(99, rect.X));
    }

    [Fact]
    public void GroupsWithOnlyOneMemberAreReturnedExactlyUnchanged()
    {
        // Two independent singletons (both far from any left-edge
        // neighbor) — neither should be touched.
        var left = new DipRect(X: 0, Y: 0, Width: 50, Height: 20);
        var right = new DipRect(X: 500, Y: 0, Width: 50, Height: 20);

        var aligned = BoxLayoutAdjuster.AlignColumnLeftEdges(new[] { left, right });

        Assert.Equal(left, aligned[0]);
        Assert.Equal(right, aligned[1]);
    }

    [Fact]
    public void FullPipelinePadThenFlushThenAlignProducesOneCleanColumn()
    {
        // End-to-end, the exact order IntegratedBoxesWindow.SetBoxes runs:
        // ApplyPadding -> SnapFlushAdjacentEdges -> AlignColumnLeftEdges.
        var nameRow = new DipRect(X: 50, Y: 100, Width: 200, Height: 16);
        var dobRow = new DipRect(X: 53, Y: 120, Width: 200, Height: 16);   // 4px raw gap, 3px left jitter
        var addressRow = new DipRect(X: 51, Y: 140, Width: 200, Height: 16); // 4px raw gap, 1px left jitter

        var padded = BoxLayoutAdjuster.ApplyPadding(new[] { nameRow, dobRow, addressRow });
        var flush = BoxLayoutAdjuster.SnapFlushAdjacentEdges(padded);
        var aligned = BoxLayoutAdjuster.AlignColumnLeftEdges(flush);

        // All three left edges now coincide exactly.
        Assert.Equal(aligned[0].X, aligned[1].X, precision: 10);
        Assert.Equal(aligned[1].X, aligned[2].X, precision: 10);

        // Vertical flushness from the earlier pass is preserved.
        Assert.Equal(aligned[1].Y, aligned[0].Y + aligned[0].Height, precision: 10);
        Assert.Equal(aligned[2].Y, aligned[1].Y + aligned[1].Height, precision: 10);
    }
}
