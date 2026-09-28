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

    // --- ReportParameterKeyPlan.Build (Round 4: leading tabs, TypeText, trailing keys) ---

    [Fact]
    public void DateRangeEntryWithTwoLeadingTabsSendsThemBeforeBothDates()
    {
        // ArAgedTrialBalanceKey ("Customer A/R Control Balance") macro:
        // <TAB><TAB>%start_date_text%<TAB><TAB>%date_text%<F12> - the
        // popup does NOT open with Begin already focused for this report.
        var entry = ReportCatalog.FindByKey(ReportCatalog.ArAgedTrialBalanceKey)!;
        var begin = new DateTime(2026, 8, 1);
        var end = new DateTime(2026, 8, 31);

        var plan = ReportParameterKeyPlan.Build(entry, begin, end);

        var expected = new[]
        {
            ReportParameterKeyAction.Tab(),
            ReportParameterKeyAction.Tab(),
            ReportParameterKeyAction.TypeText("08012026"),
            ReportParameterKeyAction.Tab(),
            ReportParameterKeyAction.Tab(),
            ReportParameterKeyAction.TypeText("08312026"),
            ReportParameterKeyAction.F12(),
        };

        Assert.Equal(expected, plan);
    }

    [Fact]
    public void DateRangeEntryWithNoLeadingTabsAndOneTabBetweenDates()
    {
        // SalesSummaryKey's macro ("Accrual system"): %start_date_text%<TAB>%date_text%<F12> - no leading tabs, one tab between dates.
        var entry = ReportCatalog.FindByKey(ReportCatalog.SalesSummaryKey)!;
        var begin = new DateTime(2026, 8, 1);
        var end = new DateTime(2026, 8, 31);

        var plan = ReportParameterKeyPlan.Build(entry, begin, end);

        var expected = new[]
        {
            ReportParameterKeyAction.TypeText("08012026"),
            ReportParameterKeyAction.Tab(),
            ReportParameterKeyAction.TypeText("08312026"),
            ReportParameterKeyAction.F12(),
        };

        Assert.Equal(expected, plan);
    }

    [Fact]
    public void AsOfDateEntryTypesOnlyTheEndDateThenF12()
    {
        // ThirdPartyAgedTrialBalanceKey's macro: %date_text%<F12> - no leading tabs, single field.
        var entry = ReportCatalog.FindByKey(ReportCatalog.ThirdPartyAgedTrialBalanceKey)!;
        var begin = new DateTime(2026, 8, 1);
        var end = new DateTime(2026, 8, 31);

        var plan = ReportParameterKeyPlan.Build(entry, begin, end);

        var expected = new[]
        {
            ReportParameterKeyAction.TypeText("08312026"),
            ReportParameterKeyAction.F12(),
        };

        Assert.Equal(expected, plan);
    }

    [Fact]
    public void InventoryValuationEndsWithTwoF12sMatchingItsMacroExactly()
    {
        // Reviewer round 4 correction: the macro is
        // %date_text%<TAB><ARROW DOWN><F12><F12> - two F12s back to back,
        // not one. Tab/ArrowDown/the first F12 come from TrailingKeys; the
        // second is ReportParameterKeyPlan.Build's own separate run F12.
        var entry = ReportCatalog.FindByKey(ReportCatalog.InventoryValuationKey)!;
        var begin = new DateTime(2026, 8, 1);
        var end = new DateTime(2026, 8, 31);

        var plan = ReportParameterKeyPlan.Build(entry, begin, end);

        var expected = new[]
        {
            ReportParameterKeyAction.TypeText("08312026"),
            ReportParameterKeyAction.Tab(),
            ReportParameterKeyAction.ArrowDown(),
            ReportParameterKeyAction.F12(),
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
    public void EveryTypeTextActionCarriesEightDigitsOnly()
    {
        foreach (var entry in ReportCatalog.All.Where(e => e.ParameterKind != ReportParameterKind.PaymentsSearch))
        {
            var plan = ReportParameterKeyPlan.Build(entry, new DateTime(2026, 1, 5), new DateTime(2026, 12, 31));

            foreach (var action in plan.Where(a => a.Kind == ReportParameterKeyActionKind.TypeText))
            {
                Assert.NotNull(action.Text);
                Assert.Equal(8, action.Text!.Length);
                Assert.All(action.Text, ch => Assert.True(char.IsDigit(ch)));
            }
        }
    }

    // --- Round 4: SelectAll/PasteText are retry-only, never in a built plan ---

    [Fact]
    public void PlanActionsOnlyEverUseTheDefaultPathActionKinds()
    {
        // Guards the plan itself, not just the enum: a ReportParameterKeyPlan
        // never contains SelectAll/PasteText - those are sent directly by
        // PioneerReportDriver.ReplayReportParameterKeyPlan's own
        // read-back-mismatch retry, never planned in advance.
        var allowedKinds = new[]
        {
            ReportParameterKeyActionKind.Tab,
            ReportParameterKeyActionKind.TypeText,
            ReportParameterKeyActionKind.ArrowDown,
            ReportParameterKeyActionKind.F12,
        };

        foreach (var entry in ReportCatalog.All)
        {
            var plan = ReportParameterKeyPlan.Build(entry, new DateTime(2026, 1, 1), new DateTime(2026, 1, 31));

            Assert.All(plan, action => Assert.Contains(action.Kind, allowedKinds));
        }
    }

    [Fact]
    public void PlanNeverContainsSelectAllOrPasteText()
    {
        foreach (var entry in ReportCatalog.All)
        {
            var plan = ReportParameterKeyPlan.Build(entry, new DateTime(2026, 1, 1), new DateTime(2026, 1, 31));

            Assert.DoesNotContain(plan, a => a.Kind == ReportParameterKeyActionKind.SelectAll);
            Assert.DoesNotContain(plan, a => a.Kind == ReportParameterKeyActionKind.PasteText);
        }
    }

    [Fact]
    public void EveryNonEmptyPlanEndsWithF12()
    {
        foreach (var entry in ReportCatalog.All.Where(e => e.ParameterKind != ReportParameterKind.PaymentsSearch))
        {
            var plan = ReportParameterKeyPlan.Build(entry, new DateTime(2026, 1, 1), new DateTime(2026, 1, 31));

            Assert.NotEmpty(plan);
            Assert.Equal(ReportParameterKeyActionKind.F12, plan[^1].Kind);
        }
    }

    // --- W-T92 round 3 (still true in round 4): includeF12: false for the "Test date entry" button ---

    [Fact]
    public void IncludeF12FalseStripsEveryTrailingF12ForEveryReport()
    {
        // Reviewer round 4 correction: Inventory Valuation's full plan now
        // ends with TWO F12s, so "omit the last one" is no longer enough -
        // Test date entry must strip ALL trailing F12s, or it would still
        // fire a keystroke that runs the report. Verified generically here
        // (works for both the single-F12 reports and Inventory Valuation's
        // double) rather than assuming a fixed count.
        foreach (var entry in ReportCatalog.All.Where(e => e.ParameterKind != ReportParameterKind.PaymentsSearch))
        {
            var begin = new DateTime(2026, 1, 1);
            var end = new DateTime(2026, 1, 31);

            var fullPlan = ReportParameterKeyPlan.Build(entry, begin, end);
            var testPlan = ReportParameterKeyPlan.Build(entry, begin, end, includeF12: false);

            var expectedTestPlan = fullPlan.ToList();
            while (expectedTestPlan.Count > 0 && expectedTestPlan[^1].Kind == ReportParameterKeyActionKind.F12)
            {
                expectedTestPlan.RemoveAt(expectedTestPlan.Count - 1);
            }

            Assert.Equal(ReportParameterKeyActionKind.F12, fullPlan[^1].Kind);
            Assert.Equal(expectedTestPlan, testPlan);
            Assert.DoesNotContain(testPlan, a => a.Kind == ReportParameterKeyActionKind.F12);
        }
    }

    [Fact]
    public void IncludeF12FalseInventoryValuationEndsWithArrowDownAndNoF12()
    {
        var entry = ReportCatalog.FindByKey(ReportCatalog.InventoryValuationKey)!;

        var plan = ReportParameterKeyPlan.Build(entry, new DateTime(2026, 1, 1), new DateTime(2026, 1, 31), includeF12: false);

        Assert.Equal(ReportParameterKeyActionKind.ArrowDown, plan[^1].Kind);
        Assert.DoesNotContain(plan, a => a.Kind == ReportParameterKeyActionKind.F12);
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

    // --- ReadbackEvaluator.Decide (reviewer round 4, blocking finding 2) ---

    [Fact]
    public void DecideIsOkWhenReadbackMatchesExpectedExactly()
    {
        Assert.Equal(ReadbackDecision.Ok, ReadbackEvaluator.Decide("09212026", "09212026"));
    }

    [Fact]
    public void DecideIsUnavailableWhenReadbackIsNull()
    {
        // Null means "could not read this field at all" (ValuePattern AND
        // LegacyIAccessible both failed) - NOT "the field is wrong". This is
        // the exact case that used to abort every report, every run.
        Assert.Equal(ReadbackDecision.Unavailable, ReadbackEvaluator.Decide(null, "09212026"));
    }

    [Fact]
    public void DecideIsMismatchWhenReadbackIsNonNullAndDifferent()
    {
        Assert.Equal(ReadbackDecision.Mismatch, ReadbackEvaluator.Decide("01012020", "09212026"));
    }

    [Fact]
    public void DecideIsMismatchNotUnavailableForAnEmptyStringReadback()
    {
        // An empty string IS a real (if unhelpful) readback, distinct from
        // null/unreadable - it should still count as a mismatch, not be
        // treated as "couldn't read it".
        Assert.Equal(ReadbackDecision.Mismatch, ReadbackEvaluator.Decide(string.Empty, "09212026"));
    }

}
