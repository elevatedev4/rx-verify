using RxVerifyOverlay.OrderAssist.Decisions;
using Xunit;

namespace RxVerifyOverlay.Tests.OrderAssist;

/// <summary>Unit tests for ZeroQuantityDetector — the owner's "nothing in Order Quantity should be 0" rule applied to one already-resolved cell's text.</summary>
public class ZeroQuantityDetectorTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("0.00")]
    [InlineData("0.0000")]
    [InlineData("$0.00")]
    [InlineData("-0")]
    public void RecognizedZeroFormatsClassifyAsZero(string text)
    {
        Assert.Equal(ZeroCellState.Zero, ZeroQuantityDetector.Classify(text));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("9")]
    [InlineData("-2.0000")]
    [InlineData("0.01")]
    public void NonZeroNumbersClassifyAsNonZero(string text)
    {
        Assert.Equal(ZeroCellState.NonZero, ZeroQuantityDetector.Classify(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    public void BlankOrUnreadableCellsClassifyAsUnknownNeverZero(string? text)
    {
        // Judgment call (see class doc): a cell OrderAssist couldn't read
        // must never be treated as a false-alarm zero.
        Assert.Equal(ZeroCellState.Unknown, ZeroQuantityDetector.Classify(text));
    }
}
