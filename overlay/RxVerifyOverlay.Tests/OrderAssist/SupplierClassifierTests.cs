using RxVerifyOverlay.OrderAssist.Decisions;
using Xunit;

namespace RxVerifyOverlay.Tests.OrderAssist;

/// <summary>Unit tests for SupplierClassifier — McKesson vs. secondary classification of a Supplier cell's OCR'd text.</summary>
public class SupplierClassifierTests
{
    [Theory]
    [InlineData("McKesson")]
    [InlineData("MCKESSON")]
    [InlineData("mckesson")]
    [InlineData(" McKesson ")]
    [InlineData("McKesson Corp")]
    public void RecognizesMcKessonCaseInsensitively(string supplier)
    {
        Assert.True(SupplierClassifier.IsMcKesson(supplier));
    }

    [Theory]
    [InlineData("IPC")]
    [InlineData("ParMed")]
    [InlineData("ANDA")]
    [InlineData("TopRx")]
    [InlineData("")]
    [InlineData(null)]
    public void EverythingElseIsSecondary(string? supplier)
    {
        Assert.False(SupplierClassifier.IsMcKesson(supplier));
    }
}
