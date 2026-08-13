using RxVerifyOverlay.OrderAssist.Windows;
using Xunit;

namespace RxVerifyOverlay.Tests.OrderAssist;

/// <summary>Unit tests for OrderAssistWindowClassifier — title-prefix matching for the two Pioneer windows this module watches.</summary>
public class OrderAssistWindowClassifierTests
{
    [Theory]
    [InlineData("Create Recommended Orders")]
    [InlineData("create recommended orders")] // case-insensitive
    [InlineData("Create Recommended Orders - PioneerRx")] // tolerant of a trailing suffix
    public void RecognizesTheCreateRecommendedOrdersWindow(string title)
    {
        Assert.Equal(OrderAssistWindowKind.CreateRecommendedOrders, OrderAssistWindowClassifier.Classify(title));
    }

    [Theory]
    [InlineData("Recommended Order - Catalog Item Substitution Selection")]
    [InlineData("recommended order - catalog item substitution selection")]
    [InlineData("Recommended Order - Catalog Item Substitution Selection - PioneerRx")]
    public void RecognizesTheCatalogSubstitutionWindow(string title)
    {
        Assert.Equal(OrderAssistWindowKind.CatalogSubstitution, OrderAssistWindowClassifier.Classify(title));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Pre-Check Rx - 1234567 - Patient Name")]
    [InlineData("Recommended Order")] // a real Pioneer title, but not either target window
    public void EverythingElseClassifiesAsNone(string? title)
    {
        Assert.Equal(OrderAssistWindowKind.None, OrderAssistWindowClassifier.Classify(title));
    }
}
