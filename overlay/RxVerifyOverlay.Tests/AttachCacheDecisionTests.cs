using RxVerifyOverlay.Uia;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for AttachCacheDecision (Uia/AttachCacheDecision.cs) — the
/// pure decision behind PioneerRxWindow.TryAttach's fast path
/// (uia-read-latency branch, item 2d: skip a full top-level-window
/// enumeration + disambiguation when the previously-resolved window is
/// cheaply and safely known to still be correct).
/// </summary>
public class AttachCacheDecisionTests
{
    [Fact]
    public void ReusesWhenAliveForegroundAndTitleStillMatches()
    {
        Assert.True(AttachCacheDecision.CanReuseCachedWindow(
            isWindowAlive: true, isForegroundWindow: true, titleStillMatchesTargetPrefix: true));
    }

    [Fact]
    public void DoesNotReuseWhenWindowIsNoLongerAlive()
    {
        Assert.False(AttachCacheDecision.CanReuseCachedWindow(
            isWindowAlive: false, isForegroundWindow: true, titleStillMatchesTargetPrefix: true));
    }

    [Fact]
    public void DoesNotReuseWhenADifferentWindowIsForeground()
    {
        // The pharmacist alt-tabbed to something else (maybe even the
        // overlay itself) — must fall through to the full disambiguation
        // (which may still pick the same window via its Z-order-topmost
        // rule, but that decision belongs to PickBestCandidate, not here).
        Assert.False(AttachCacheDecision.CanReuseCachedWindow(
            isWindowAlive: true, isForegroundWindow: false, titleStillMatchesTargetPrefix: true));
    }

    [Fact]
    public void DoesNotReuseWhenTitleNoLongerMatchesATargetPrefix()
    {
        // Guards the rare HWND-reuse case: the same native handle now
        // belongs to a window that isn't a PioneerRx Pre-Check/Edit/New
        // Rx screen at all.
        Assert.False(AttachCacheDecision.CanReuseCachedWindow(
            isWindowAlive: true, isForegroundWindow: true, titleStillMatchesTargetPrefix: false));
    }

    [Fact]
    public void DoesNotReuseWhenEverythingIsFalse()
    {
        Assert.False(AttachCacheDecision.CanReuseCachedWindow(
            isWindowAlive: false, isForegroundWindow: false, titleStillMatchesTargetPrefix: false));
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public void RequiresAllThreeConditionsSimultaneously(bool isAlive, bool isForeground, bool titleMatches)
    {
        // Any single condition failing (with the other two true) must
        // still refuse to reuse — "when in doubt, re-resolve".
        Assert.False(AttachCacheDecision.CanReuseCachedWindow(isAlive, isForeground, titleMatches));
    }
}
