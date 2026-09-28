using System.Linq;
using RxVerifyOverlay.Reports;
using Xunit;

namespace RxVerifyOverlay.Tests.Reports;

/// <summary>Unit tests for Reports/ReportCatalog.cs — pure data, no UIA/WPF involved.</summary>
public class ReportCatalogTests
{
    [Fact]
    public void HasExactlyEightEntries()
    {
        Assert.Equal(8, ReportCatalog.All.Count);
    }

    [Fact]
    public void AllKeysAreUnique()
    {
        var keys = ReportCatalog.All.Select(e => e.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void SevenEntriesAreEnabledAndPosDailyIsNot()
    {
        var enabled = ReportCatalog.All.Where(e => e.Enabled).ToList();
        Assert.Equal(7, enabled.Count);

        var posDaily = ReportCatalog.FindByKey(ReportCatalog.PosDailyKey);
        Assert.NotNull(posDaily);
        Assert.False(posDaily!.Enabled);
        Assert.Contains("coming next", posDaily.DisplayName);
    }

    [Fact]
    public void InventoryValuationIsAsOfDate()
    {
        var entry = ReportCatalog.FindByKey(ReportCatalog.InventoryValuationKey);
        Assert.NotNull(entry);
        Assert.Equal(ReportParameterKind.AsOfDate, entry!.ParameterKind);
    }

    [Fact]
    public void ThirdPartyAgedTrialBalanceIsAsOfDate()
    {
        var entry = ReportCatalog.FindByKey(ReportCatalog.ThirdPartyAgedTrialBalanceKey);
        Assert.NotNull(entry);
        Assert.Equal(ReportParameterKind.AsOfDate, entry!.ParameterKind);
    }

    /// <summary>
    /// Round 3 fix: the owner's real "Run Financial Reports" grid screenshot
    /// has no row literally named "Third Party Aged Trial Balance As of
    /// Date" — the closest verbatim row is "Third Party Reconciliation
    /// Account Aged Trial Balance". PioneerRowText is updated to the real
    /// name; the old guessed name is kept as a secondary alias.
    /// </summary>
    [Fact]
    public void ThirdPartyAgedTrialBalanceUsesTheRealRowNameWithTheOldNameAsAnAlias()
    {
        var entry = ReportCatalog.FindByKey(ReportCatalog.ThirdPartyAgedTrialBalanceKey);

        Assert.NotNull(entry);
        Assert.Equal("Third Party Reconciliation Account Aged Trial Balance", entry!.PioneerRowText);
        Assert.Equal("Third Party Aged Trial Balance As of Date", entry.PioneerRowTextAlias);
    }

    [Fact]
    public void NoOtherEntryHasARowTextAlias()
    {
        foreach (var entry in ReportCatalog.All.Where(e => e.Key != ReportCatalog.ThirdPartyAgedTrialBalanceKey))
        {
            Assert.Null(entry.PioneerRowTextAlias);
        }
    }

    [Theory]
    [InlineData(ReportCatalog.ArAgedTrialBalanceKey)]
    [InlineData(ReportCatalog.ThirdPartyControlBalanceKey)]
    [InlineData(ReportCatalog.InventoryControlBalanceKey)]
    [InlineData(ReportCatalog.SalesSummaryKey)]
    public void FinancialDateRangeReportsAreDateRangeKind(string key)
    {
        var entry = ReportCatalog.FindByKey(key);
        Assert.NotNull(entry);
        Assert.Equal(ReportParameterKind.DateRange, entry!.ParameterKind);
    }

    [Fact]
    public void PaymentsIsPaymentsSearchKindAndXlsx()
    {
        var entry = ReportCatalog.FindByKey(ReportCatalog.PaymentsKey);
        Assert.NotNull(entry);
        Assert.Equal(ReportParameterKind.PaymentsSearch, entry!.ParameterKind);
        Assert.Equal(ReportOutputFormat.Xlsx, entry.OutputFormat);
    }

    [Fact]
    public void EveryOtherEntryIsPdf()
    {
        foreach (var entry in ReportCatalog.All.Where(e => e.Key != ReportCatalog.PaymentsKey))
        {
            Assert.Equal(ReportOutputFormat.Pdf, entry.OutputFormat);
        }
    }

    [Fact]
    public void FindByKeyReturnsNullForUnknownKey()
    {
        Assert.Null(ReportCatalog.FindByKey("does-not-exist"));
    }

    // --- Round 4 (W-T92 round 4): LeadingTabs/TrailingKeys, one value per report's own macro line ---

    [Theory]
    [InlineData(ReportCatalog.ArAgedTrialBalanceKey, 2)]
    [InlineData(ReportCatalog.ThirdPartyAgedTrialBalanceKey, 0)]
    [InlineData(ReportCatalog.ThirdPartyControlBalanceKey, 0)]
    [InlineData(ReportCatalog.InventoryValuationKey, 0)]
    [InlineData(ReportCatalog.InventoryControlBalanceKey, 0)]
    [InlineData(ReportCatalog.SalesSummaryKey, 0)]
    public void EveryEnabledFinancialReportHasALeadingTabsValueFromItsOwnMacroLine(string key, int expectedLeadingTabs)
    {
        var entry = ReportCatalog.FindByKey(key);
        Assert.NotNull(entry);
        Assert.Equal(expectedLeadingTabs, entry!.LeadingTabs);
    }

    [Fact]
    public void OnlyArAgedTrialBalanceHasNonZeroLeadingTabs()
    {
        foreach (var entry in ReportCatalog.All.Where(e => e.Key != ReportCatalog.ArAgedTrialBalanceKey))
        {
            Assert.Equal(0, entry.LeadingTabs);
        }
    }

    [Fact]
    public void OnlyInventoryValuationHasTrailingKeys()
    {
        var inventoryValuation = ReportCatalog.FindByKey(ReportCatalog.InventoryValuationKey)!;
        Assert.Equal(
            new[] { ReportParameterKeyActionKind.Tab, ReportParameterKeyActionKind.ArrowDown },
            inventoryValuation.TrailingKeys);

        foreach (var entry in ReportCatalog.All.Where(e => e.Key != ReportCatalog.InventoryValuationKey))
        {
            Assert.Null(entry.TrailingKeys);
        }
    }
}
