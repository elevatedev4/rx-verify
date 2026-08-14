using System;
using System.Drawing;
using RxVerifyOverlay.OrderAssist.Windows;
using Xunit;

namespace RxVerifyOverlay.Tests.OrderAssist;

/// <summary>
/// Unit tests for OrderAssistWindowSelectionRule — the pure decision
/// behind the popup-fix branch's headline bug (owner's live pharmacy
/// report: "the recommended order pops up in a window above the main
/// Pioneer ... make sure that the logic will work with the popup window
/// because right now nothing works"). Same test shape as
/// MainWindowAnchorRuleTests, the round-7 fix this one is modeled on.
/// </summary>
public class OrderAssistWindowSelectionRuleTests
{
    private static readonly IntPtr HandleA = new(1);
    private static readonly IntPtr HandleB = new(2);
    private static readonly Rectangle SaneBounds = new(0, 0, 800, 600);

    [Fact]
    public void ForegroundWindowWinsWhenItIsAnEligibleTarget()
    {
        // Common case, unchanged from the old foreground-only behavior:
        // the "Create Recommended Orders" window itself is foreground.
        var createOrders = new OrderAssistWindowSelectionRule.Candidate(HandleA, OrderAssistWindowKind.CreateRecommendedOrders, IsVisible: true, IsMinimized: false, Bounds: SaneBounds);

        var chosen = OrderAssistWindowSelectionRule.Choose(new[] { createOrders }, foregroundHandle: HandleA);

        Assert.Equal(HandleA, chosen!.Value.Handle);
    }

    [Fact]
    public void PicksTheTopmostEligibleTargetWhenNeitherIsForeground()
    {
        // THE BUG: the Catalog Item Substitution dialog floats above the
        // still-open Create Recommended Orders window, but a stray click
        // on the NOACTIVATE control box (or anything else) means NEITHER
        // is the current foreground window. Candidates are supplied in
        // EnumWindows' own Z order (topmost first) -- the dialog, being
        // on top, must win.
        var catalogSubstitutionOnTop = new OrderAssistWindowSelectionRule.Candidate(HandleA, OrderAssistWindowKind.CatalogSubstitution, IsVisible: true, IsMinimized: false, Bounds: SaneBounds);
        var createOrdersBehindIt = new OrderAssistWindowSelectionRule.Candidate(HandleB, OrderAssistWindowKind.CreateRecommendedOrders, IsVisible: true, IsMinimized: false, Bounds: SaneBounds);

        var chosen = OrderAssistWindowSelectionRule.Choose(new[] { catalogSubstitutionOnTop, createOrdersBehindIt }, foregroundHandle: IntPtr.Zero);

        Assert.Equal(HandleA, chosen!.Value.Handle);
        Assert.Equal(OrderAssistWindowKind.CatalogSubstitution, chosen.Value.Kind);
    }

    [Fact]
    public void ForegroundHandleThatIsNotAnEligibleTargetFallsThroughToEnumerationOrder()
    {
        // The foreground window right now is neither target screen (e.g.
        // Pioneer's own main window, or an entirely different app) -- the
        // fast path must not block the fallback to whatever eligible
        // target IS visible.
        var target = new OrderAssistWindowSelectionRule.Candidate(HandleA, OrderAssistWindowKind.CreateRecommendedOrders, IsVisible: true, IsMinimized: false, Bounds: SaneBounds);
        var foregroundHandleThatIsNotACandidateAtAll = new IntPtr(999);

        var chosen = OrderAssistWindowSelectionRule.Choose(new[] { target }, foregroundHandleThatIsNotACandidateAtAll);

        Assert.Equal(HandleA, chosen!.Value.Handle);
    }

    [Fact]
    public void NoneKindCandidatesAreNeverEligibleEvenAsForeground()
    {
        var notATargetScreen = new OrderAssistWindowSelectionRule.Candidate(HandleA, OrderAssistWindowKind.None, IsVisible: true, IsMinimized: false, Bounds: SaneBounds);

        var chosen = OrderAssistWindowSelectionRule.Choose(new[] { notATargetScreen }, foregroundHandle: HandleA);

        Assert.Null(chosen);
    }

    [Fact]
    public void SkipsMinimizedCandidates()
    {
        var minimized = new OrderAssistWindowSelectionRule.Candidate(HandleA, OrderAssistWindowKind.CreateRecommendedOrders, IsVisible: true, IsMinimized: true, Bounds: SaneBounds);
        var visible = new OrderAssistWindowSelectionRule.Candidate(HandleB, OrderAssistWindowKind.CatalogSubstitution, IsVisible: true, IsMinimized: false, Bounds: SaneBounds);

        var chosen = OrderAssistWindowSelectionRule.Choose(new[] { minimized, visible }, foregroundHandle: IntPtr.Zero);

        Assert.Equal(HandleB, chosen!.Value.Handle);
    }

    [Fact]
    public void SkipsInvisibleCandidates()
    {
        var invisible = new OrderAssistWindowSelectionRule.Candidate(HandleA, OrderAssistWindowKind.CreateRecommendedOrders, IsVisible: false, IsMinimized: false, Bounds: SaneBounds);
        var visible = new OrderAssistWindowSelectionRule.Candidate(HandleB, OrderAssistWindowKind.CatalogSubstitution, IsVisible: true, IsMinimized: false, Bounds: SaneBounds);

        var chosen = OrderAssistWindowSelectionRule.Choose(new[] { invisible, visible }, foregroundHandle: IntPtr.Zero);

        Assert.Equal(HandleB, chosen!.Value.Handle);
    }

    [Fact]
    public void SkipsCandidatesWithADegenerateRect()
    {
        var degenerate = new OrderAssistWindowSelectionRule.Candidate(HandleA, OrderAssistWindowKind.CreateRecommendedOrders, IsVisible: true, IsMinimized: false, Bounds: new Rectangle(0, 0, 0, 0));
        var normal = new OrderAssistWindowSelectionRule.Candidate(HandleB, OrderAssistWindowKind.CatalogSubstitution, IsVisible: true, IsMinimized: false, Bounds: SaneBounds);

        var chosen = OrderAssistWindowSelectionRule.Choose(new[] { degenerate, normal }, foregroundHandle: IntPtr.Zero);

        Assert.Equal(HandleB, chosen!.Value.Handle);
    }

    [Fact]
    public void EmptyCandidatesProducesNoTarget()
    {
        var chosen = OrderAssistWindowSelectionRule.Choose(Array.Empty<OrderAssistWindowSelectionRule.Candidate>(), foregroundHandle: IntPtr.Zero);

        Assert.Null(chosen);
    }

    [Fact]
    public void NoEligibleCandidatesAtAllProducesNoTargetEvenWithOtherPioneerWindowsPresent()
    {
        // Simulates the diagnostic-logging scenario: Pioneer's queue/
        // search/dashboard window is visible (a real Candidate would
        // normally never even be built for it -- OrderAssistWindowLocator
        // only builds Candidates for windows it bothered to classify --
        // but Kind.None here stands in for "classified, didn't match").
        var mainPioneerWindow = new OrderAssistWindowSelectionRule.Candidate(HandleA, OrderAssistWindowKind.None, IsVisible: true, IsMinimized: false, Bounds: SaneBounds);

        var chosen = OrderAssistWindowSelectionRule.Choose(new[] { mainPioneerWindow }, foregroundHandle: IntPtr.Zero);

        Assert.Null(chosen);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(-5, 10)]
    public void SaneWindowRectRequiresPositiveWidthAndHeight(int width, int height)
    {
        Assert.False(OrderAssistWindowSelectionRule.IsSaneWindowRect(new Rectangle(0, 0, width, height)));
    }

    [Fact]
    public void SaneWindowRectAcceptsAnyPositiveSize()
    {
        Assert.True(OrderAssistWindowSelectionRule.IsSaneWindowRect(new Rectangle(0, 0, 1, 1)));
    }
}
