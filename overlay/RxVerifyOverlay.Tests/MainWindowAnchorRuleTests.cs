using System;
using System.Drawing;
using RxVerifyOverlay.Integrated;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for MainWindowAnchorRule (Integrated/MainWindowAnchorRule.cs)
/// — the pure decision behind round 7's fix ("the little overlay box is
/// jumping around every time Pioneer opens a new little popup window ...
/// it needs to stay put at the top-right of the MAIN window"):
/// positively identify PioneerRx's own main window (maximized wins,
/// largest-of-several, largest-visible fallback) instead of deriving it
/// from whatever's foreground, and stick to it once found.
/// </summary>
public class MainWindowAnchorRuleTests
{
    private static readonly IntPtr HandleA = new(1);
    private static readonly IntPtr HandleB = new(2);
    private static readonly IntPtr HandleC = new(3);

    // ------------------------------------------------------------------
    // Choose (fresh selection, no memory of a previous pick)
    // ------------------------------------------------------------------

    [Fact]
    public void MaximizedCandidateWinsOverLargerNonMaximizedCandidate()
    {
        var maximizedButSmaller = new MainWindowAnchorRule.Candidate(HandleA, IsVisible: true, IsMinimized: false, IsMaximized: true, new Rectangle(0, 0, 1000, 800));
        var restoredButBigger = new MainWindowAnchorRule.Candidate(HandleB, IsVisible: true, IsMinimized: false, IsMaximized: false, new Rectangle(0, 0, 5000, 5000));

        var anchor = MainWindowAnchorRule.Choose(new[] { restoredButBigger, maximizedButSmaller });

        Assert.Equal(HandleA, anchor!.Value.Handle);
        Assert.Equal(maximizedButSmaller.Bounds, anchor.Value.Bounds);
    }

    [Fact]
    public void LargestOfSeveralMaximizedCandidatesWins()
    {
        var smallerMaximized = new MainWindowAnchorRule.Candidate(HandleA, IsVisible: true, IsMinimized: false, IsMaximized: true, new Rectangle(0, 0, 1920, 1040));
        var biggerMaximized = new MainWindowAnchorRule.Candidate(HandleB, IsVisible: true, IsMinimized: false, IsMaximized: true, new Rectangle(0, 0, 3840, 2160));

        var anchor = MainWindowAnchorRule.Choose(new[] { smallerMaximized, biggerMaximized });

        Assert.Equal(HandleB, anchor!.Value.Handle);
        Assert.Equal(biggerMaximized.Bounds, anchor.Value.Bounds);
    }

    [Fact]
    public void FallsBackToLargestVisibleCandidateWhenNoneAreMaximized()
    {
        var smaller = new MainWindowAnchorRule.Candidate(HandleA, IsVisible: true, IsMinimized: false, IsMaximized: false, new Rectangle(100, 100, 400, 300));
        var bigger = new MainWindowAnchorRule.Candidate(HandleB, IsVisible: true, IsMinimized: false, IsMaximized: false, new Rectangle(0, 0, 1920, 1040));

        var anchor = MainWindowAnchorRule.Choose(new[] { smaller, bigger });

        Assert.Equal(HandleB, anchor!.Value.Handle);
        Assert.Equal(bigger.Bounds, anchor.Value.Bounds);
    }

    [Fact]
    public void SkipsMinimizedCandidatesEvenIfMaximizedFlagIsSet()
    {
        // A window can report IsZoomed==true while minimized in some
        // Win32 edge cases (it remembers its pre-minimize state) — never
        // trust that over IsIconic.
        var minimizedMaximized = new MainWindowAnchorRule.Candidate(HandleA, IsVisible: true, IsMinimized: true, IsMaximized: true, new Rectangle(0, 0, 1920, 1040));
        var normalVisible = new MainWindowAnchorRule.Candidate(HandleB, IsVisible: true, IsMinimized: false, IsMaximized: false, new Rectangle(0, 0, 800, 600));

        var anchor = MainWindowAnchorRule.Choose(new[] { minimizedMaximized, normalVisible });

        Assert.Equal(HandleB, anchor!.Value.Handle);
    }

    [Fact]
    public void SkipsInvisibleCandidates()
    {
        var invisible = new MainWindowAnchorRule.Candidate(HandleA, IsVisible: false, IsMinimized: false, IsMaximized: true, new Rectangle(0, 0, 1920, 1040));
        var visible = new MainWindowAnchorRule.Candidate(HandleB, IsVisible: true, IsMinimized: false, IsMaximized: false, new Rectangle(0, 0, 800, 600));

        var anchor = MainWindowAnchorRule.Choose(new[] { invisible, visible });

        Assert.Equal(HandleB, anchor!.Value.Handle);
    }

    [Fact]
    public void SkipsCandidatesWithADegenerateRectEvenIfMaximizedAndVisible()
    {
        var degenerate = new MainWindowAnchorRule.Candidate(HandleA, IsVisible: true, IsMinimized: false, IsMaximized: true, new Rectangle(0, 0, 0, 0));
        var normal = new MainWindowAnchorRule.Candidate(HandleB, IsVisible: true, IsMinimized: false, IsMaximized: false, new Rectangle(0, 0, 800, 600));

        var anchor = MainWindowAnchorRule.Choose(new[] { degenerate, normal });

        Assert.Equal(HandleB, anchor!.Value.Handle);
    }

    [Fact]
    public void EmptyCandidatesProducesNoAnchor()
    {
        var anchor = MainWindowAnchorRule.Choose(Array.Empty<MainWindowAnchorRule.Candidate>());

        Assert.Null(anchor);
    }

    [Fact]
    public void NoEligibleCandidatesAtAllProducesNoAnchor()
    {
        var onlyMinimized = new MainWindowAnchorRule.Candidate(HandleA, IsVisible: true, IsMinimized: true, IsMaximized: true, new Rectangle(0, 0, 1920, 1040));

        var anchor = MainWindowAnchorRule.Choose(new[] { onlyMinimized });

        Assert.Null(anchor);
    }

    // ------------------------------------------------------------------
    // Resolve (sticky entry point)
    // ------------------------------------------------------------------

    [Fact]
    public void ResolveWithNoCachedHandleFallsThroughToChoose()
    {
        var candidate = new MainWindowAnchorRule.Candidate(HandleA, IsVisible: true, IsMinimized: false, IsMaximized: true, new Rectangle(0, 0, 1920, 1040));

        var anchor = MainWindowAnchorRule.Resolve(IntPtr.Zero, new[] { candidate });

        Assert.Equal(HandleA, anchor!.Value.Handle);
    }

    [Fact]
    public void ResolveKeepsAnchoringToTheCachedWindowEvenWhenAPopupIsNowLargerOrMaximized()
    {
        // The scenario the round-5 bug regression actually was: the main
        // window is already cached from a previous tick, and PioneerRx
        // opens a small popup that happens to become foreground — Resolve
        // must ignore the popup entirely and keep returning the cached
        // main window's (possibly-updated) rect.
        var mainWindow = new MainWindowAnchorRule.Candidate(HandleA, IsVisible: true, IsMinimized: false, IsMaximized: true, new Rectangle(0, 0, 1920, 1040));
        var popup = new MainWindowAnchorRule.Candidate(HandleB, IsVisible: true, IsMinimized: false, IsMaximized: false, new Rectangle(500, 500, 300, 200));

        var anchor = MainWindowAnchorRule.Resolve(HandleA, new[] { popup, mainWindow });

        Assert.Equal(HandleA, anchor!.Value.Handle);
        Assert.Equal(mainWindow.Bounds, anchor.Value.Bounds);
    }

    [Fact]
    public void ResolveTracksTheCachedWindowsRectWhenItMovesOrResizes()
    {
        var movedMainWindow = new MainWindowAnchorRule.Candidate(HandleA, IsVisible: true, IsMinimized: false, IsMaximized: true, new Rectangle(10, 20, 1900, 1000));

        var anchor = MainWindowAnchorRule.Resolve(HandleA, new[] { movedMainWindow });

        Assert.Equal(movedMainWindow.Bounds, anchor!.Value.Bounds);
    }

    [Fact]
    public void ResolveReEvaluatesWhenTheCachedHandleIsNoLongerAmongTheCandidates()
    {
        // The previously-cached window closed entirely.
        var newMainWindow = new MainWindowAnchorRule.Candidate(HandleB, IsVisible: true, IsMinimized: false, IsMaximized: true, new Rectangle(0, 0, 1920, 1040));

        var anchor = MainWindowAnchorRule.Resolve(HandleA, new[] { newMainWindow });

        Assert.Equal(HandleB, anchor!.Value.Handle);
    }

    [Fact]
    public void ResolveReEvaluatesWhenTheCachedHandleHasBeenMinimized()
    {
        var nowMinimized = new MainWindowAnchorRule.Candidate(HandleA, IsVisible: true, IsMinimized: true, IsMaximized: true, new Rectangle(0, 0, 1920, 1040));
        var fallback = new MainWindowAnchorRule.Candidate(HandleB, IsVisible: true, IsMinimized: false, IsMaximized: false, new Rectangle(0, 0, 800, 600));

        var anchor = MainWindowAnchorRule.Resolve(HandleA, new[] { nowMinimized, fallback });

        Assert.Equal(HandleB, anchor!.Value.Handle);
    }

    [Fact]
    public void ResolveReEvaluatesWhenTheCachedHandleHasBecomeInvisible()
    {
        var nowInvisible = new MainWindowAnchorRule.Candidate(HandleA, IsVisible: false, IsMinimized: false, IsMaximized: true, new Rectangle(0, 0, 1920, 1040));
        var fallback = new MainWindowAnchorRule.Candidate(HandleC, IsVisible: true, IsMinimized: false, IsMaximized: false, new Rectangle(0, 0, 800, 600));

        var anchor = MainWindowAnchorRule.Resolve(HandleA, new[] { nowInvisible, fallback });

        Assert.Equal(HandleC, anchor!.Value.Handle);
    }

    [Fact]
    public void ResolveReturnsNullWhenNothingIsEligibleEvenWithAStaleCachedHandle()
    {
        var anchor = MainWindowAnchorRule.Resolve(HandleA, Array.Empty<MainWindowAnchorRule.Candidate>());

        Assert.Null(anchor);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(-5, 10)]
    public void SaneWindowRectRequiresPositiveWidthAndHeight(int width, int height)
    {
        Assert.False(MainWindowAnchorRule.IsSaneWindowRect(new Rectangle(0, 0, width, height)));
    }

    [Fact]
    public void SaneWindowRectAcceptsAnyPositiveSize()
    {
        Assert.True(MainWindowAnchorRule.IsSaneWindowRect(new Rectangle(0, 0, 1, 1)));
    }
}
