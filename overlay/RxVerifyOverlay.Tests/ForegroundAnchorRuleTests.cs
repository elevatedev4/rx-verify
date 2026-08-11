using System;
using System.Drawing;
using RxVerifyOverlay.Integrated;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for ForegroundAnchorRule (Integrated/ForegroundAnchorRule.cs)
/// — the pure decision behind the round-5 addendum fix ("the little
/// overlay box is jumping around every time Pioneer opens a new little
/// popup window"): anchor to a popup's ROOT OWNER when one exists and is
/// sane, otherwise fall back to the foreground window itself.
/// </summary>
public class ForegroundAnchorRuleTests
{
    private static readonly IntPtr ForegroundHandle = new(1);
    private static readonly IntPtr OwnerHandle = new(2);
    private static readonly Rectangle ForegroundBounds = new(500, 500, 200, 100); // a small popup, e.g.
    private static readonly Rectangle OwnerBounds = new(0, 0, 1928, 1040); // the main shell, maximized

    [Fact]
    public void AnchorsToTheOwnerWhenOneExistsWithASaneRect()
    {
        var anchor = ForegroundAnchorRule.Choose(ForegroundHandle, ForegroundBounds, OwnerHandle, OwnerBounds);

        Assert.Equal(OwnerHandle, anchor.Handle);
        Assert.Equal(OwnerBounds, anchor.Bounds);
    }

    [Fact]
    public void FallsBackToForegroundWhenThereIsNoOwner()
    {
        // The foreground window IS already the top-level shell — no
        // owner at all (GetAncestor returned IntPtr.Zero) — behaves
        // exactly like before this fix.
        var anchor = ForegroundAnchorRule.Choose(ForegroundHandle, ForegroundBounds, IntPtr.Zero, ownerBounds: null);

        Assert.Equal(ForegroundHandle, anchor.Handle);
        Assert.Equal(ForegroundBounds, anchor.Bounds);
    }

    [Fact]
    public void FallsBackToForegroundWhenTheOwnerRectCouldNotBeRead()
    {
        // GetWindowRect on the owner failed — ownerBounds is null even
        // though a real owner HANDLE was found.
        var anchor = ForegroundAnchorRule.Choose(ForegroundHandle, ForegroundBounds, OwnerHandle, ownerBounds: null);

        Assert.Equal(ForegroundHandle, anchor.Handle);
        Assert.Equal(ForegroundBounds, anchor.Bounds);
    }

    [Fact]
    public void FallsBackToForegroundWhenTheOwnerRectIsDegenerate()
    {
        // GetWindowRect "succeeded" but reported a zero-size rect — never
        // trust that over a real foreground rect.
        var degenerate = new Rectangle(0, 0, 0, 0);

        var anchor = ForegroundAnchorRule.Choose(ForegroundHandle, ForegroundBounds, OwnerHandle, degenerate);

        Assert.Equal(ForegroundHandle, anchor.Handle);
        Assert.Equal(ForegroundBounds, anchor.Bounds);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(-5, 10)]
    public void SaneWindowRectRequiresPositiveWidthAndHeight(int width, int height)
    {
        Assert.False(ForegroundAnchorRule.IsSaneWindowRect(new Rectangle(0, 0, width, height)));
    }

    [Fact]
    public void SaneWindowRectAcceptsAnyPositiveSize()
    {
        Assert.True(ForegroundAnchorRule.IsSaneWindowRect(new Rectangle(0, 0, 1, 1)));
    }
}
