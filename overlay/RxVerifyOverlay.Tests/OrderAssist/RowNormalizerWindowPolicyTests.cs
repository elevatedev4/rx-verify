using RxVerifyOverlay.OrderAssist.Ocr;
using RxVerifyOverlay.OrderAssist.Windows;
using Xunit;

namespace RxVerifyOverlay.Tests.OrderAssist;

/// <summary>
/// Regression: after the local-contrast normalizer shipped, the Catalog Item
/// Substitution window's pricing analysis (savings badges / package markers)
/// stopped working in the pharmacy while the sort badge still worked. That
/// window must keep the pre-change (legacy) pixels by default.
/// </summary>
public class RowNormalizerWindowPolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("local")]
    [InlineData("garbage")]
    public void CatalogSubstitutionDefaultsToLegacy(string? setting)
    {
        Assert.Equal(RowNormalizerMode.Legacy, RowNormalizerWindowPolicy.Resolve(OrderAssistWindowKind.CatalogSubstitution, setting));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("local")]
    [InlineData("garbage")]
    public void CreateRecommendedOrdersDefaultsToLocal(string? setting)
    {
        Assert.Equal(RowNormalizerMode.Local, RowNormalizerWindowPolicy.Resolve(OrderAssistWindowKind.CreateRecommendedOrders, setting));
    }

    [Theory]
    [InlineData(OrderAssistWindowKind.CatalogSubstitution)]
    [InlineData(OrderAssistWindowKind.CreateRecommendedOrders)]
    public void LegacySettingForcesLegacyEverywhere(OrderAssistWindowKind kind)
    {
        Assert.Equal(RowNormalizerMode.Legacy, RowNormalizerWindowPolicy.Resolve(kind, " Legacy "));
    }

    [Theory]
    [InlineData(OrderAssistWindowKind.CatalogSubstitution)]
    [InlineData(OrderAssistWindowKind.CreateRecommendedOrders)]
    public void LocalAllSettingForcesLocalEverywhere(OrderAssistWindowKind kind)
    {
        Assert.Equal(RowNormalizerMode.Local, RowNormalizerWindowPolicy.Resolve(kind, "local-all"));
    }
}
