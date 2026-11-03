using RxVerifyOverlay.OrderAssist.Windows;
using Xunit;

namespace RxVerifyOverlay.Tests.OrderAssist;

/// <summary>Unit tests for OrderAssistWindowClassifier.IsOrderScreenTitle -- which window titles count as "an order screen is up" for automatic Order/Verify switching. Synthetic titles only.</summary>
public class OrderScreenTitleTests
{
    [Theory]
    [InlineData("Create Recommended Orders")]
    [InlineData("create recommended orders")]
    [InlineData("CREATE RECOMMENDED ORDERS - PioneerRx")]
    [InlineData("Recommended Order - Catalog Item Substitution Selection")]
    [InlineData("recommended order - catalog item substitution selection")]
    [InlineData("Recommended Order")]
    [InlineData("Recommended Orders")]
    [InlineData("  Recommended Order - Anything Else  ")]
    public void OrderScreenTitlesMatch(string title)
    {
        Assert.True(OrderAssistWindowClassifier.IsOrderScreenTitle(title));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Pre-Check Rx - 1234567 - Test, Patient")]
    [InlineData("PioneerRx")]
    [InlineData("Order Entry")]
    [InlineData("My Recommended Order notes - Notepad")] // prefix only, not substring
    [InlineData("Orders - Recommended")]
    public void OtherTitlesDoNotMatch(string? title)
    {
        Assert.False(OrderAssistWindowClassifier.IsOrderScreenTitle(title));
    }

    [Fact]
    public void BothScannerTargetWindowsAreOrderScreens()
    {
        Assert.True(OrderAssistWindowClassifier.IsOrderScreenTitle(OrderAssistWindowClassifier.CreateRecommendedOrdersTitle));
        Assert.True(OrderAssistWindowClassifier.IsOrderScreenTitle(OrderAssistWindowClassifier.CatalogSubstitutionTitle));
    }
}
