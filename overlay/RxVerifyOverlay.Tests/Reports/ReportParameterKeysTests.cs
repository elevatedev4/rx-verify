using System;
using System.Linq;
using RxVerifyOverlay.Reports;
using Xunit;

namespace RxVerifyOverlay.Tests.Reports;

/// <summary>Unit tests for Reports/ReportParameterKeys.cs — pure date formatting, keystroke-plan building, and per-report timeout math. No UIA/FlaUI/WPF involved (PioneerReportDriver.ReplayReportParameterKeyPlan is the untestable-on-this-Mac shim that actually sends these).</summary>
public class ReportParameterKeysTests
{
    // --- ReportDateKeys.Format ---

    [Fact]
    public void FormatUsesMMddyyyyWithNoSlashes()
    {
        var date = new DateTime(2026, 9, 21);

        Assert.Equal("09212026", ReportDateKeys.Format(date));
    }

    [Fact]
    public void FormatPadsSingleDigitMonthAndDayWithLeadingZeros()
    {
        var date = new DateTime(2026, 1, 5);

        Assert.Equal("01052026", ReportDateKeys.Format(date));
    }

    [Fact]
    public void FormatUsesTheFullFourDigitYear()
    {
        var date = new DateTime(2028, 12, 31);

        Assert.Equal("12312028", ReportDateKeys.Format(date));
    }

    // --- ReportParameterKeyPlan.Build ---

    [Fact]
    public void DateRangeEntryDefaultsToTwoTabsBetweenBeginAndEnd()
    {
        // ArAgedTrialBalanceKey ("Customer A/R Control Balance") matches
        // the owner's own description of the popup and Will's "A/R
        // Control Balance" macro - two tabs between Begin and End.
        var entry = ReportCatalog.FindByKey(ReportCatalog.ArAgedTrialBalanceKey)!;
        var begin = new DateTime(2026, 8, 1);
        var end = new DateTime(2026, 8, 31);

        var plan = ReportParameterKeyPlan.Build(entry, begin, end);

        var expected = new[]
        {
            ReportParameterKeyAction.SelectAll(),
            ReportParameterKeyAction.PasteText("08012026"),
            ReportParameterKeyAction.Tab(),
            ReportParameterKeyAction.Tab(),
            ReportParameterKeyAction.SelectAll(),
            ReportParameterKeyAction.PasteText("08312026"),
            ReportParameterKeyAction.F12(),
        };

        Assert.Equal(expected, plan);
    }

    [Fact]
    public void DateRangeEntryWithSingleTabVariantSendsOnlyOneTab()
    {
        // SalesSummaryKey's macro ("Accrual system"): %start_date_text%<TAB>%date_text%<F12> - one tab.
        var entry = ReportCatalog.FindByKey(ReportCatalog.SalesSummaryKey)!;
        var begin = new DateTime(2026, 8, 1);
        var end = new DateTime(2026, 8, 31);

        var plan = ReportParameterKeyPlan.Build(entry, begin, end);

        var expected = new[]
        {
            ReportParameterKeyAction.SelectAll(),
            ReportParameterKeyAction.PasteText("08012026"),
            ReportParameterKeyAction.Tab(),
            ReportParameterKeyAction.SelectAll(),
            ReportParameterKeyAction.PasteText("08312026"),
            ReportParameterKeyAction.F12(),
        };

        Assert.Equal(expected, plan);
    }

    [Fact]
    public void AsOfDateEntryTypesOnlyTheEndDateThenF12()
    {
        var entry = ReportCatalog.FindByKey(ReportCatalog.ThirdPartyAgedTrialBalanceKey)!;
        var begin = new DateTime(2026, 8, 1);
        var end = new DateTime(2026, 8, 31);

        var plan = ReportParameterKeyPlan.Build(entry, begin, end);

        var expected = new[]
        {
            ReportParameterKeyAction.SelectAll(),
            ReportParameterKeyAction.PasteText("08312026"),
            ReportParameterKeyAction.F12(),
        };

        Assert.Equal(expected, plan);
    }

    [Fact]
    public void PaymentsSearchEntryProducesAnEmptyPlan()
    {
        // Payments never reaches the "Report Parameters" popup - it's
        // driven by RunPaymentsExport/SetPaymentsDateRange directly on
        // _mainWindow instead (see ReportCatalog's ParameterKind doc).
        var entry = ReportCatalog.FindByKey(ReportCatalog.PaymentsKey)!;
        var begin = new DateTime(2026, 8, 1);
        var end = new DateTime(2026, 8, 31);

        var plan = ReportParameterKeyPlan.Build(entry, begin, end);

        Assert.Empty(plan);
    }

    [Fact]
    public void EveryPasteTextActionCarriesEightDigitsOnly()
    {
        foreach (var entry in ReportCatalog.All.Where(e => e.ParameterKind != ReportParameterKind.PaymentsSearch))
        {
            var plan = ReportParameterKeyPlan.Build(entry, new DateTime(2026, 1, 5), new DateTime(2026, 12, 31));

            foreach (var action in plan.Where(a => a.Kind == ReportParameterKeyActionKind.PasteText))
            {
                Assert.NotNull(action.Text);
                Assert.Equal(8, action.Text!.Length);
                Assert.All(action.Text, ch => Assert.True(char.IsDigit(ch)));
            }
        }
    }

    // --- Round 5 (W-T92 follow-up): TypeText is gone, PasteText replaces it ---

    [Fact]
    public void PlanActionsOnlyEverUseTheCurrentActionKinds()
    {
        // Guards the plan itself, not just the enum: every action any
        // catalog entry can ever produce must be one of the four kinds
        // ReplayReportParameterKeyPlan actually knows how to send.
        var allowedKinds = new[]
        {
            ReportParameterKeyActionKind.SelectAll,
            ReportParameterKeyActionKind.PasteText,
            ReportParameterKeyActionKind.Tab,
            ReportParameterKeyActionKind.F12,
        };

        foreach (var entry in ReportCatalog.All)
        {
            var plan = ReportParameterKeyPlan.Build(entry, new DateTime(2026, 1, 1), new DateTime(2026, 1, 31));

            Assert.All(plan, action => Assert.Contains(action.Kind, allowedKinds));
        }
    }

    [Fact]
    public void ReportParameterKeyActionKindNoLongerDefinesTypeText()
    {
        // The down-arrow bug (Round 4's Keyboard.Type of digit characters
        // landing as numpad/scan-code keys in Pioneer's masked date field)
        // means typing digits into this popup must never come back -
        // pin the enum itself so a future edit can't silently reintroduce
        // a TypeText member/action.
        Assert.DoesNotContain("TypeText", Enum.GetNames(typeof(ReportParameterKeyActionKind)));
    }

    [Fact]
    public void EveryNonEmptyPlanStartsWithSelectAllAndEndsWithF12()
    {
        foreach (var entry in ReportCatalog.All.Where(e => e.ParameterKind != ReportParameterKind.PaymentsSearch))
        {
            var plan = ReportParameterKeyPlan.Build(entry, new DateTime(2026, 1, 1), new DateTime(2026, 1, 31));

            Assert.NotEmpty(plan);
            Assert.Equal(ReportParameterKeyActionKind.SelectAll, plan[0].Kind);
            Assert.Equal(ReportParameterKeyActionKind.F12, plan[^1].Kind);
        }
    }

    // --- W-T92 round 3: includeF12: false for the "Test date entry" button ---

    [Fact]
    public void IncludeF12FalseOmitsF12ButKeepsEverythingElseIdentical()
    {
        var entry = ReportCatalog.FindByKey(ReportCatalog.ArAgedTrialBalanceKey)!;
        var begin = new DateTime(2026, 8, 1);
        var end = new DateTime(2026, 8, 31);

        var fullPlan = ReportParameterKeyPlan.Build(entry, begin, end);
        var testPlan = ReportParameterKeyPlan.Build(entry, begin, end, includeF12: false);

        Assert.Equal(fullPlan.Count - 1, testPlan.Count);
        Assert.Equal(fullPlan.Take(fullPlan.Count - 1), testPlan);
        Assert.DoesNotContain(testPlan, a => a.Kind == ReportParameterKeyActionKind.F12);
    }

    [Fact]
    public void IncludeF12FalseStillEndsWithThePasteTextStepForEveryReport()
    {
        foreach (var entry in ReportCatalog.All.Where(e => e.ParameterKind != ReportParameterKind.PaymentsSearch))
        {
            var plan = ReportParameterKeyPlan.Build(entry, new DateTime(2026, 1, 1), new DateTime(2026, 1, 31), includeF12: false);

            Assert.NotEmpty(plan);
            Assert.Equal(ReportParameterKeyActionKind.SelectAll, plan[0].Kind);
            Assert.Equal(ReportParameterKeyActionKind.PasteText, plan[^1].Kind);
        }
    }

    [Fact]
    public void IncludeF12FalseProducesAnEmptyPlanForPaymentsSearchToo()
    {
        var entry = ReportCatalog.FindByKey(ReportCatalog.PaymentsKey)!;

        var plan = ReportParameterKeyPlan.Build(entry, new DateTime(2026, 8, 1), new DateTime(2026, 8, 31), includeF12: false);

        Assert.Empty(plan);
    }

    // --- ReportTimeoutPlan.CalculateTimeout ---

    [Fact]
    public void CalculateTimeoutIsFourTimesTheMacroRunTimeWhenAboveTheFloor()
    {
        // Third Party / Inventory Control Balance's own macro run time (20s) x4 = 80s.
        Assert.Equal(TimeSpan.FromSeconds(80), ReportTimeoutPlan.CalculateTimeout(TimeSpan.FromSeconds(20)));
        // Inventory valuation's own macro run time (15s) x4 = 60s.
        Assert.Equal(TimeSpan.FromSeconds(60), ReportTimeoutPlan.CalculateTimeout(TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public void CalculateTimeoutFloorsAtThirtySecondsForAFastMacroRunTime()
    {
        // 6s x4 = 24s, below the 30s floor.
        Assert.Equal(TimeSpan.FromSeconds(30), ReportTimeoutPlan.CalculateTimeout(TimeSpan.FromSeconds(6)));
        // 2.5s x4 = 10s, below the 30s floor.
        Assert.Equal(TimeSpan.FromSeconds(30), ReportTimeoutPlan.CalculateTimeout(TimeSpan.FromSeconds(2.5)));
    }

    [Fact]
    public void CalculateTimeoutFloorsAtThirtySecondsWhenMacroRunTimeIsUnset()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), ReportTimeoutPlan.CalculateTimeout(TimeSpan.Zero));
    }
}
