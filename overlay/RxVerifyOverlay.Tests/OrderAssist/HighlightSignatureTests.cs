using System.Collections.Generic;
using RxVerifyOverlay.OrderAssist;
using RxVerifyOverlay.OrderAssist.Scanning;
using Xunit;

namespace RxVerifyOverlay.Tests.OrderAssist;

/// <summary>
/// Unit tests for HighlightSignature — the pure comparison-key builder
/// HighlightStabilityPolicy compares tick to tick (W-T85 bug 3 fix). All
/// row indices/savings text below are synthetic.
/// </summary>
public class HighlightSignatureTests
{
    // ---- ForZeroQuantityHighlights ---------------------------------------

    [Fact]
    public void EmptyHighlightListYieldsEmptySignature()
    {
        Assert.Equal("", HighlightSignature.ForZeroQuantityHighlights(new List<CreateRecommendedOrdersScanner.ZeroCellHighlight>()));
    }

    [Fact]
    public void SameRowIndicesInDifferentOrderProduceTheSameSignature()
    {
        var a = new List<CreateRecommendedOrdersScanner.ZeroCellHighlight>
        {
            new(3, 0, 0, 10, 10),
            new(1, 0, 0, 10, 10),
        };
        var b = new List<CreateRecommendedOrdersScanner.ZeroCellHighlight>
        {
            new(1, 0, 0, 10, 10),
            new(3, 0, 0, 10, 10),
        };

        Assert.Equal(HighlightSignature.ForZeroQuantityHighlights(a), HighlightSignature.ForZeroQuantityHighlights(b));
    }

    [Fact]
    public void DifferentRowIndicesProduceDifferentSignatures()
    {
        var a = new List<CreateRecommendedOrdersScanner.ZeroCellHighlight> { new(1, 0, 0, 10, 10) };
        var b = new List<CreateRecommendedOrdersScanner.ZeroCellHighlight> { new(2, 0, 0, 10, 10) };

        Assert.NotEqual(HighlightSignature.ForZeroQuantityHighlights(a), HighlightSignature.ForZeroQuantityHighlights(b));
    }

    [Fact]
    public void SlightlyDifferentPixelBoundsOnTheSameRowStillProduceTheSameSignature()
    {
        // The whole point: signature identity is ROW-based, not
        // pixel-geometry-based, so ordinary OCR jitter on the SAME logical
        // row never looks like a "change" to HighlightStabilityPolicy.
        var a = new List<CreateRecommendedOrdersScanner.ZeroCellHighlight> { new(0, 130.0, 40.0, 140.0, 52.0) };
        var b = new List<CreateRecommendedOrdersScanner.ZeroCellHighlight> { new(0, 130.4, 40.1, 140.6, 52.2) };

        Assert.Equal(HighlightSignature.ForZeroQuantityHighlights(a), HighlightSignature.ForZeroQuantityHighlights(b));
    }

    // ---- ForCatalogAnnotations --------------------------------------------

    [Fact]
    public void AllNullAnnotationsYieldEmptySignature()
    {
        Assert.Equal("", HighlightSignature.ForCatalogAnnotations(CatalogSubstitutionScanner.CatalogAnnotations.Empty));
    }

    [Fact]
    public void SameGreenPickProducesTheSameSignature()
    {
        var a = new CatalogSubstitutionScanner.CatalogAnnotations(
            new CatalogSubstitutionScanner.SubstitutionHighlight(0, 10, 40, 400, 52, "30% savings"),
            null, null, null, null);
        var b = new CatalogSubstitutionScanner.CatalogAnnotations(
            new CatalogSubstitutionScanner.SubstitutionHighlight(0, 10.2, 40.1, 400.3, 52.0, "30% savings"),
            null, null, null, null);

        Assert.Equal(HighlightSignature.ForCatalogAnnotations(a), HighlightSignature.ForCatalogAnnotations(b));
    }

    [Fact]
    public void ADifferentGreenPickRowProducesADifferentSignature()
    {
        var a = new CatalogSubstitutionScanner.CatalogAnnotations(
            new CatalogSubstitutionScanner.SubstitutionHighlight(0, 10, 40, 400, 52, "30% savings"),
            null, null, null, null);
        var b = new CatalogSubstitutionScanner.CatalogAnnotations(
            new CatalogSubstitutionScanner.SubstitutionHighlight(2, 10, 80, 400, 92, "30% savings"),
            null, null, null, null);

        Assert.NotEqual(HighlightSignature.ForCatalogAnnotations(a), HighlightSignature.ForCatalogAnnotations(b));
    }

    [Fact]
    public void ADifferentSavingsPercentOnTheSameRowProducesADifferentSignature()
    {
        // A row staying "the green pick" but its computed savings changing
        // (e.g. a price genuinely updated) is still a meaningful content
        // change worth re-displaying, not just pixel jitter.
        var a = new CatalogSubstitutionScanner.CatalogAnnotations(
            new CatalogSubstitutionScanner.SubstitutionHighlight(0, 10, 40, 400, 52, "30% savings"),
            null, null, null, null);
        var b = new CatalogSubstitutionScanner.CatalogAnnotations(
            new CatalogSubstitutionScanner.SubstitutionHighlight(0, 10, 40, 400, 52, "40% savings"),
            null, null, null, null);

        Assert.NotEqual(HighlightSignature.ForCatalogAnnotations(a), HighlightSignature.ForCatalogAnnotations(b));
    }

    [Fact]
    public void AddingAYellowHighlightToTheSameGreenPickProducesADifferentSignature()
    {
        var green = new CatalogSubstitutionScanner.SubstitutionHighlight(0, 10, 40, 400, 52, "30% savings");
        var a = new CatalogSubstitutionScanner.CatalogAnnotations(green, null, null, null, null);
        var b = new CatalogSubstitutionScanner.CatalogAnnotations(green, new CatalogSubstitutionScanner.RowMarker(1, 10, 60, 400, 72, null), null, null, null);

        Assert.NotEqual(HighlightSignature.ForCatalogAnnotations(a), HighlightSignature.ForCatalogAnnotations(b));
    }
}
