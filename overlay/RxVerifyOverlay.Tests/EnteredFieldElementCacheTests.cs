using System;
using RxVerifyOverlay.Uia;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for EnteredFieldElementCache (Uia/EnteredFieldElementCache.cs)
/// — the invalidation/bookkeeping logic backing FieldReader.ReadEntered's
/// latency fix (uia-read-latency branch: caching each entered field's
/// located UIA element per attached window, instead of re-walking the
/// whole tree for all ~14 fields on every refresh). Uses a plain dummy
/// reference type as TElement so this needs no FlaUI/UIA/Windows runtime
/// at all.
/// </summary>
public class EnteredFieldElementCacheTests
{
    // Stand-in for FlaUI's AutomationElement — the cache is generic
    // specifically so tests never need a live UIA element.
    private sealed class DummyElement
    {
        public string Tag { get; init; } = "";
    }

    private static readonly IntPtr HandleA = new(1);
    private static readonly IntPtr HandleB = new(2);
    private const string FieldA = "uxPatientQuickSearch";
    private const string FieldB = "uxPrescriberQuickSearch";

    [Fact]
    public void TryGetElementMissesWhenNothingHasEverBeenCached()
    {
        var cache = new EnteredFieldElementCache<DummyElement>();

        var hit = cache.TryGetElement(HandleA, FieldA, out var element);

        Assert.False(hit);
        Assert.Null(element);
    }

    [Fact]
    public void TryGetElementHitsAfterSetForTheSameWindowAndField()
    {
        var cache = new EnteredFieldElementCache<DummyElement>();
        var stored = new DummyElement { Tag = "patient" };
        cache.SetElement(HandleA, FieldA, stored);

        var hit = cache.TryGetElement(HandleA, FieldA, out var element);

        Assert.True(hit);
        Assert.Same(stored, element);
    }

    [Fact]
    public void DifferentFieldsOnTheSameWindowAreCachedIndependently()
    {
        var cache = new EnteredFieldElementCache<DummyElement>();
        var patientElement = new DummyElement { Tag = "patient" };
        var prescriberElement = new DummyElement { Tag = "prescriber" };

        cache.SetElement(HandleA, FieldA, patientElement);
        cache.SetElement(HandleA, FieldB, prescriberElement);

        Assert.True(cache.TryGetElement(HandleA, FieldA, out var gotA));
        Assert.Same(patientElement, gotA);
        Assert.True(cache.TryGetElement(HandleA, FieldB, out var gotB));
        Assert.Same(prescriberElement, gotB);
    }

    [Fact]
    public void ADifferentWindowHandleClearsEveryPreviouslyCachedField()
    {
        // PioneerRx's window layout is static PER SESSION — a different
        // window instance (different hwnd) may not even still exist, so
        // nothing from it is safe to hand back for a new window.
        var cache = new EnteredFieldElementCache<DummyElement>();
        cache.SetElement(HandleA, FieldA, new DummyElement { Tag = "stale" });
        cache.MarkNonBlank(HandleA, FieldA);

        var hitAfterWindowChange = cache.TryGetElement(HandleB, FieldA, out var element);

        Assert.False(hitAfterWindowChange);
        Assert.Null(element);
        Assert.False(cache.HasEverReadNonBlank(HandleB, FieldA));
    }

    [Fact]
    public void SwitchingBackToAPreviousWindowHandleStartsFreshNotRestored()
    {
        // Only ONE window's worth of state is ever kept (mirrors Ocr/
        // CaptureRegionCache.cs's single-slot design) — going A -> B -> A
        // does NOT magically restore A's old cache; it's gone.
        var cache = new EnteredFieldElementCache<DummyElement>();
        cache.SetElement(HandleA, FieldA, new DummyElement { Tag = "first-a" });

        cache.SetElement(HandleB, FieldA, new DummyElement { Tag = "b" });

        var hit = cache.TryGetElement(HandleA, FieldA, out var element);

        Assert.False(hit);
        Assert.Null(element);
    }

    [Fact]
    public void InvalidateFieldRemovesOnlyThatFieldNotTheWholeWindow()
    {
        var cache = new EnteredFieldElementCache<DummyElement>();
        cache.SetElement(HandleA, FieldA, new DummyElement { Tag = "a" });
        cache.SetElement(HandleA, FieldB, new DummyElement { Tag = "b" });

        cache.InvalidateField(HandleA, FieldA);

        Assert.False(cache.TryGetElement(HandleA, FieldA, out _));
        Assert.True(cache.TryGetElement(HandleA, FieldB, out _));
    }

    [Fact]
    public void HasEverReadNonBlankStartsFalseAndFlipsOnceMarked()
    {
        var cache = new EnteredFieldElementCache<DummyElement>();

        Assert.False(cache.HasEverReadNonBlank(HandleA, FieldA));

        cache.MarkNonBlank(HandleA, FieldA);

        Assert.True(cache.HasEverReadNonBlank(HandleA, FieldA));
    }

    [Fact]
    public void HasEverReadNonBlankIsPerFieldNotSharedAcrossFields()
    {
        var cache = new EnteredFieldElementCache<DummyElement>();
        cache.MarkNonBlank(HandleA, FieldA);

        Assert.True(cache.HasEverReadNonBlank(HandleA, FieldA));
        Assert.False(cache.HasEverReadNonBlank(HandleA, FieldB));
    }

    [Fact]
    public void ADifferentWindowHandleResetsHasEverReadNonBlankToo()
    {
        var cache = new EnteredFieldElementCache<DummyElement>();
        cache.MarkNonBlank(HandleA, FieldA);

        // Touch the cache with a different handle (any call takes the
        // EnsureWindow reset path).
        cache.TryGetElement(HandleB, FieldA, out _);

        Assert.False(cache.HasEverReadNonBlank(HandleB, FieldA));
    }
}
