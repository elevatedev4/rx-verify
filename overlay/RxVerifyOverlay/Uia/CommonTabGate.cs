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
    /// Neither confirmed signal below resolved a definitive answer THIS
    /// tick — either this Pioneer version's tree shape differs from the
    /// two confirmed dumps, or (just as routinely) a UIA read hit a
    /// stale/disconnected element this one tick. Callers should fall back
    /// to whatever proxy they used before this gate existed rather than
    /// treat a momentary read failure as a confirmed answer either way —
    /// see CommonTabGate&lt;TElement&gt;.DetermineState's REVIEW FIX note.
    /// </summary>
    Unknown
}

/// <summary>
/// Layered, strongest-confirmed-signal-first orchestration behind
/// CommonTabState, matching FieldReader/EnteredFieldElementCache's own
/// "cache the located element per attach-session, re-find only when it
/// goes stale" latency discipline (see EnteredFieldElementCache.cs) —
/// this runs on every ~250ms tick, so a full-tree search every single
/// tick is not acceptable, only on a cache miss or a confirmed stale
/// element (ComException/element gone).
///
/// GENERIC OVER TElement (review fix — testability): mirrors
/// EnteredFieldElementCache&lt;TElement&gt;, which this class's cache is
/// built on directly. Every FlaUI-touching operation (find the outer
/// TabItem, read its selection state, find the Common pane, read its
/// onscreen state) is injected as a delegate rather than hardcoded to
/// AutomationElement/FlaUI calls, so RxVerifyOverlay.Tests/
/// CommonTabGateTests.cs can drive the ENTIRE state machine — caching,
/// stale-element re-find-once, and the tri-state decision itself — with
/// a plain dummy element type and fully-controlled fake reads, no FlaUI/
/// UIA/Windows runtime involved at all. See the non-generic CommonTabGate
/// facade below for the production entry point, which wires this up with
/// real UiaTreeWalker/FieldMap calls.
///
/// PRIMARY — the outer "Common" TabItem: found by
/// <c>findOuterCommonTabItem</c> (production: UiaTreeWalker.
/// FindDescendantTabItemByNamePrefix(FieldMap.OuterCommonTabNamePrefix) —
/// no confirmed AutomationId exists for the outer Tab control itself to
/// narrow the search by), its selection state read by
/// <c>readIsSelected</c> (production: UiaTreeWalker.ReadIsSelected, i.e.
/// SelectionItemPattern.IsSelected). Per Pioneer's confirmed
/// tab-rendering pattern (FieldMap.cs: a Tab control holds every TabItem
/// but only the SELECTED pane), the TabItem itself stays present and
/// readable regardless of which outer tab is active — only its selection
/// state changes — so this signal alone is normally sufficient and never
/// needs the secondary fallback below.
///
/// SECONDARY — the Common pane: found by <c>findCommonPane</c>
/// (production: UiaTreeWalker.FindDescendantByAutomationId(FieldMap.
/// OuterCommonPaneAutomationId), i.e. cntCommonTab), its onscreen state
/// read by <c>readIsOnscreen</c> (production: the NEGATION of
/// UiaTreeWalker.ReadIsOffscreen — see the facade's Negate helper). Only
/// consulted when the outer Common TabItem can't be located/read at all.
/// </summary>
public sealed class CommonTabGate<TElement> where TElement : class
{
    private const string TabItemCacheKey = "__outerCommonTabItem";
    private const string PaneCacheKey = "__commonPane";

    /// <summary>
    /// Same per-window-handle cache class FieldReader.ElementCache uses
    /// (see EnteredFieldElementCache.cs) — an instance field here (not a
    /// static, unlike FieldReader's own — see the facade below for why
    /// production wiring still gets ONE shared instance for the process)
    /// so RxVerifyOverlay.Tests/CommonTabGateTests.cs can construct a
    /// fresh, isolated CommonTabGate&lt;DummyElement&gt; per test with no
    /// cross-test state bleed.
    /// </summary>
    private readonly EnteredFieldElementCache<TElement> _cache = new();

    /// <summary>
    /// Forces the next DetermineState call (for any window handle) to
    /// start from empty — mirrors FieldReader.InvalidateElementCache().
    /// Called from PioneerRxWindow.TryAttach's self-heal catch block
    /// (shared UIA3Automation session disposed/recreated) for the same
    /// reason FieldReader's own cache needs it there: a same-handle cache
    /// hit alone would NOT catch a torn-down automation session
    /// underneath an otherwise-unchanged HWND.
    /// </summary>
    public void InvalidateCache() => _cache.Invalidate();

    /// <summary>
    /// Computes the current CommonTabState. Never throws — any read
    /// failure at any step (the injected delegates are expected to catch
    /// their own FlaUI/UIA exceptions and return null, same contract as
    /// UiaTreeWalker.ReadIsSelected/ReadIsOffscreen) is treated as "this
    /// signal didn't resolve", falling through to the next layer or
    /// ultimately Unknown.
    /// </summary>
    public CommonTabState DetermineState(
        IntPtr windowHandle,
        Func<TElement?> findOuterCommonTabItem,
        Func<TElement, bool?> readIsSelected,
        Func<TElement?> findCommonPane,
        Func<TElement, bool?> readIsOnscreen)
    {
        if (windowHandle == IntPtr.Zero)
        {
            // Same caveat as FieldReader.ResolveElement: without a real
            // handle there's nothing safe to key a cache off (a
            // documented rare edge case — see PioneerRxWindow.
            // PickBestCandidate) — resolve fresh, uncached, every call.
            return DetermineUncached(findOuterCommonTabItem, readIsSelected, findCommonPane, readIsOnscreen);
        }

        var fromTabItem = TryRead(windowHandle, TabItemCacheKey, findOuterCommonTabItem, readIsSelected);
        if (fromTabItem.HasValue) return fromTabItem.Value ? CommonTabState.On : CommonTabState.Off;

        var fromPane = TryRead(windowHandle, PaneCacheKey, findCommonPane, readIsOnscreen);
        if (fromPane.HasValue) return fromPane.Value ? CommonTabState.On : CommonTabState.Off;

        // REVIEW FIX (blocker — a tick where BOTH signals fail to resolve
        // must NEVER be escalated to a confirmed Off just because EITHER
        // one resolved successfully at some EARLIER tick this
        // attach-session): a stale-element/COMException read failure is
        // routine in this codebase (see FieldReader's own retry-on-
        // suspicion patterns, which exist precisely because this happens
        // regularly, not exceptionally) — treating a momentary double-
        // failure as "confirmed Off" would hide the verdict boxes on a
        // transient UIA hiccup even though the pharmacist never left
        // Common. Always Unknown here, UNCONDITIONALLY, regardless of
        // history: the caller's own forgiving hasResolvableFieldRects
        // proxy (IntegratedVisibilityGate.ShouldShowBoxes) is the correct
        // fallback for "couldn't tell this tick" — never a hide decision
        // on its own.
        return CommonTabState.Unknown;
    }

    private static CommonTabState DetermineUncached(
        Func<TElement?> findOuterCommonTabItem,
        Func<TElement, bool?> readIsSelected,
        Func<TElement?> findCommonPane,
        Func<TElement, bool?> readIsOnscreen)
    {
        var tabItem = findOuterCommonTabItem();
        if (tabItem is not null)
        {
            var isSelected = readIsSelected(tabItem);
            if (isSelected.HasValue) return isSelected.Value ? CommonTabState.On : CommonTabState.Off;
        }

        var pane = findCommonPane();
        if (pane is not null)
        {
            var isOnscreen = readIsOnscreen(pane);
            if (isOnscreen.HasValue) return isOnscreen.Value ? CommonTabState.On : CommonTabState.Off;
        }

        return CommonTabState.Unknown;
    }

    /// <summary>
    /// Resolves the cached-or-freshly-found element for <paramref name="cacheKey"/>
    /// and reads its value via <paramref name="read"/>. If that read
    /// fails on a CACHED element (stale/gone — <paramref name="read"/>
    /// returns null), the cache entry is evicted and <paramref name="find"/>
    /// is called exactly once more before giving up on this signal for
    /// this tick (per-tick cost discipline: never re-search proactively).
    /// Returns null if the element can't be found/read at all.
    /// </summary>
    private bool? TryRead(IntPtr windowHandle, string cacheKey, Func<TElement?> find, Func<TElement, bool?> read)
    {
        var element = ResolveCached(windowHandle, cacheKey, find);
        if (element is null) return null;

        var value = read(element);
        if (value.HasValue)
        {
            // NIT (review) — MarkNonBlank/HasEverReadNonBlank were named
            // for FieldReader's original "has this text field ever had
            // real typed content" use; reused here to mean something
            // narrower: "has this signal ever produced a DEFINITIVE
            // (non-null) read this attach-session". Kept as forward-
            // looking instrumentation only — the DetermineState decision
            // above deliberately does NOT branch on it (see that method's
            // REVIEW FIX note), so today this is bookkeeping with no
            // behavioral effect, not a load-bearing signal.
            _cache.MarkNonBlank(windowHandle, cacheKey);
            return value;
        }

        // Cached element went stale — drop it and re-find exactly once.
        _cache.InvalidateField(windowHandle, cacheKey);
        var refound = find();
        if (refound is null) return null;

        _cache.SetElement(windowHandle, cacheKey, refound);
        var reread = read(refound);
        if (reread.HasValue) _cache.MarkNonBlank(windowHandle, cacheKey);
        return reread;
    }

    private TElement? ResolveCached(IntPtr windowHandle, string cacheKey, Func<TElement?> find)
    {
        if (_cache.TryGetElement(windowHandle, cacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        var found = find();
        if (found is not null)
        {
            _cache.SetElement(windowHandle, cacheKey, found);
        }

        return found;
    }
}

/// <summary>
/// Production entry point — wires CommonTabGate&lt;AutomationElement&gt;
/// up with real UiaTreeWalker/FieldMap calls. ONE shared instance for the
/// process (static, mirrors FieldReader.ElementCache) since this overlay
/// only ever tracks one attached PioneerRx window at a time.
/// </summary>
public static class CommonTabGate
{
    private static readonly CommonTabGate<AutomationElement> Instance = new();

    /// <summary>See CommonTabGate&lt;TElement&gt;.InvalidateCache — called from PioneerRxWindow.TryAttach's self-heal catch block.</summary>
    public static void InvalidateCache() => Instance.InvalidateCache();

    /// <summary>See CommonTabGate&lt;TElement&gt;.DetermineState. <paramref name="walker"/> wraps the attached PioneerRx window's root element.</summary>
    public static CommonTabState DetermineState(UiaTreeWalker walker, IntPtr windowHandle)
    {
        return Instance.DetermineState(
            windowHandle,
            findOuterCommonTabItem: () => walker.FindDescendantTabItemByNamePrefix(FieldMap.OuterCommonTabNamePrefix),
            readIsSelected: UiaTreeWalker.ReadIsSelected,
            findCommonPane: () => walker.FindDescendantByAutomationId(FieldMap.OuterCommonPaneAutomationId),
            readIsOnscreen: element => Negate(UiaTreeWalker.ReadIsOffscreen(element)));
    }

    /// <summary>UiaTreeWalker.ReadIsOffscreen is naturally phrased as "is it offscreen"; the pane signal needs "is it onscreen" (see class doc SECONDARY) — a plain tri-state negation, never collapsing null to a definite value.</summary>
    private static bool? Negate(bool? value) => value.HasValue ? !value.Value : null;
}
