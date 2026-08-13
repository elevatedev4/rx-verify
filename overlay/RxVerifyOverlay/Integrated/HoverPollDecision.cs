namespace RxVerifyOverlay.Integrated;

/// <summary>
/// Pure decision behind IntegratedBoxesWindow's hover-poll click-through
/// toggle (see that class's HOVER/RIGHT-CLICK AFFORDANCE section) — "is
/// there nothing to hover, so the poll must force WS_EX_TRANSPARENT back
/// ON and skip the cursor-position check entirely." Extracted as a
/// static, WPF/Win32-free method so it's directly unit-testable — same
/// "pure decision pulled out of a native/WPF-heavy caller" pattern as
/// DawBoxRule/FallbackSeparateWindowRule/IntegratedVisibilityGate — see
/// RxVerifyOverlay.Tests/HoverPollDecisionTests.cs.
///
/// REVIEWER BLOCKER FIX (post-review, verdict-tooltips-reports branch): a
/// hidden IntegratedBoxesWindow used to keep its LAST-SHOWN _hotspots and
/// keep the poll ticking with no IsVisible check at all — if the cursor
/// happened to sit inside one of those stale rects while the window was
/// hidden, WS_EX_TRANSPARENT would get cleared on the hidden HWND (a
/// Win32 extended style persists across Hide()/Show() — OnSourceInitialized
/// only ever runs once, at first SourceInitialized, not on every Show()),
/// so a LATER Show() over a completely different Pioneer window/Rx could
/// surface already NON-transparent and swallow real clicks until the next
/// poll tick happened to fix it. This decision is now checked FIRST, in
/// PollCursorForHover, before any cursor-vs-hotspot math even runs — and
/// the same "nothing to hover" reasoning is what IntegratedBoxesWindow.
/// HideAndResetHover forces immediately on hide, rather than waiting for
/// the next poll tick to notice.
/// </summary>
public static class HoverPollDecision
{
    /// <summary>True whenever there is nothing to hover — the window isn't visible, or it has no current hotspots (boxes hidden/cleared/never populated yet) — in which case the poll must force click-through ON unconditionally.</summary>
    public static bool ShouldForceTransparent(bool isVisible, int hotspotCount) => !isVisible || hotspotCount == 0;
}
