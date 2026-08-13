using System.Collections.Generic;
using System.Drawing;
using RxVerifyOverlay.Integrated;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for CursorHitTest (Integrated/CursorHitTest.cs) — the pure
/// geometry behind IntegratedBoxesWindow's hover/right-click affordance:
/// converting the cursor's physical screen position into the same
/// window-relative DIP space VerdictBarGeometry's bars live in, then
/// answering "is this point inside any of the current hotspot rects".
/// Same "pure geometry pulled out for its own tests" pattern as
/// DpiRectConverterTests/VerdictBarGeometryTests — no WPF/Win32
/// dependency, no synthetic PHI needed.
/// </summary>
public class CursorHitTestTests
{
    [Fact]
    public void ToDipPointSubtractsWindowOriginAndDividesByDpiScale()
    {
        // Mirrors DpiRectConverterTests' own math exactly, just for a
        // point instead of a rect — a cursor at physical (1150, 300) with
        // the window's own top-left at physical (1000, 200) and 1.25x
        // scaling (125%, a common real-workstation value) lands at DIP
        // (120, 80).
        var cursorPhysical = new Point(1150, 300);
        var windowOriginPhysical = new Point(1000, 200);

        var dip = CursorHitTest.ToDipPoint(cursorPhysical, windowOriginPhysical, dpiScaleX: 1.25, dpiScaleY: 1.25);

        Assert.Equal(120, dip.X);
        Assert.Equal(80, dip.Y);
    }

    [Fact]
    public void ToDipPointAt100PercentScalingIsJustTheOffset()
    {
        var cursorPhysical = new Point(550, 420);
        var windowOriginPhysical = new Point(500, 400);

        var dip = CursorHitTest.ToDipPoint(cursorPhysical, windowOriginPhysical, dpiScaleX: 1.0, dpiScaleY: 1.0);

        Assert.Equal(50, dip.X);
        Assert.Equal(20, dip.Y);
    }

    [Fact]
    public void IsWithinAnyRectTrueWhenPointFallsInsideOneOfSeveralRects()
    {
        var rects = new List<DipRect>
        {
            new(X: 0, Y: 0, Width: 10, Height: 10),
            new(X: 100, Y: 200, Width: 5, Height: 20),
        };

        Assert.True(CursorHitTest.IsWithinAnyRect(102, 210, rects));
    }

    [Fact]
    public void IsWithinAnyRectFalseWhenPointIsOutsideEveryRect()
    {
        var rects = new List<DipRect>
        {
            new(X: 0, Y: 0, Width: 10, Height: 10),
            new(X: 100, Y: 200, Width: 5, Height: 20),
        };

        Assert.False(CursorHitTest.IsWithinAnyRect(50, 50, rects));
    }

    [Fact]
    public void IsWithinAnyRectTrueExactlyOnABoundaryEdge()
    {
        // Inclusive on every edge — see CursorHitTest.IsWithinAnyRect's own
        // doc: errs toward "still interactive" rather than a pharmacist's
        // cursor sitting right at a thin bar's edge finding it unresponsive.
        var rects = new List<DipRect> { new(X: 100, Y: 200, Width: 5, Height: 20) };

        Assert.True(CursorHitTest.IsWithinAnyRect(100, 200, rects)); // top-left corner
        Assert.True(CursorHitTest.IsWithinAnyRect(105, 220, rects)); // bottom-right corner
    }

    [Fact]
    public void IsWithinAnyRectFalseForAnEmptyHotspotList()
    {
        Assert.False(CursorHitTest.IsWithinAnyRect(0, 0, new List<DipRect>()));
    }

    // ------------------------------------------------------------------
    // FindContainingRectIndex (fix/hover-popup-live branch) — added so
    // IntegratedBoxesWindow's poll can tell HoverStateMachine WHICH field
    // the cursor is over, not just whether it's over any hotspot at all.
    // IsWithinAnyRect is now implemented in terms of this method — these
    // tests double as regression coverage for that refactor too.
    // ------------------------------------------------------------------

    [Fact]
    public void FindContainingRectIndexReturnsTheMatchingIndex()
    {
        var rects = new List<DipRect>
        {
            new(X: 0, Y: 0, Width: 10, Height: 10),
            new(X: 100, Y: 200, Width: 5, Height: 20),
        };

        Assert.Equal(1, CursorHitTest.FindContainingRectIndex(102, 210, rects));
    }

    [Fact]
    public void FindContainingRectIndexReturnsMinusOneWhenNoRectMatches()
    {
        var rects = new List<DipRect> { new(X: 0, Y: 0, Width: 10, Height: 10) };

        Assert.Equal(-1, CursorHitTest.FindContainingRectIndex(50, 50, rects));
    }

    [Fact]
    public void FindContainingRectIndexReturnsMinusOneForAnEmptyList()
    {
        Assert.Equal(-1, CursorHitTest.FindContainingRectIndex(0, 0, new List<DipRect>()));
    }

    [Fact]
    public void FindContainingRectIndexReturnsTheFirstMatchWhenRectsOverlap()
    {
        // Hotspots aren't expected to overlap in real use (each is one
        // field's own left-edge bar), but first-match-wins is the defined,
        // deterministic behavior rather than leaving it unspecified.
        var rects = new List<DipRect>
        {
            new(X: 0, Y: 0, Width: 20, Height: 20),
            new(X: 5, Y: 5, Width: 20, Height: 20),
        };

        Assert.Equal(0, CursorHitTest.FindContainingRectIndex(10, 10, rects));
    }
}
