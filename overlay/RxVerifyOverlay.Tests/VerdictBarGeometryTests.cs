using System.Linq;
using RxVerifyOverlay.Integrated;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for VerdictBarGeometry (Integrated/VerdictBarGeometry.cs) —
/// originally GreenBarGeometry, the pure geometry behind round 6's owner
/// feedback ("make the green boxes just be a thicker left side only bar
/// ... too distracting to have everything encircled. Leave red boxes the
/// way they are"), generalized in round 7 when the owner asked for red
/// verdicts to render the SAME way. The class itself has no notion of
/// color at all — IntegratedBoxesWindow.SetBoxes calls it once per color
/// — so these tests (renamed/re-targeted from GreenBarGeometryTests, same
/// assertions) already cover both; the RED-labeled section below mirrors
/// them 1:1 against the exact rects/widths the red rendering path now
/// feeds through, to lock in that parity as its own regression coverage
/// rather than relying on "it's the same method" as an implicit argument.
/// </summary>
public class VerdictBarGeometryTests
{
    [Fact]
    public void DeriveBarRectPlacesBarOutsideLeftEdgeWithGapAndGivenWidth()
    {
        // Round 8 (owner: bars sat flush against the field and were
        // "creeping right up on the text"): the bar no longer starts AT
        // rect.X — it sits BarGapDip to the left of it, i.e.
        // X = rect.X - barWidth - gap. 100 - 5 - 3 = 92.
        var rect = new DipRect(X: 100, Y: 200, Width: 50, Height: 20);

        var bar = VerdictBarGeometry.DeriveBarRect(rect, barWidth: 5, gap: 3);

        Assert.Equal(92, bar.X);
        Assert.Equal(200, bar.Y);
        Assert.Equal(5, bar.Width);
        Assert.Equal(20, bar.Height);
    }

    [Fact]
    public void BarRightEdgeSitsExactlyGapDipLeftOfTheFieldRect()
    {
        var rect = new DipRect(X: 150, Y: 0, Width: 40, Height: 10);

        var bar = VerdictBarGeometry.DeriveBarRect(rect);

        var barRightEdge = bar.X + bar.Width;
        Assert.Equal(rect.X - VerdictBarGeometry.BarGapDip, barRightEdge);
    }

    [Fact]
    public void BarNearWindowLeftEdgeClampsToZeroButKeepsFullWidth()
    {
        // BLOCKER 2 (review): these are WINDOW-relative DIPs (not screen-
        // relative), and BoxLayoutAdjuster.ApplyPadding already reaches 2
        // DIP further left before this class ever sees the rect — a field
        // at rect.X=4 is well within the 10 DIP total reach
        // (2 padding + 5 width + 3 gap), so the unclamped math would go
        // negative (4 - 5 - 3 = -4) and WPF would silently clip it.
        var rect = new DipRect(X: 4, Y: 50, Width: 40, Height: 10);

        var bar = VerdictBarGeometry.DeriveBarRect(rect);

        Assert.Equal(0, bar.X);
        Assert.Equal(VerdictBarGeometry.BarWidthDip, bar.Width); // width never shrinks to compensate
        Assert.Equal(50, bar.Y);
        Assert.Equal(10, bar.Height);
    }

    [Fact]
    public void BarFarFromWindowLeftEdgeIsNeverClamped()
    {
        var rect = new DipRect(X: 20, Y: 0, Width: 40, Height: 10);

        var bar = VerdictBarGeometry.DeriveBarRect(rect);

        Assert.Equal(12, bar.X); // 20 - 5 - 3 = 12, unclamped
        Assert.Equal(VerdictBarGeometry.BarWidthDip, bar.Width);
    }

    [Fact]
    public void DefaultBarWidthIsFiveDip()
    {
        // Locks in the chosen bar width (owner asked for ~5 DIP, judgment
        // 4-6) as the actual shipped default, not just a doc claim.
        var rect = new DipRect(X: 0, Y: 0, Width: 50, Height: 20);

        var bar = VerdictBarGeometry.DeriveBarRect(rect);

        Assert.Equal(5, VerdictBarGeometry.BarWidthDip);
        Assert.Equal(5, bar.Width);
    }

    [Fact]
    public void MergedBarRectsForSingleRectIsJustItsOwnBar()
    {
        var rect = new DipRect(X: 10, Y: 10, Width: 50, Height: 20);

        var bars = VerdictBarGeometry.DeriveMergedBarRects(new[] { rect });

        var bar = Assert.Single(bars);
        Assert.Equal(new DipRect(2, 10, 5, 20), bar); // X: 10 - 5 - 3 = 2
    }

    [Fact]
    public void StackedFlushRectsMergeIntoOneContinuousBar()
    {
        // Same X/Width (as BoxLayoutAdjuster.AlignColumnLeftEdges would
        // produce for a stacked column), touching exactly at the seam
        // (as SnapFlushAdjacentEdges would produce) — must merge into ONE
        // bar spanning both, not two abutting bars that WPF could render
        // with a hairline gap between them.
        var top = new DipRect(X: 10, Y: 0, Width: 50, Height: 20);
        var bottom = new DipRect(X: 10, Y: 20, Width: 50, Height: 15);

        var bars = VerdictBarGeometry.DeriveMergedBarRects(new[] { top, bottom });

        var bar = Assert.Single(bars);
        Assert.Equal(2, bar.X); // 10 - 5 - 3 = 2
        Assert.Equal(0, bar.Y);
        Assert.Equal(5, bar.Width);
        Assert.Equal(35, bar.Height); // 0 -> 35, spanning both original rects with no seam
    }

    [Fact]
    public void ThreeStackedFlushRectsMergeIntoOneBarRegardlessOfInputOrder()
    {
        var a = new DipRect(X: 10, Y: 0, Width: 50, Height: 10);
        var b = new DipRect(X: 10, Y: 10, Width: 50, Height: 10);
        var c = new DipRect(X: 10, Y: 20, Width: 50, Height: 10);

        // Shuffled order — merge must not depend on being adjacent in the input list.
        var bars = VerdictBarGeometry.DeriveMergedBarRects(new[] { c, a, b });

        var bar = Assert.Single(bars);
        Assert.Equal(new DipRect(2, 0, 5, 30), bar); // X: 10 - 5 - 3 = 2
    }

    [Fact]
    public void NonTouchingSameColumnRectsDoNotMerge()
    {
        var top = new DipRect(X: 10, Y: 0, Width: 50, Height: 20);
        var farBelow = new DipRect(X: 10, Y: 100, Width: 50, Height: 20);

        var bars = VerdictBarGeometry.DeriveMergedBarRects(new[] { top, farBelow });

        Assert.Equal(2, bars.Count);
        Assert.Contains(bars, b => b == new DipRect(2, 0, 5, 20)); // X: 10 - 5 - 3 = 2
        Assert.Contains(bars, b => b == new DipRect(2, 100, 5, 20));
    }

    [Fact]
    public void DifferentColumnRectsNeverMergeEvenWhenVerticallyAdjacent()
    {
        // Same Y-range (perfectly touching vertically), but different X —
        // must never merge across columns regardless of vertical proximity.
        var left = new DipRect(X: 10, Y: 0, Width: 50, Height: 20);
        var right = new DipRect(X: 200, Y: 0, Width: 50, Height: 20);

        var bars = VerdictBarGeometry.DeriveMergedBarRects(new[] { left, right });

        Assert.Equal(2, bars.Count);
        Assert.Contains(bars, b => b.X == 2); // 10 - 5 - 3 = 2
        Assert.Contains(bars, b => b.X == 192); // 200 - 5 - 3 = 192
    }

    [Fact]
    public void EmptyInputProducesNoBars()
    {
        var bars = VerdictBarGeometry.DeriveMergedBarRects(System.Array.Empty<DipRect>());

        Assert.Empty(bars);
    }

    // ------------------------------------------------------------------
    // RED (round 7): IntegratedBoxesWindow.SetBoxes now calls
    // VerdictBarGeometry.DeriveMergedBarRects a SECOND time for red
    // fields' rects, independently of green's — same method, same
    // BarWidthDip, same merge rule. Mirrors the green cases above 1:1.
    // ------------------------------------------------------------------

    [Fact]
    public void RedFieldGetsTheSameLeftEdgeBarPlacementAndWidthAsGreen()
    {
        var rect = new DipRect(X: 300, Y: 400, Width: 60, Height: 25);

        var bar = VerdictBarGeometry.DeriveBarRect(rect);

        Assert.Equal(292, bar.X); // 300 - 5 - 3 = 292
        Assert.Equal(400, bar.Y);
        Assert.Equal(VerdictBarGeometry.BarWidthDip, bar.Width);
        Assert.Equal(25, bar.Height);
    }

    [Fact]
    public void RedStackedFlushRectsMergeIntoOneContinuousBarJustLikeGreen()
    {
        var top = new DipRect(X: 20, Y: 0, Width: 40, Height: 30);
        var bottom = new DipRect(X: 20, Y: 30, Width: 40, Height: 10);

        var bars = VerdictBarGeometry.DeriveMergedBarRects(new[] { top, bottom });

        var bar = Assert.Single(bars);
        Assert.Equal(new DipRect(12, 0, 5, 40), bar); // X: 20 - 5 - 3 = 12
    }

    [Fact]
    public void RedNonTouchingRectsInTheSameColumnDoNotMerge()
    {
        var top = new DipRect(X: 20, Y: 0, Width: 40, Height: 10);
        var farBelow = new DipRect(X: 20, Y: 200, Width: 40, Height: 10);

        var bars = VerdictBarGeometry.DeriveMergedBarRects(new[] { top, farBelow });

        Assert.Equal(2, bars.Count);
    }

    [Fact]
    public void RedAndGreenColumnsAtTheSameXWouldStillMergeIfDerivedTogether()
    {
        // This is exactly why IntegratedBoxesWindow.SetBoxes calls
        // DeriveMergedBarRects ONCE PER COLOR rather than once for the
        // combined list — proves the geometry itself has no color
        // awareness, so keeping the two colors' rects apart is the
        // caller's responsibility, not this class's.
        var a = new DipRect(X: 20, Y: 0, Width: 40, Height: 10);
        var b = new DipRect(X: 20, Y: 10, Width: 40, Height: 10);

        var bars = VerdictBarGeometry.DeriveMergedBarRects(new[] { a, b });

        Assert.Single(bars);
    }
}
