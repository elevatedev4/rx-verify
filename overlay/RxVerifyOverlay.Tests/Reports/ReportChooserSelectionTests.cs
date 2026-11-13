using System;
using System.Collections.Generic;
using System.Linq;
using RxVerifyOverlay.Reports;
using Xunit;

namespace RxVerifyOverlay.Tests.Reports;

/// <summary>W-T97: choosing a report on Pioneer's chooser page by name, never by stepping Down. Pure logic only (ReportChooserSelection.cs).</summary>
public class ReportChooserSelectionTests
{
    private const string ArControl = "A/R Control Balance by Date Range";

    [Fact]
    public void TypeToSearchKeysAreCtrlHomeThenTheWholeNameThenEnterAndNeverAnArrow()
    {
        var keys = ReportChooserSelection.BuildTypeToSearchKeys(ArControl);

        Assert.Equal(new[] { ChooserKey.CtrlHome, ChooserKey.TypeName, ChooserKey.Enter }, keys.Select(k => k.Key));
        Assert.Equal(ArControl, keys.Single(k => k.Key == ChooserKey.TypeName).Text);
        // The key set itself has no arrow key to send: guard against one being added.
        Assert.DoesNotContain(Enum.GetNames<ChooserKey>(), n => n.Contains("Down", StringComparison.OrdinalIgnoreCase) || n.Contains("Arrow", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TypeToSearchTypesTheNormalizedName()
    {
        var keys = ReportChooserSelection.BuildTypeToSearchKeys("  A/R   Control Balance by Date Range ");

        Assert.Equal(ArControl, keys.Single(k => k.Key == ChooserKey.TypeName).Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void NoKeysAreBuiltForAnEmptyName(string name)
    {
        Assert.Empty(ReportChooserSelection.BuildTypeToSearchKeys(name));
    }

    [Fact]
    public void EveryCatalogRowGetsAKeyPlanWithoutAnArrow()
    {
        foreach (var entry in ReportCatalog.All.Where(e => e.ParameterKind != ReportParameterKind.PaymentsSearch))
        {
            foreach (var candidate in ReportRowSelectionSequencer.BuildRowTextCandidates(entry))
            {
                var keys = ReportChooserSelection.BuildTypeToSearchKeys(candidate);
                Assert.Equal(3, keys.Count);
                Assert.Equal(ReportRowTextMatcher.Normalize(candidate), keys[1].Text);
            }
        }
    }

    [Fact]
    public void ReadbackIsExactMatchOnlyWhenTheSelectedRowIsTheReport()
    {
        Assert.Equal(ChooserReadback.ExactMatch, ReportChooserSelection.Decide(new string?[] { ArControl }, ArControl));
        Assert.Equal(ChooserReadback.ExactMatch, ReportChooserSelection.Decide(new string?[] { "a/r control   balance by date range" }, ArControl));
    }

    [Fact]
    public void ReadbackOfAnotherRowOrAPrefixIsNeverAMatch()
    {
        Assert.Equal(ChooserReadback.OtherRow, ReportChooserSelection.Decide(new string?[] { "A/R Control Balance by Date Range (Detail)" }, ArControl));
        Assert.Equal(ChooserReadback.OtherRow, ReportChooserSelection.Decide(new string?[] { "A/R Aging" }, ArControl));
        Assert.Equal(ChooserReadback.OtherRow, ReportChooserSelection.Decide(new string?[] { "Sales Summary", "Report Name Row 4" }, ArControl));
    }

    [Fact]
    public void ReadbackWithNothingReadableIsUnreadableSoEnterIsNeverSent()
    {
        Assert.Equal(ChooserReadback.Unreadable, ReportChooserSelection.Decide(Array.Empty<string?>(), ArControl));
        Assert.Equal(ChooserReadback.Unreadable, ReportChooserSelection.Decide(new string?[] { null, "  " }, ArControl));
    }

    [Fact]
    public void AGenericNameDoesNotHideTheRealTextInTheValue()
    {
        // A grid cell: Name "Report Name Row 5", Value = the report name. Any text matching is enough.
        var texts = new string?[] { "Report Name Row 5", ArControl };

        Assert.True(ReportChooserSelection.AnyTextMatches(texts, ArControl));
        Assert.Equal(ChooserReadback.ExactMatch, ReportChooserSelection.Decide(texts, ArControl));
    }

    [Fact]
    public void AnyTextMatchesIsFalseWhenNoTextIsExact()
    {
        Assert.False(ReportChooserSelection.AnyTextMatches(new string?[] { "A/R", "Control", null }, ArControl));
    }

    [Fact]
    public void KnownReportNamesAreCatalogNamesNotColumnHeaders()
    {
        Assert.True(ReportChooserSelection.IsKnownReportName(ArControl));
        Assert.False(ReportChooserSelection.IsKnownReportName("Report Name"));
        Assert.False(ReportChooserSelection.IsKnownReportName("Some Patient Row"));
    }

    [Fact]
    public void NotFoundMessageNamesTheReportAndWhatWasOnScreenWithoutAnyTitle()
    {
        var stats = new ChooserScanStats(true, 812, 36, new[] { "Sales Summary", "Inventory Valuation" }, false);

        var message = ReportChooserSelection.BuildNotFoundMessage(ArControl, stats);

        Assert.Contains($"'{ArControl}'", message);
        Assert.Contains("812 element(s)", message);
        Assert.Contains("36 row-like", message);
        Assert.Contains("Sales Summary; Inventory Valuation", message);
        Assert.DoesNotContain("\n", message);
    }

    [Fact]
    public void NotFoundMessageSaysSoWhenTheReportListPaneIsMissing()
    {
        var message = ReportChooserSelection.BuildNotFoundMessage(ArControl, ChooserScanStats.None);

        Assert.Contains($"'{ArControl}'", message);
        Assert.Contains("pane was not found", message);
    }

    [Fact]
    public void NotFoundMessageSaysWhenNoKnownNameWasVisibleAndWhenTheSearchHitItsLimit()
    {
        var message = ReportChooserSelection.BuildNotFoundMessage(ArControl, new ChooserScanStats(true, 5000, 0, Array.Empty<string>(), true));

        Assert.Contains("none of the known report names were visible", message);
        Assert.Contains("element limit", message);
    }

    [Fact]
    public void NotFoundMessageListsAtMostSixKnownNames()
    {
        var names = Enumerable.Range(1, 10).Select(i => $"Report {i}").ToList();

        var message = ReportChooserSelection.BuildNotFoundMessage(ArControl, new ChooserScanStats(true, 100, 10, names, false));

        Assert.Contains("Report 6", message);
        Assert.DoesNotContain("Report 7", message);
        Assert.Contains("...", message);
    }
}
