using System.Collections.Generic;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.OrderAssist.Geometry;
using Xunit;

namespace RxVerifyOverlay.Tests.OrderAssist;

/// <summary>
/// Unit tests for RowPitchEstimator + RowBounds.ComputeUniform (round 3,
/// Will verbatim: "Make the height match the height of the row too (which
/// should be the same for all rows), so it's easier to read"). All
/// coordinates are synthetic.
/// </summary>
public class RowPitchEstimatorTests
{
    private static OcrWord Word(double x, double y, double w, double h) =>
        new() { Text = "x", X = x, Y = y, W = w, H = h };

    // ---- EstimateCanonicalHeight -------------------------------------------

    [Fact]
    public void UniformlySpacedRowsYieldTheirOwnCommonPitch()
    {
        var rows = new List<IReadOnlyList<OcrWord>>
        {
            new List<OcrWord> { Word(0, 40, 20, 12) },  // center 46
            new List<OcrWord> { Word(0, 60, 20, 12) },  // center 66 (pitch 20)
            new List<OcrWord> { Word(0, 80, 20, 12) },  // center 86 (pitch 20)
        };

        Assert.Equal(20, RowPitchEstimator.EstimateCanonicalHeight(rows));
    }

    [Fact]
    public void OneOutlierGapDoesNotSkewTheMedianPitch()
    {
        var rows = new List<IReadOnlyList<OcrWord>>
        {
            new List<OcrWord> { Word(0, 40, 20, 12) },  // center 46
            new List<OcrWord> { Word(0, 60, 20, 12) },  // center 66 (pitch 20)
            new List<OcrWord> { Word(0, 80, 20, 12) },  // center 86 (pitch 20)
            new List<OcrWord> { Word(0, 150, 20, 12) }, // center 156 (pitch 70 -- a missed/merged row)
        };

        // Median of [20, 20, 70] is 20 -- the two normal gaps outvote the
        // one outlier.
        Assert.Equal(20, RowPitchEstimator.EstimateCanonicalHeight(rows));
    }

    [Fact]
    public void RowsWithNoReadableWordsAreIgnoredWhenEstimatingPitch()
    {
        var rows = new List<IReadOnlyList<OcrWord>>
        {
            new List<OcrWord> { Word(0, 40, 20, 12) },
            new List<OcrWord>(), // blank/unreadable row -- no bounds at all
            new List<OcrWord> { Word(0, 60, 20, 12) },
            new List<OcrWord> { Word(0, 80, 20, 12) },
        };

        Assert.Equal(20, RowPitchEstimator.EstimateCanonicalHeight(rows));
    }

    [Fact]
    public void SingleRowFallsBackToItsOwnRawHeight()
    {
        var rows = new List<IReadOnlyList<OcrWord>>
        {
            new List<OcrWord> { Word(0, 40, 20, 15) }, // Top=40, Bottom=55 -> height 15
        };

        Assert.Equal(15, RowPitchEstimator.EstimateCanonicalHeight(rows));
    }

    [Fact]
    public void NoRowsAtAllYieldsZero()
    {
        Assert.Equal(0, RowPitchEstimator.EstimateCanonicalHeight(new List<IReadOnlyList<OcrWord>>()));
    }

    // ---- RowBounds.ComputeUniform -------------------------------------------

    [Fact]
    public void ComputeUniformKeepsHorizontalExtentButForcesTheGivenHeight()
    {
        var row = new List<OcrWord> { Word(10, 40, 30, 8) }; // Top=40, Bottom=48, center=44

        var uniform = RowBounds.ComputeUniform(row, canonicalHeight: 20);

        Assert.NotNull(uniform);
        Assert.Equal(10, uniform!.Value.Left);
        Assert.Equal(40, uniform.Value.Right); // 10 + 30
        Assert.Equal(34, uniform.Value.Top);    // center 44 - 10
        Assert.Equal(54, uniform.Value.Bottom); // center 44 + 10
    }

    [Fact]
    public void TwoRowsWithDifferentRawHeightsProduceTheSameUniformHeight()
    {
        var shortRow = new List<OcrWord> { Word(10, 100, 30, 8) };  // raw height 8
        var tallRow = new List<OcrWord> { Word(10, 200, 30, 30) };  // raw height 30

        var a = RowBounds.ComputeUniform(shortRow, canonicalHeight: 18);
        var b = RowBounds.ComputeUniform(tallRow, canonicalHeight: 18);

        Assert.Equal(18, a!.Value.Bottom - a.Value.Top);
        Assert.Equal(18, b!.Value.Bottom - b.Value.Top);
    }

    [Fact]
    public void ZeroOrNegativeCanonicalHeightFallsBackToRawCompute()
    {
        var row = new List<OcrWord> { Word(10, 40, 30, 8) };

        var uniform = RowBounds.ComputeUniform(row, canonicalHeight: 0);
        var raw = RowBounds.Compute(row);

        Assert.Equal(raw, uniform);
    }

    [Fact]
    public void BlankRowStaysNullRegardlessOfCanonicalHeight()
    {
        Assert.Null(RowBounds.ComputeUniform(new List<OcrWord>(), canonicalHeight: 20));
    }
}
