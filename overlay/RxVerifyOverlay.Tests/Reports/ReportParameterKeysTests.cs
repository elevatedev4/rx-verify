using System;
using System.Linq;
using System.Text.RegularExpressions;
using RxVerifyOverlay.Reports;
using Xunit;

namespace RxVerifyOverlay.Tests.Reports;

/// <summary>Unit tests for Reports/ReportParameterKeys.cs — pure date formatting, keystroke-plan building, and per-report timeout math. No UIA/FlaUI/WPF involved (PioneerReportDriver.ReplayReportParameterKeyPlan is the untestable-on-this-Mac shim that actually sends these).</summary>
public class ReportParameterKeysTests
{
    // --- ReportDateKeys.FormatBegin / FormatEnd (W-T92 round 5 — the macro is the spec) ---

    [Fact]
    public void FormatBeginUsesMMDashDdDashFourDigitYear()
    {
        // Macro's %start_date_text% = %month%-01-%year% -> "MM-dd-yyyy".
        var date = new DateTime(2026, 9, 1);

        Assert.Equal("09-01-2026", ReportDateKeys.FormatBegin(date));
    }

    [Fact]
    public void FormatBeginPadsSingleDigitMonthAndDayWithLeadingZeros()
    {
        var date = new DateTime(2026, 1, 5);

        Assert.Equal("01-05-2026", ReportDateKeys.FormatBegin(date));
    }

    [Fact]
    public void FormatBeginUsesTheFullFourDigitYear()
    {
        var date = new DateTime(2028, 12, 31);

        Assert.Equal("12-31-2028", ReportDateKeys.FormatBegin(date));
    }

    [Fact]
    public void FormatEndUsesMMDashDdDashTwoDigitYear()
    {
        // Macro's %date_text% = Macro Express format mm'-'dd'-'yy -> "MM-dd-yy".
        var date = new DateTime(2026, 9, 28);

        Assert.Equal("09-28-26", ReportDateKeys.FormatEnd(date));
    }

    [Fact]
    public void FormatEndPadsSingleDigitMonthAndDayWithLeadingZeros()
    {
        var date = new DateTime(2026, 1, 5);

        Assert.Equal("01-05-26", ReportDateKeys.FormatEnd(date));
    }

    [Fact]
    public void FormatEndUsesOnlyTheLastTwoYearDigits()
    {
        var date = new DateTime(2028, 12, 31);

        Assert.Equal("12-31-28", ReportDateKeys.FormatEnd(date));
    }

    // --- ReportParameterKeyPlan.Build (Round 4: leading tabs, TypeText, trailing keys) ---

    [Fact]
    public void ControlBalanceEntryStartsWithBeginDateThenTwoTabsThenEndDateThenF12()
    {
        // W-T92 round 7 correction (Will, 2026-09-30, verbatim: "You need
        // to tab twice to enter to the end date. Your 1 tab ended in the
        // time field of the start date."). Round 6 already confirmed the
        // popup opens with focus already in Begin/start-date (LeadingTabs
        // stays 0), but round 6's single Tab between Begin and End left
        // the cursor in the Begin field's own trailing time sub-field
        // instead of the End/date field - ReportCatalog.cs now sets
        // TabsBetweenDates: 2 for ArAgedTrialBalanceKey, so the plan must
        // be exactly: type begin date, two Tabs, type end date, F12 -
        // nothing before the first date, two Tabs between dates. Begin
        // uses FormatBegin (4-digit year), end uses FormatEnd (2-digit
        // year).
        var entry = ReportCatalog.FindByKey(ReportCatalog.ArAgedTrialBalanceKey)!;
        var begin = new DateTime(2026, 9, 1);
        var end = new DateTime(2026, 9, 28);

        var plan = ReportParameterKeyPlan.Build(entry, begin, end);

        var expected = new[]
        {
            ReportParameterKeyAction.TypeText("09-01-2026"),
            ReportParameterKeyAction.Tab(),
            ReportParameterKeyAction.Tab(),
            ReportParameterKeyAction.TypeText("09-28-26"),
            ReportParameterKeyAction.F12(),
        };

        Assert.Equal(expected, plan);
    }

    [Fact]
    public void DateRangeEntryWithTwoLeadingTabsAndTwoTabsBetweenDatesStillProducesThatSequence()
    {
        // The plan builder (ReportParameterKeyPlan.Build) is shared across
        // every DateRange catalog entry - LeadingTabs/TabsBetweenDates are
        // per-entry data, not per-report code paths. This proves the
        // builder still honors LeadingTabs: 2 / TabsBetweenDates: 2 for
        // whichever entry (if any) actually needs that shape, independent
        // of ArAgedTrialBalanceKey's own LeadingTabs: 0 / TabsBetweenDates: 2
        // (round 6/7 corrections) above - a synthetic entry stands in so
        // this test doesn't depend on any particular catalog row keeping
        // those old values.
        var entry = new ReportCatalogEntry(
            Key: "test-two-leading-two-between",
            DisplayName: "Test Two Leading Two Between",
            PioneerRowText: "Test Row",
            ParameterKind: ReportParameterKind.DateRange,
            OutputFormat: ReportOutputFormat.Pdf,
            SaveName: "Test Save Name",
            Enabled: true,
            TabsBetweenDates: 2,
            LeadingTabs: 2);
        var begin = new DateTime(2026, 9, 1);
        var end = new DateTime(2026, 9, 28);

        var plan = ReportParameterKeyPlan.Build(entry, begin, end);

        var expected = new[]
        {
            ReportParameterKeyAction.Tab(),
            ReportParameterKeyAction.Tab(),
            ReportParameterKeyAction.TypeText("09-01-2026"),
            ReportParameterKeyAction.Tab(),
            ReportParameterKeyAction.Tab(),
            ReportParameterKeyAction.TypeText("09-28-26"),
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
            ReportParameterKeyAction.TypeText("08-01-2026"),
            ReportParameterKeyAction.Tab(),
            ReportParameterKeyAction.TypeText("08-31-26"),
            ReportParameterKeyAction.F12(),
        };

        Assert.Equal(expected, plan);
    }

    [Fact]
    public void AsOfDateEntryTypesOnlyTheEndDateThenF12()
    {
        // ThirdPartyAgedTrialBalanceKey's macro: %date_text%<F12> - no
        // leading tabs, single field, and that field uses %date_text%
        // (FormatEnd, 2-digit year) - every AsOfDate macro types
        // %date_text%, never %start_date_text%.
        var entry = ReportCatalog.FindByKey(ReportCatalog.ThirdPartyAgedTrialBalanceKey)!;
        var begin = new DateTime(2026, 8, 1);
        var end = new DateTime(2026, 8, 31);

        var plan = ReportParameterKeyPlan.Build(entry, begin, end);

        var expected = new[]
        {
            ReportParameterKeyAction.TypeText("08-31-26"),
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
            ReportParameterKeyAction.TypeText("08-31-26"),
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
    public void EveryTypeTextActionMatchesItsExpectedDashedDateFormat()
    {
        // W-T92 round 5: the two TypeText formats are no longer identical
        // (bare 8-digit MMddyyyy) - the FIRST TypeText in a plan (Begin, or
        // the only one for AsOfDate/single-field reports) is FormatBegin
        // ("MM-dd-yyyy", 4-digit year); every subsequent one (End) is
        // FormatEnd ("MM-dd-yy", 2-digit year). AsOfDate's single field is
        // always the macro's %date_text% (End format) - see
        // AsOfDateEntryTypesOnlyTheEndDateThenF12 above - so this walks
        // every entry's own TypeText count rather than assuming position 0
        // is always "Begin".
        var beginFormat = new Regex(@"^\d{2}-\d{2}-\d{4}$");
        var endFormat = new Regex(@"^\d{2}-\d{2}-\d{2}$");

        foreach (var entry in ReportCatalog.All.Where(e => e.ParameterKind != ReportParameterKind.PaymentsSearch))
        {
            var plan = ReportParameterKeyPlan.Build(entry, new DateTime(2026, 1, 5), new DateTime(2026, 12, 31));
            var typeTextActions = plan.Where(a => a.Kind == ReportParameterKeyActionKind.TypeText).ToList();

            if (entry.ParameterKind == ReportParameterKind.AsOfDate)
            {
                var single = Assert.Single(typeTextActions);
                Assert.Matches(endFormat, single.Text);
                continue;
            }

            // DateRange: exactly two TypeText actions, Begin then End.
            Assert.Equal(2, typeTextActions.Count);
            Assert.Matches(beginFormat, typeTextActions[0].Text);
            Assert.Matches(endFormat, typeTextActions[1].Text);
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
        // W-T92 round 5 reviewer fix: the original data here ("01012020")
        // has no separators, so it's only ONE digit run, not three - under
        // the round 5 normalizer that's "cannot judge" (Unavailable), not
        // a mismatch signal. Real field readbacks always carry separators
        // (slashes or dashes) between month/day/year, so this now uses a
        // properly 3-grouped, genuinely different date instead.
        Assert.Equal(ReadbackDecision.Mismatch, ReadbackEvaluator.Decide("01/01/2020", "09-21-2026"));
    }

    // --- ReadbackEvaluator.Decide normalization (W-T92 round 5): the
    // field's own display formatting (slashes vs. dashes, no leading
    // zeros, 2- vs. 4-digit year) is not a real mismatch. ---

    [Fact]
    public void DecideIsOkWhenTheFieldRedisplaysDashesAsSlashes()
    {
        Assert.Equal(ReadbackDecision.Ok, ReadbackEvaluator.Decide("09/01/2026", "09-01-2026"));
    }

    [Fact]
    public void DecideIsOkWhenTheFieldDropsLeadingZerosOnMonthAndDay()
    {
        Assert.Equal(ReadbackDecision.Ok, ReadbackEvaluator.Decide("9/1/2026", "09-01-2026"));
    }

    [Fact]
    public void DecideIsOkWhenTheFieldShowsAFourDigitYearForATwoDigitExpectedYear()
    {
        Assert.Equal(ReadbackDecision.Ok, ReadbackEvaluator.Decide("09/28/2026", "09-28-26"));
    }

    [Fact]
    public void DecideIsMismatchWhenTheMonthGenuinelyDiffers()
    {
        Assert.Equal(ReadbackDecision.Mismatch, ReadbackEvaluator.Decide("10/01/2026", "09-01-2026"));
    }

    [Fact]
    public void DecideIsUnavailableWhenReadbackIsNullRegardlessOfExpectedFormat()
    {
        Assert.Equal(ReadbackDecision.Unavailable, ReadbackEvaluator.Decide(null, "09-01-2026"));
    }

    // --- ReadbackEvaluator.Decide (reviewer round 5, blocking finding 2):
    // null/empty/whitespace and "too few digit groups to judge" readbacks
    // must be Unavailable, never Mismatch - a Mismatch fires the
    // destructive Ctrl+A/Ctrl+V retry, and could abort a run whose date
    // was actually correct. Confirmed reachable: PioneerReportDriver.
    // TryReadFocusedFieldValue can legitimately return "" (a real but
    // unhelpful ValuePattern/LegacyIAccessible value), and a readback with
    // a time suffix ("09/28/2026 12:00:00 AM") has 6 digit groups, not 3. ---

    [Fact]
    public void DecideIsUnavailableNotMismatchForAnEmptyStringReadback()
    {
        // An empty string is a real (if unhelpful) readback - but it has
        // ZERO digit groups, so there's nothing to judge it against; it
        // must not fire the retry.
        Assert.Equal(ReadbackDecision.Unavailable, ReadbackEvaluator.Decide(string.Empty, "09-21-2026"));
    }

    [Fact]
    public void DecideIsUnavailableForAWhitespaceOnlyReadback()
    {
        Assert.Equal(ReadbackDecision.Unavailable, ReadbackEvaluator.Decide("   ", "09-21-2026"));
    }

    [Fact]
    public void DecideIsUnavailableForAReadbackWithFewerThanThreeDigitGroups()
    {
        // Only month/year, e.g. a partially-populated or truncated field -
        // two digit groups, not enough to judge month+day+year against.
        Assert.Equal(ReadbackDecision.Unavailable, ReadbackEvaluator.Decide("09/2026", "09-21-2026"));
    }

    [Fact]
    public void DecideIsOkForADateWithATimeSuffixUsingOnlyTheFirstThreeGroups()
    {
        // A readback with MORE than three digit groups (a date+time value)
        // still gets judged - on its first three groups (month, day,
        // year), ignoring the time suffix's own digit groups entirely.
        Assert.Equal(ReadbackDecision.Ok, ReadbackEvaluator.Decide("09/28/2026 12:00:00 AM", "09-28-26"));
    }

    [Fact]
    public void DecideIsMismatchForADateWithATimeSuffixWhenTheDateItselfDiffers()
    {
        Assert.Equal(ReadbackDecision.Mismatch, ReadbackEvaluator.Decide("10/28/2026 12:00:00 AM", "09-28-26"));
    }
}
