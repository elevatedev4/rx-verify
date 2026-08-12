using System;
using System.Collections.Generic;
using RxVerifyOverlay.Uia;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for CommonTabGate&lt;TElement&gt; (Uia/CommonTabGate.cs) —
/// the tri-state (On/Off/Unknown) orchestration behind "is PioneerRx's
/// outer Common tab active right now". Generic over TElement specifically
/// so this whole state machine — caching, stale-element re-find-once, and
/// the BLOCKER-1 fix below — is testable with a plain dummy element type
/// and fully-controlled fake reads, no FlaUI/UIA/Windows runtime at all
/// (same reasoning as EnteredFieldElementCacheTests.cs, which this
/// class's cache is built directly on).
/// </summary>
public class CommonTabGateTests
{
    // Stand-in for FlaUI's AutomationElement.
    private sealed class DummyElement
    {
        public string Tag { get; init; } = "";
        public override string ToString() => Tag;
    }

    private static readonly IntPtr Handle = new(1);
    private static readonly IntPtr OtherHandle = new(2);

    /// <summary>Returns each element in sequence on successive calls, repeating the last one for any call beyond the array — models FindDescendant* being called at most twice per tick (initial + one re-find-on-stale).</summary>
    private static Func<DummyElement?> FindSequence(params DummyElement?[] elements)
    {
        var index = 0;
        return () => elements.Length == 0 ? null : elements[Math.Min(index++, elements.Length - 1)];
    }

    /// <summary>A find delegate that always returns the same element and counts how many times it was invoked, so tests can assert caching actually avoided a re-find.</summary>
    private sealed class CountingFind
    {
        private readonly DummyElement? _element;
        public int CallCount { get; private set; }
        public CountingFind(DummyElement? element) => _element = element;
        public DummyElement? Find()
        {
            CallCount++;
            return _element;
        }
    }

    /// <summary>Read delegate keyed by element reference, so a test can give different (or no) elements different results, and mutate results between simulated ticks.</summary>
    private static Func<DummyElement, bool?> ReadByReference(Dictionary<DummyElement, bool?> results)
    {
        return element => results.TryGetValue(element, out var v) ? v : null;
    }

    private static Func<DummyElement?> NeverCalledFind() => () => throw new InvalidOperationException("SECONDARY find should not run when PRIMARY already resolved.");
    private static Func<DummyElement, bool?> NeverCalledRead() => _ => throw new InvalidOperationException("SECONDARY read should not run when PRIMARY already resolved.");

    // ------------------------------------------------------------------
    // Normal On/Off, PRIMARY signal
    // ------------------------------------------------------------------

    [Fact]
    public void OnWhenOuterTabItemIsSelected()
    {
        var gate = new CommonTabGate<DummyElement>();
        var tabItem = new DummyElement { Tag = "common-tabitem" };

        var state = gate.DetermineState(
            Handle,
            findOuterCommonTabItem: () => tabItem,
            readIsSelected: _ => true,
            findCommonPane: () => null,
            readIsOnscreen: _ => null);

        Assert.Equal(CommonTabState.On, state);
    }

    [Fact]
    public void OffWhenOuterTabItemIsNotSelected()
    {
        var gate = new CommonTabGate<DummyElement>();
        var tabItem = new DummyElement { Tag = "common-tabitem" };

        var state = gate.DetermineState(
            Handle,
            findOuterCommonTabItem: () => tabItem,
            readIsSelected: _ => false,
            findCommonPane: () => null,
            readIsOnscreen: _ => null);

        Assert.Equal(CommonTabState.Off, state);
    }

    [Fact]
    public void PrimaryResolvingMeansSecondaryIsNeverConsulted()
    {
        var gate = new CommonTabGate<DummyElement>();
        var tabItem = new DummyElement { Tag = "common-tabitem" };

        var state = gate.DetermineState(
            Handle,
            findOuterCommonTabItem: () => tabItem,
            readIsSelected: _ => true,
            findCommonPane: NeverCalledFind(),
            readIsOnscreen: NeverCalledRead());

        Assert.Equal(CommonTabState.On, state);
    }

    // ------------------------------------------------------------------
    // SECONDARY signal (only consulted when PRIMARY can't be found)
    // ------------------------------------------------------------------

    [Fact]
    public void OnWhenTabItemNotFoundButPaneIsOnscreen()
    {
        var gate = new CommonTabGate<DummyElement>();
        var pane = new DummyElement { Tag = "cntCommonTab" };

        var state = gate.DetermineState(
            Handle,
            findOuterCommonTabItem: () => null,
            readIsSelected: NeverCalledRead(),
            findCommonPane: () => pane,
            readIsOnscreen: _ => true);

        Assert.Equal(CommonTabState.On, state);
    }

    [Fact]
    public void OffWhenTabItemNotFoundAndPaneIsOffscreen()
    {
        var gate = new CommonTabGate<DummyElement>();
        var pane = new DummyElement { Tag = "cntCommonTab" };

        var state = gate.DetermineState(
            Handle,
            findOuterCommonTabItem: () => null,
            readIsSelected: NeverCalledRead(),
            findCommonPane: () => pane,
            readIsOnscreen: _ => false);

        Assert.Equal(CommonTabState.Off, state);
    }

    // ------------------------------------------------------------------
    // Unknown fallback — neither signal found at all
    // ------------------------------------------------------------------

    [Fact]
    public void UnknownWhenNeitherTabItemNorPaneCanBeFound()
    {
        var gate = new CommonTabGate<DummyElement>();

        var state = gate.DetermineState(
            Handle,
            findOuterCommonTabItem: () => null,
            readIsSelected: NeverCalledRead(),
            findCommonPane: () => null,
            readIsOnscreen: NeverCalledRead());

        Assert.Equal(CommonTabState.Unknown, state);
    }

    // ------------------------------------------------------------------
    // BLOCKER 1 (review): a double-failure THIS tick must always be
    // Unknown, regardless of whether either signal resolved successfully
    // at an EARLIER tick this attach-session — never escalated to a
    // confirmed Off. Covers all three histories the reviewer named.
    // ------------------------------------------------------------------

    [Fact]
    public void SeenOnThenBothFail_ReturnsUnknownNotOff()
    {
        var gate = new CommonTabGate<DummyElement>();
        var tabItemResults = new Dictionary<DummyElement, bool?>();
        var tick1TabItem = new DummyElement { Tag = "tick1-tabitem" };
        tabItemResults[tick1TabItem] = true;

        // Tick 1: resolves On via the TabItem — this is the "seen"
        // history, and caches tick1TabItem for tick 2 to reuse.
        var tick1 = gate.DetermineState(
            Handle,
            findOuterCommonTabItem: () => tick1TabItem,
            readIsSelected: ReadByReference(tabItemResults),
            findCommonPane: () => null,
            readIsOnscreen: _ => null);
        Assert.Equal(CommonTabState.On, tick1);

        // Tick 2: the CACHED tick1TabItem now reads null (stale/
        // COMException — mutating its own dictionary entry, not swapping
        // in a different element, since the gate reuses the cached
        // reference without calling findOuterCommonTabItem again on a
        // cache hit). The one permitted re-find also comes back empty
        // (findOuterCommonTabItem: () => null below), and the pane can't
        // be found either — a routine double-failure, not a real tab
        // switch.
        tabItemResults[tick1TabItem] = null;
        var tick2 = gate.DetermineState(
            Handle,
            findOuterCommonTabItem: () => null,
            readIsSelected: ReadByReference(tabItemResults),
            findCommonPane: () => null,
            readIsOnscreen: NeverCalledRead());

        Assert.Equal(CommonTabState.Unknown, tick2);
    }

    [Fact]
    public void SeenOffThenBothFail_ReturnsUnknownNotOff()
    {
        var gate = new CommonTabGate<DummyElement>();
        var tabItemResults = new Dictionary<DummyElement, bool?>();
        var tick1TabItem = new DummyElement { Tag = "tick1-tabitem" };
        tabItemResults[tick1TabItem] = false;

        // Tick 1: resolves Off via the TabItem — still a "seen" history,
        // just seen as Off rather than On.
        var tick1 = gate.DetermineState(
            Handle,
            findOuterCommonTabItem: () => tick1TabItem,
            readIsSelected: ReadByReference(tabItemResults),
            findCommonPane: () => null,
            readIsOnscreen: _ => null);
        Assert.Equal(CommonTabState.Off, tick1);

        // Tick 2: double-failure again (same stale-cached-element
        // mechanics as the seen-On case above) — must still be Unknown,
        // not a "re-confirmed" Off. Pre-fix behavior collapsed this to
        // Off purely because SOME earlier tick had resolved (regardless
        // of On/Off) — this is exactly the case that fix must not
        // regress.
        tabItemResults[tick1TabItem] = null;
        var tick2 = gate.DetermineState(
            Handle,
            findOuterCommonTabItem: () => null,
            readIsSelected: ReadByReference(tabItemResults),
            findCommonPane: () => null,
            readIsOnscreen: NeverCalledRead());

        Assert.Equal(CommonTabState.Unknown, tick2);
    }

    [Fact]
    public void NeverSeenThenFail_ReturnsUnknown()
    {
        // No prior tick at all — the very first read is a double-failure.
        // Was already Unknown before the fix; kept as an explicit
        // regression case per the reviewer's requested coverage.
        var gate = new CommonTabGate<DummyElement>();

        var state = gate.DetermineState(
            Handle,
            findOuterCommonTabItem: () => null,
            readIsSelected: NeverCalledRead(),
            findCommonPane: () => null,
            readIsOnscreen: NeverCalledRead());

        Assert.Equal(CommonTabState.Unknown, state);
    }

    // ------------------------------------------------------------------
    // Caching / stale-element re-find discipline
    // ------------------------------------------------------------------

    [Fact]
    public void SameWindowHandleReusesTheCachedTabItemAcrossTicksWithoutRefinding()
    {
        var gate = new CommonTabGate<DummyElement>();
        var tabItem = new DummyElement { Tag = "common-tabitem" };
        var find = new CountingFind(tabItem);
        var currentlySelected = true;

        var tick1 = gate.DetermineState(Handle, find.Find, _ => currentlySelected, () => null, _ => null);
        currentlySelected = false; // pharmacist switched off Common — no re-search needed to see this
        var tick2 = gate.DetermineState(Handle, find.Find, _ => currentlySelected, () => null, _ => null);

        Assert.Equal(CommonTabState.On, tick1);
        Assert.Equal(CommonTabState.Off, tick2);
        Assert.Equal(1, find.CallCount); // found once, re-read (not re-found) on tick 2
    }

    [Fact]
    public void StaleCachedElementIsEvictedAndRefoundExactlyOnce()
    {
        var gate = new CommonTabGate<DummyElement>();
        var staleElement = new DummyElement { Tag = "stale" };
        var freshElement = new DummyElement { Tag = "fresh" };
        var find = FindSequence(staleElement, freshElement);
        var results = new Dictionary<DummyElement, bool?>
        {
            [staleElement] = null, // simulates a COMException-guarded read returning null
            [freshElement] = true
        };

        var state = gate.DetermineState(Handle, find, ReadByReference(results), () => null, _ => null);

        Assert.Equal(CommonTabState.On, state); // recovered via the one re-find, not left as Unknown
    }

    [Fact]
    public void ADifferentWindowHandleDoesNotReuseThePreviousWindowsCache()
    {
        // EnteredFieldElementCache is single-slot (see its own class doc)
        // — a different handle resets the whole cache rather than
        // keeping per-handle entries side by side. Whichever mechanism,
        // the observable contract this gate needs is the same: window B
        // must never be handed window A's cached TabItem.
        var gate = new CommonTabGate<DummyElement>();
        var tabItemA = new DummyElement { Tag = "window-a" };
        var findA = new CountingFind(tabItemA);
        gate.DetermineState(Handle, findA.Find, _ => true, () => null, _ => null);

        var tabItemB = new DummyElement { Tag = "window-b" };
        var findB = new CountingFind(tabItemB);
        var state = gate.DetermineState(OtherHandle, findB.Find, _ => false, () => null, _ => null);

        Assert.Equal(CommonTabState.Off, state);
        Assert.Equal(1, findB.CallCount); // found fresh for window B, not skipped as a false cache hit
    }

    [Fact]
    public void InvalidateCacheForcesARefindOnTheNextCall()
    {
        var gate = new CommonTabGate<DummyElement>();
        var tabItem = new DummyElement { Tag = "common-tabitem" };
        var find = new CountingFind(tabItem);

        gate.DetermineState(Handle, find.Find, _ => true, () => null, _ => null);
        gate.InvalidateCache();
        gate.DetermineState(Handle, find.Find, _ => true, () => null, _ => null);

        Assert.Equal(2, find.CallCount); // self-heal path (PioneerRxWindow) must force a fresh find, not reuse a torn-down session's element
    }

    // ------------------------------------------------------------------
    // IntPtr.Zero (uncached) path — mirrors FieldReader.ResolveElement's
    // same caveat for a window whose native handle couldn't be read.
    // ------------------------------------------------------------------

    [Fact]
    public void UncachedPathStillResolvesOnWhenHandleIsZero()
    {
        var gate = new CommonTabGate<DummyElement>();
        var tabItem = new DummyElement { Tag = "common-tabitem" };

        var state = gate.DetermineState(
            IntPtr.Zero,
            findOuterCommonTabItem: () => tabItem,
            readIsSelected: _ => true,
            findCommonPane: () => null,
            readIsOnscreen: _ => null);

        Assert.Equal(CommonTabState.On, state);
    }

    [Fact]
    public void UncachedPathFallsBackToUnknownOnDoubleFailure()
    {
        var gate = new CommonTabGate<DummyElement>();

        var state = gate.DetermineState(
            IntPtr.Zero,
            findOuterCommonTabItem: () => null,
            readIsSelected: NeverCalledRead(),
            findCommonPane: () => null,
            readIsOnscreen: NeverCalledRead());

        Assert.Equal(CommonTabState.Unknown, state);
    }
}
