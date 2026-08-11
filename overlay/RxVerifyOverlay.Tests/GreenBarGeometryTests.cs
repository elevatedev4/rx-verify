using System.Linq;
using RxVerifyOverlay.Integrated;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for GreenBarGeometry (Integrated/GreenBarGeometry.cs) — the
/// pure geometry behind round 6's owner feedback ("make the green boxes
/// just be a thicker left side only bar ... too distracting to have
/// everything encircled. Leave red boxes the way they are"). Covers the
/// two scenarios the brief specifically called out: bar X/width derived
/// correctly from a rect, and stacked/flush rects producing one seamless
/// abutting bar rather than two separate elements that could show a
/// rendering seam.
/// </summary>
public class GreenBarGeometryTests
{
    [Fact]
    public void DeriveBarRectPlacesBarAtLeftEdgeWithGivenWidth()
    {
        var rect = new DipRect(X: 100, Y: 200, Width: 50, Height: 20);

        var bar = GreenBarGeometry.DeriveBarRect(rect, barWidth: 5);

        Assert.Equal(100, bar.X);
        Assert.Equal(200, bar.Y);
        Assert.Equal(5, bar.Width);
        Assert.Equal(20, bar.Height);
    }

    [Fact]
    public void DefaultBarWidthIsFiveDip()
    {
        // Locks in the chosen bar width (owner asked for ~5 DIP, judgment
        // 4-6) as the actual shipped default, not just a doc claim.
        var rect = new DipRect(X: 0, Y: 0, Width: 50, Height: 20);

        var bar = GreenBarGeometry.DeriveBarRect(rect);

        Assert.Equal(5, GreenBarGeometry.BarWidthDip);
        Assert.Equal(5, bar.Width);
    }

    [Fact]
    public void MergedBarRectsForSingleRectIsJustItsOwnBar()
    {
        var rect = new DipRect(X: 10, Y: 10, Width: 50, Height: 20);

        var bars = GreenBarGeometry.DeriveMergedBarRects(new[] { rect });

        var bar = Assert.Single(bars);
        Assert.Equal(new DipRect(10, 10, 5, 20), bar);
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

        var bars = GreenBarGeometry.DeriveMergedBarRects(new[] { top, bottom });

        var bar = Assert.Single(bars);
        Assert.Equal(10, bar.X);
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
        var bars = GreenBarGeometry.DeriveMergedBarRects(new[] { c, a, b });

        var bar = Assert.Single(bars);
        Assert.Equal(new DipRect(10, 0, 5, 30), bar);
    }

    [Fact]
    public void NonTouchingSameColumnRectsDoNotMerge()
    {
        var top = new DipRect(X: 10, Y: 0, Width: 50, Height: 20);
        var farBelow = new DipRect(X: 10, Y: 100, Width: 50, Height: 20);

        var bars = GreenBarGeometry.DeriveMergedBarRects(new[] { top, farBelow });

        Assert.Equal(2, bars.Count);
        Assert.Contains(bars, b => b == new DipRect(10, 0, 5, 20));
        Assert.Contains(bars, b => b == new DipRect(10, 100, 5, 20));
    }

    [Fact]
    public void DifferentColumnRectsNeverMergeEvenWhenVerticallyAdjacent()
    {
        // Same Y-range (perfectly touching vertically), but different X —
        // must never merge across columns regardless of vertical proximity.
        var left = new DipRect(X: 10, Y: 0, Width: 50, Height: 20);
        var right = new DipRect(X: 200, Y: 0, Width: 50, Height: 20);

        var bars = GreenBarGeometry.DeriveMergedBarRects(new[] { left, right });

        Assert.Equal(2, bars.Count);
        Assert.Contains(bars, b => b.X == 10);
        Assert.Contains(bars, b => b.X == 200);
    }

    [Fact]
    public void EmptyInputProducesNoBars()
    {
        var bars = GreenBarGeometry.DeriveMergedBarRects(System.Array.Empty<DipRect>());

        Assert.Empty(bars);
    }
}
