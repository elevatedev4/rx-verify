using System;
using System.Drawing;
using RxVerifyOverlay.Ocr;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for CaptureRegionCache/CaptureWindowSignature (Ocr/
/// CaptureRegionCache.cs) — the region-cache invalidation logic backing
/// EscriptImageCapture.ResolveCaptureRegion's latency fix (branch brief
/// item 3: cache the UIA-walk-resolved capture region per attached
/// window, invalidate on window/Rx/bounds change). Deliberately no UIA/
/// WPF types involved (System.IntPtr + System.Drawing.Rectangle only),
/// so this runs on any .NET host, Windows runtime or not.
/// </summary>
public class CaptureRegionCacheTests
{
    private static readonly Rectangle RegionA = new(10, 20, 300, 400);
    private static readonly Rectangle RegionB = new(50, 60, 500, 600);

    private static CaptureWindowSignature MakeSignature(
        int handle = 1,
        string? rxNumber = "123456",
        int left = 0, int top = 0, int width = 1000, int height = 800)
        => new(new IntPtr(handle), rxNumber, new Rectangle(left, top, width, height));

    [Fact]
    public void TryGetMissesWhenNothingHasEverBeenSet()
    {
        var cache = new CaptureRegionCache();

        var hit = cache.TryGet(MakeSignature(), out var region);

        Assert.False(hit);
        Assert.Equal(Rectangle.Empty, region);
    }

    [Fact]
    public void TryGetHitsOnAnExactlyMatchingSignature()
    {
        var cache = new CaptureRegionCache();
        var signature = MakeSignature();
        cache.Set(signature, RegionA);

        var hit = cache.TryGet(signature, out var region);

        Assert.True(hit);
        Assert.Equal(RegionA, region);
    }

    [Fact]
    public void TryGetHitsOnASeparatelyConstructedButEqualSignature()
    {
        // Same field values via two independent MakeSignature calls —
        // proves the cache compares by value (record struct equality),
        // not by reference, since ResolveCaptureRegion builds a fresh
        // CaptureWindowSignature on every call.
        var cache = new CaptureRegionCache();
        cache.Set(MakeSignature(), RegionA);

        var hit = cache.TryGet(MakeSignature(), out var region);

        Assert.True(hit);
        Assert.Equal(RegionA, region);
    }

    [Fact]
    public void TryGetMissesOnADifferentWindowHandle()
    {
        var cache = new CaptureRegionCache();
        cache.Set(MakeSignature(handle: 1), RegionA);

        var hit = cache.TryGet(MakeSignature(handle: 2), out var region);

        Assert.False(hit);
        Assert.Equal(Rectangle.Empty, region);
    }

    [Fact]
    public void TryGetMissesOnADifferentRxNumber()
    {
        var cache = new CaptureRegionCache();
        cache.Set(MakeSignature(rxNumber: "123456"), RegionA);

        var hit = cache.TryGet(MakeSignature(rxNumber: "999999"), out _);

        Assert.False(hit);
    }

    [Fact]
    public void TryGetMissesOnDifferentWindowBounds()
    {
        // Same window/Rx, but the PioneerRx window moved or was resized —
        // must invalidate even though handle + RxNumber are unchanged
        // (branch brief item 3: "invalidate on ... resize").
        var cache = new CaptureRegionCache();
        cache.Set(MakeSignature(width: 1000, height: 800), RegionA);

        var hit = cache.TryGet(MakeSignature(width: 1200, height: 800), out _);

        Assert.False(hit);
    }

    [Fact]
    public void TryGetMissesWhenRxNumberChangesFromNullToAValue()
    {
        // A "New Rx" screen with no number assigned yet parses to a null/
        // fallback RxNumber (see PioneerRxWindow.ExtractRxNumber) — once
        // the Rx is saved and gets a real number, that must count as a
        // change even though the window handle is identical.
        var cache = new CaptureRegionCache();
        cache.Set(MakeSignature(rxNumber: null), RegionA);

        var hit = cache.TryGet(MakeSignature(rxNumber: "123456"), out _);

        Assert.False(hit);
    }

    [Fact]
    public void SetOverwritesAPreviouslyCachedRegionForANewSignature()
    {
        var cache = new CaptureRegionCache();
        cache.Set(MakeSignature(handle: 1), RegionA);
        cache.Set(MakeSignature(handle: 2), RegionB);

        Assert.False(cache.TryGet(MakeSignature(handle: 1), out _));
        Assert.True(cache.TryGet(MakeSignature(handle: 2), out var region));
        Assert.Equal(RegionB, region);
    }

    [Fact]
    public void InvalidateForcesTheNextTryGetToMissEvenForTheSameSignature()
    {
        var cache = new CaptureRegionCache();
        var signature = MakeSignature();
        cache.Set(signature, RegionA);

        cache.Invalidate();

        Assert.False(cache.TryGet(signature, out var region));
        Assert.Equal(Rectangle.Empty, region);
    }
}
