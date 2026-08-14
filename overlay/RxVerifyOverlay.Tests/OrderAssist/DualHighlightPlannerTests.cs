using System.Collections.Generic;
using RxVerifyOverlay.OrderAssist.Decisions;
using Xunit;

namespace RxVerifyOverlay.Tests.OrderAssist;

using CatalogRowInput = RxVerifyOverlay.OrderAssist.Decisions.SubstitutionRecommender.CatalogRowInput;
using SubstitutionResult = RxVerifyOverlay.OrderAssist.Decisions.SubstitutionRecommender.SubstitutionResult;

/// <summary>
/// Unit tests for DualHighlightPlanner — Will's round-2 "dual visibility"
/// rule (yellow McKesson contrast row alongside a green secondary pick).
/// All suppliers/costs below are synthetic.
/// </summary>
public class DualHighlightPlannerTests
{
    [Fact]
    public void NoGreenRecommendationMeansNoYellowHighlight()
    {
        var rows = new List<CatalogRowInput>
        {
            new(0, "McKesson", "3.00"),
            new(1, "ANDA", "10.00"),
        };

        var result = SubstitutionRecommender.Evaluate(rows); // McKesson already cheapest -> None
        Assert.Null(DualHighlightPlanner.FindMcKessonHighlightRowIndex(rows, result));
    }

    [Fact]
    public void GreenSecondaryPickSurfacesTheCheapestMcKessonRowAsYellow()
    {
        var rows = new List<CatalogRowInput>
        {
            new(0, "ANDA", "6.00"),      // cheapest secondary -> green
            new(1, "McKesson", "12.00"), // more expensive McKesson
            new(2, "McKesson", "10.00"), // cheapest McKesson -> yellow
        };

        var result = SubstitutionRecommender.Evaluate(rows);
        Assert.Equal(SubstitutionRecommendation.RecommendSecondary, result.Recommendation);

        var yellowRowIndex = DualHighlightPlanner.FindMcKessonHighlightRowIndex(rows, result);

        Assert.Equal(2, yellowRowIndex);
    }

    [Fact]
    public void NoMcKessonRowAtAllMeansNoYellowHighlight()
    {
        var rows = new List<CatalogRowInput>
        {
            new(0, "ANDA", "10.00"),
            new(1, "IPC", "7.00"),
        };

        var result = SubstitutionRecommender.Evaluate(rows); // no McKesson option -> still recommends IPC green
        Assert.Equal(SubstitutionRecommendation.RecommendSecondary, result.Recommendation);

        Assert.Null(DualHighlightPlanner.FindMcKessonHighlightRowIndex(rows, result));
    }

    [Fact]
    public void McKessonRowsWithOnlyUnreadableCostNeverGetYellow()
    {
        var rows = new List<CatalogRowInput>
        {
            new(0, "ANDA", "6.00"),
            new(1, "McKesson", ""), // unreadable -- excluded, not treated as free
            new(2, "McKesson", "N/A"),
        };

        var result = SubstitutionRecommender.Evaluate(rows);
        // SubstitutionRecommender itself still recommends ANDA green here
        // (its own "no readable McKesson option" case -- see that class's
        // doc), but DualHighlightPlanner has NOTHING solid to point yellow
        // at: both McKesson rows failed to parse, so its own McKesson
        // candidate pool is empty -- never a yellow guess with no real
        // number behind it.
        Assert.Equal(SubstitutionRecommendation.RecommendSecondary, result.Recommendation);
        Assert.Null(DualHighlightPlanner.FindMcKessonHighlightRowIndex(rows, result));
    }

    [Fact]
    public void UnknownOrBlankSupplierRowsNeverGetYellowEvenWhenCheaperThanTheRealMcKessonRow()
    {
        var rows = new List<CatalogRowInput>
        {
            new(0, "ANDA", "6.00"),      // cheapest secondary -> green
            new(1, "", "7.00"),          // blank supplier -- cheaper than the real McKesson row, must still never be picked
            new(2, "McKesson", "10.00"), // the only row that's actually McKesson -> yellow
        };

        var result = SubstitutionRecommender.Evaluate(rows);
        Assert.Equal(SubstitutionRecommendation.RecommendSecondary, result.Recommendation);
        Assert.Equal(0, result.RecommendedRowIndex);

        Assert.Equal(2, DualHighlightPlanner.FindMcKessonHighlightRowIndex(rows, result));
    }

    [Fact]
    public void TiedCheapestMcKessonRowsResolveToTheLowestRowIndex()
    {
        var rows = new List<CatalogRowInput>
        {
            new(0, "ANDA", "6.00"),
            new(1, "McKesson", "10.00"),
            new(2, "McKesson", "10.00"), // tied, appears later
        };

        var result = SubstitutionRecommender.Evaluate(rows);

        Assert.Equal(1, DualHighlightPlanner.FindMcKessonHighlightRowIndex(rows, result));
    }

    [Fact]
    public void DefensiveGuardNeverYellowsARowThatIsAlsoTheGreenPick()
    {
        // Constructs a SubstitutionResult claiming the green pick IS a
        // McKesson row -- not producible by SubstitutionRecommender.Evaluate
        // today (its own rule only ever recommends a non-McKesson
        // secondary), but the owner's spec is explicit: "Yellow must never
        // appear when McKesson IS the green pick." This proves
        // DualHighlightPlanner enforces that invariant directly rather
        // than assuming SubstitutionRecommender's current behavior always
        // holds.
        var rows = new List<CatalogRowInput>
        {
            new(0, "McKesson", "5.00"),
            new(1, "McKesson", "9.00"),
        };

        var contrivedGreenIsMcKesson = new SubstitutionResult(
            SubstitutionRecommendation.RecommendSecondary,
            RecommendedRowIndex: 0,
            SavingsPercent: 30m,
            SavingsDisplay: "30% savings");

        Assert.Null(DualHighlightPlanner.FindMcKessonHighlightRowIndex(rows, contrivedGreenIsMcKesson));
    }
}
