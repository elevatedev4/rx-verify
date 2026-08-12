using System;
using FlaUI.Core.AutomationElements;

namespace RxVerifyOverlay.Uia;

/// <summary>
/// Tri-state answer to "is PioneerRx's OUTER Common tab the active one
/// right now?" — computed once per tick in
/// IntegratedOverlayCoordinator.TickCore and fed into
/// Integrated/IntegratedVisibilityGate.ShouldShowBoxes. Owner report:
/// during pre-check, switching OFF the outer Common tab (to Patient
/// Education/Interactions/Fill History/...) must hide the verdict boxes;
/// switching back must reveal them. The pre-existing proxy
/// (hasResolvableFieldRects — "did any entered field resolve to an
/// on-screen rect this tick") does NOT actually do this: RxDetailsPanel's
/// field elements evidently keep non-empty BoundingRectangles even while
/// a different outer tab is showing.
/// </summary>
public enum CommonTabState
{
    /// <summary>Common is definitely the active outer tab.</summary>
    On,

    /// <summary>Common is definitely NOT the active outer tab.</summary>
    Off,

    /// <summary>
    /// Neither confirmed signal below could be read this tick (and
    /// neither has EVER been read successfully this attach-session) —
    /// this Pioneer version's tree shape may differ from the two
    /// confirmed dumps. Callers should fall back to whatever proxy they
    /// used before this gate existed.
    /// </summary>
    Unknown
}

/// <summary>
/// Layered, strongest-confirmed-signal-first detector behind
/// CommonTabState, matching FieldReader/EnteredFieldElementCache's own
/// "cache the located element per attach-session, re-find only when it
/// goes stale" latency discipline (see EnteredFieldElementCache.cs) —
/// this runs on every ~250ms tick, so a full-tree search every single
/// tick is not acceptable, only on a cache miss or a confirmed stale
/// element (ComException/element gone).
///
/// PRIMARY — FieldMap.OuterCommonTabNamePrefix: locate the outer "Common"
/// TabItem anywhere under the window (no confirmed AutomationId exists
/// for the outer Tab control itself to narrow the search by) and read its
/// SelectionItemPattern.IsSelected directly. Per Pioneer's confirmed
/// tab-rendering pattern (FieldMap.cs: a Tab control holds every TabItem
/// but only the SELECTED pane), the TabItem itself stays present and
/// readable regardless of which outer tab is active — only its
/// IsSelected value changes — so this signal alone is normally
/// sufficient and never needs the secondary fallback below.
///
/// SECONDARY — FieldMap.OuterCommonPaneAutomationId (cntCommonTab): only
/// consulted when the outer Common TabItem can't be located at all (e.g.
/// SelectionItemPattern unsupported on this control). Existing +
/// onscreen -&gt; Common is active; existing-but-offscreen or absent -&gt;
/// Common is not active.
///
/// Neither signal resolving THIS tick falls back to CommonTabState.Unknown
/// — UNLESS at least one of them has resolved successfully at some
/// earlier tick this attach-session (tracked via the same cache's
/// "ever seen" bookkeeping FieldReader's retry-on-suspicion logic already
/// uses), in which case a momentary read failure is treated as a
/// confirmed Off rather than reverting to the old proxy fallback (a
/// worse regression than briefly under-trusting one tick).
/// </summary>
public static class CommonTabGate
{
    private const string TabItemCacheKey = "__outerCommonTabItem";
    private const string PaneCacheKey = "__" + FieldMap.OuterCommonPaneAutomationId;

    /// <summary>
    /// Same per-window-handle cache class FieldReader.ElementCache uses
    /// (see EnteredFieldElementCache.cs) — a separate static instance
    /// (not FieldReader's own) so this gate's cache lifetime and the
    /// entered-field cache's lifetime are independently reasoned about,
    /// even though both key by the same PioneerRx window handle and both
    /// auto-reset the moment that handle changes (EnsureWindow).
    /// </summary>
    private static readonly EnteredFieldElementCache<AutomationElement> Cache = new();

    /// <summary>
    /// Forces the next DetermineState call (for any window handle) to
    /// start from empty — mirrors FieldReader.InvalidateElementCache().
    /// Called from PioneerRxWindow.TryAttach's self-heal catch block
    /// (shared UIA3Automation session disposed/recreated) for the same
    /// reason FieldReader's own cache needs it there: a same-handle cache
    /// hit alone would NOT catch a torn-down automation session
    /// underneath an otherwise-unchanged HWND.
    /// </summary>
    public static void InvalidateCache() => Cache.Invalidate();

    /// <summary>
    /// Computes the current CommonTabState for the given attached
    /// PioneerRx window (<paramref name="walker"/> wraps its root
    /// element — see UiaTreeWalker.FindDescendantTabItemByNamePrefix/
    /// FindDescendantByAutomationId, which do the actual searches; this
    /// class owns only the caching + layering decision on top). Never
    /// throws — any UIA failure at any step is treated as "this signal
    /// didn't resolve", falling through to the next layer or ultimately
    /// Unknown.
    /// </summary>
    public static CommonTabState DetermineState(UiaTreeWalker walker, IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            // Same caveat as FieldReader.ResolveElement: without a real
            // handle there's nothing safe to key a cache off (a
            // documented rare edge case — see PioneerRxWindow.
            // PickBestCandidate) — resolve fresh, uncached, every call.
            return DetermineUncached(walker);
        }

        var fromTabItem = TryReadFromOuterTabItem(walker, windowHandle);
        if (fromTabItem.HasValue) return fromTabItem.Value ? CommonTabState.On : CommonTabState.Off;

        var fromPane = TryReadFromCommonPane(walker, windowHandle);
        if (fromPane.HasValue) return fromPane.Value ? CommonTabState.On : CommonTabState.Off;

        var everSeenEither = Cache.HasEverReadNonBlank(windowHandle, TabItemCacheKey)
            || Cache.HasEverReadNonBlank(windowHandle, PaneCacheKey);

        return everSeenEither ? CommonTabState.Off : CommonTabState.Unknown;
    }

    private static CommonTabState DetermineUncached(UiaTreeWalker walker)
    {
        var tabItem = FindOuterCommonTabItem(walker);
        if (tabItem is not null)
        {
            var isSelected = UiaTreeWalker.ReadIsSelected(tabItem);
            if (isSelected.HasValue) return isSelected.Value ? CommonTabState.On : CommonTabState.Off;
        }

        var pane = FindCommonPane(walker);
        if (pane is not null)
        {
            var isOffscreen = UiaTreeWalker.ReadIsOffscreen(pane);
            if (isOffscreen.HasValue) return isOffscreen.Value ? CommonTabState.Off : CommonTabState.On;
        }

        return CommonTabState.Unknown;
    }

    /// <summary>
    /// PRIMARY signal. Resolves the outer Common TabItem (cached, or a
    /// fresh single search on a cache miss), reads IsSelected, and — only
    /// if that read fails on a CACHED element (stale/gone) — evicts it
    /// and searches exactly once more before giving up on this signal for
    /// this tick. Returns null if the TabItem can't be found/read at all.
    /// </summary>
    private static bool? TryReadFromOuterTabItem(UiaTreeWalker walker, IntPtr windowHandle)
    {
        var element = ResolveCached(windowHandle, TabItemCacheKey, () => FindOuterCommonTabItem(walker));
        if (element is null) return null;

        var isSelected = UiaTreeWalker.ReadIsSelected(element);
        if (isSelected.HasValue)
        {
            Cache.MarkNonBlank(windowHandle, TabItemCacheKey);
            return isSelected;
        }

        // Cached element went stale — drop it and re-find exactly once
        // (per-tick cost discipline: never re-search proactively).
        Cache.InvalidateField(windowHandle, TabItemCacheKey);
        var refound = FindOuterCommonTabItem(walker);
        if (refound is null) return null;

        Cache.SetElement(windowHandle, TabItemCacheKey, refound);
        var reread = UiaTreeWalker.ReadIsSelected(refound);
        if (reread.HasValue) Cache.MarkNonBlank(windowHandle, TabItemCacheKey);
        return reread;
    }

    /// <summary>SECONDARY signal — same cached/re-find-once-on-stale shape as TryReadFromOuterTabItem, over FieldMap.OuterCommonPaneAutomationId + IsOffscreen instead of the TabItem + SelectionItemPattern.</summary>
    private static bool? TryReadFromCommonPane(UiaTreeWalker walker, IntPtr windowHandle)
    {
        var element = ResolveCached(windowHandle, PaneCacheKey, () => FindCommonPane(walker));
        if (element is null) return null;

        var isOffscreen = UiaTreeWalker.ReadIsOffscreen(element);
        if (isOffscreen.HasValue)
        {
            Cache.MarkNonBlank(windowHandle, PaneCacheKey);
            return !isOffscreen.Value;
        }

        Cache.InvalidateField(windowHandle, PaneCacheKey);
        var refound = FindCommonPane(walker);
        if (refound is null) return null;

        Cache.SetElement(windowHandle, PaneCacheKey, refound);
        var reread = UiaTreeWalker.ReadIsOffscreen(refound);
        if (!reread.HasValue) return null;

        Cache.MarkNonBlank(windowHandle, PaneCacheKey);
        return !reread.Value;
    }

    private static AutomationElement? ResolveCached(IntPtr windowHandle, string cacheKey, Func<AutomationElement?> findFresh)
    {
        if (Cache.TryGetElement(windowHandle, cacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        var found = findFresh();
        if (found is not null)
        {
            Cache.SetElement(windowHandle, cacheKey, found);
        }

        return found;
    }

    private static AutomationElement? FindOuterCommonTabItem(UiaTreeWalker walker) =>
        walker.FindDescendantTabItemByNamePrefix(FieldMap.OuterCommonTabNamePrefix);

    private static AutomationElement? FindCommonPane(UiaTreeWalker walker) =>
        walker.FindDescendantByAutomationId(FieldMap.OuterCommonPaneAutomationId);
}
