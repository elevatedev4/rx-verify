namespace RxVerifyOverlay.Uia;

/// <summary>
/// Pure decision logic behind PioneerRxWindow.TryAttach's fast path
/// (latency fix, branch brief item 2d: the "attach" timing bucket cost
/// 240-335ms per refresh, dominated by constructing a brand-new
/// UIA3Automation every call plus a full top-level-window
/// enumeration/disambiguation — see PioneerRxWindow.cs). Given what's
/// cheaply knowable about OS window state WITHOUT any UIA/COM call
/// (just two Win32 calls: IsWindow + GetForegroundWindow), should the
/// previously-resolved PioneerRx window be reused as-is, or does
/// TryAttach need to fall through to its full resolve?
///
/// Deliberately just booleans in, boolean out — no FlaUI/UIA/Windows
/// dependency at all — so this is covered by fast xUnit tests; see
/// RxVerifyOverlay.Tests/AttachCacheDecisionTests.cs.
/// </summary>
public static class AttachCacheDecision
{
    /// <summary>
    /// True only when the cached window is DEFINITELY still the correct
    /// one to hand back without re-enumerating: it still exists
    /// (<paramref name="isWindowAlive"/>) AND it's the current OS
    /// foreground window (<paramref name="isForegroundWindow"/>) AND its
    /// title still starts with a target prefix
    /// (<paramref name="titleStillMatchesTargetPrefix"/> — a cheap guard
    /// against the rare case of HWND reuse handing an unrelated window's
    /// handle to a since-closed PioneerRx window).
    ///
    /// "Is the current foreground window" is exactly rule 1 of
    /// PioneerRxWindow.PickBestCandidate's own disambiguation preference
    /// order (see that class's "DISAMBIGUATING MULTIPLE MATCHES" doc) —
    /// so reusing here can never pick a DIFFERENT window than the full
    /// disambiguation logic would have chosen; it only ever skips
    /// REDOING work that would have landed on the same answer. Any other
    /// combination — window gone, a different window (or none) is now
    /// foreground, even a title that no longer looks like a target
    /// window — falls through to a full re-resolve: "when in doubt,
    /// re-resolve" (same rule Ocr/CaptureRegionCache.cs uses).
    /// </summary>
    public static bool CanReuseCachedWindow(bool isWindowAlive, bool isForegroundWindow, bool titleStillMatchesTargetPrefix)
    {
        return isWindowAlive && isForegroundWindow && titleStillMatchesTargetPrefix;
    }
}
