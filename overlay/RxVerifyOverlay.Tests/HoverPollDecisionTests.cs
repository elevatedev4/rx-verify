using RxVerifyOverlay.Integrated;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for HoverPollDecision (Integrated/HoverPollDecision.cs) —
/// the pure gate behind IntegratedBoxesWindow's hover-poll click-through
/// toggle, extracted specifically so the post-review blocker fix (a
/// hidden window must never poll-toggle transparency off from stale
/// hotspots) has a directly testable seam, since IntegratedBoxesWindow
/// itself (a WPF Window with a live HWND) isn't instantiable in this test
/// project the way its pure geometry helpers are.
/// </summary>
public class HoverPollDecisionTests
{
    [Fact]
    public void ForcesTransparentWhenNotVisibleEvenWithHotspotsPresent()
    {
        // REVIEWER BLOCKER FIX: this is the exact scenario that leaked —
        // a hidden window that still remembers its last-shown hotspot
        // count must still force transparent, never fall through to a
        // cursor-position check.
        Assert.True(HoverPollDecision.ShouldForceTransparent(isVisible: false, hotspotCount: 3));
    }

    [Fact]
    public void ForcesTransparentWhenVisibleButNoHotspots()
    {
        Assert.True(HoverPollDecision.ShouldForceTransparent(isVisible: true, hotspotCount: 0));
    }

    [Fact]
    public void ForcesTransparentWhenNeitherVisibleNorHasHotspots()
    {
        Assert.True(HoverPollDecision.ShouldForceTransparent(isVisible: false, hotspotCount: 0));
    }

    [Fact]
    public void DoesNotForceTransparentWhenVisibleWithHotspots()
    {
        // The only case where the poll should proceed to the real
        // cursor-vs-hotspot check instead of short-circuiting.
        Assert.False(HoverPollDecision.ShouldForceTransparent(isVisible: true, hotspotCount: 1));
    }
}
