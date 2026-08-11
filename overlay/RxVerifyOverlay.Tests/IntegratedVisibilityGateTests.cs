using RxVerifyOverlay.Integrated;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for IntegratedVisibilityGate (Integrated/
/// IntegratedVisibilityGate.cs) — the pure decision behind when the
/// integrated boxes layer / control box should be visible. Encodes the
/// owner's spec directly: boxes require attached + foreground + maximized
/// + something verified to show; the control box only requires attached +
/// foreground (it stays up, in its "maximize to use integrated view"
/// state, even when not maximized).
/// </summary>
public class IntegratedVisibilityGateTests
{
    [Fact]
    public void BoxesShowOnlyWhenEveryConditionIsTrue()
    {
        Assert.True(IntegratedVisibilityGate.ShouldShowBoxes(
            isAttached: true, isForeground: true, isMaximized: true, hasVerifiableContent: true));
    }

    [Fact]
    public void BoxesHideWhenNotAttached()
    {
        Assert.False(IntegratedVisibilityGate.ShouldShowBoxes(
            isAttached: false, isForeground: true, isMaximized: true, hasVerifiableContent: true));
    }

    [Fact]
    public void BoxesHideWhenPioneerIsNotForeground()
    {
        // The pharmacist switched to a different app — boxes must not
        // float over whatever that app is.
        Assert.False(IntegratedVisibilityGate.ShouldShowBoxes(
            isAttached: true, isForeground: false, isMaximized: true, hasVerifiableContent: true));
    }

    [Fact]
    public void BoxesHideWhenPioneerIsNotMaximized()
    {
        // MAXIMIZED-ONLY per the owner's spec.
        Assert.False(IntegratedVisibilityGate.ShouldShowBoxes(
            isAttached: true, isForeground: true, isMaximized: false, hasVerifiableContent: true));
    }

    [Fact]
    public void BoxesHideWhenNothingHasBeenVerifiedYet()
    {
        // Mirrors the existing non-escript/no-data blank-state signal —
        // no data to draw boxes for.
        Assert.False(IntegratedVisibilityGate.ShouldShowBoxes(
            isAttached: true, isForeground: true, isMaximized: true, hasVerifiableContent: false));
    }

    [Fact]
    public void ControlBoxShowsWhenAttachedAndForegroundRegardlessOfMaximizedState()
    {
        Assert.True(IntegratedVisibilityGate.ShouldShowControlBox(isAttached: true, isForeground: true));
    }

    [Fact]
    public void ControlBoxHidesWhenNotAttached()
    {
        Assert.False(IntegratedVisibilityGate.ShouldShowControlBox(isAttached: false, isForeground: true));
    }

    [Fact]
    public void ControlBoxHidesWhenNotForeground()
    {
        Assert.False(IntegratedVisibilityGate.ShouldShowControlBox(isAttached: true, isForeground: false));
    }
}
