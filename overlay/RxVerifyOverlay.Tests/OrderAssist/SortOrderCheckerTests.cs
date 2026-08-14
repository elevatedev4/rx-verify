using System.Collections.Generic;
using RxVerifyOverlay.OrderAssist.Decisions;
using Xunit;

namespace RxVerifyOverlay.Tests.OrderAssist;

/// <summary>
/// Unit tests for SortOrderChecker — Will's round-2 sort-indicator rule
/// ("show right above rebate column ... sorted ascending by that
/// column"). All costs below are synthetic round numbers.
/// </summary>
public class SortOrderCheckerTests
{
    [Fact]
    public void AscendingValuesClassifyAsSorted()
    {
        var state = SortOrderChecker.Classify(new List<string?> { "3.00", "5.00", "7.00", "10.00" });
        Assert.Equal(SortIndicatorState.Sorted, state);
    }

    [Fact]
    public void TiedAdjacentValuesStillCountAsSortedNonDecreasing()
    {
        var state = SortOrderChecker.Classify(new List<string?> { "3.00", "5.00", "5.00", "10.00" });
        Assert.Equal(SortIndicatorState.Sorted, state);
    }

    [Fact]
    public void AnyDecreaseAnywhereClassifiesAsNotSorted()
    {
        var state = SortOrderChecker.Classify(new List<string?> { "5.00", "3.00", "3.00" });
        Assert.Equal(SortIndicatorState.NotSorted, state);
    }

    [Fact]
    public void UnparseableRowsAreIgnoredNotTreatedAsBreaksInSequence()
    {
        // 3.00 -> (unreadable) -> 5.00: still ascending once the
        // unreadable row is dropped from the comparison.
        var state = SortOrderChecker.Classify(new List<string?> { "3.00", "N/A", "5.00" });
        Assert.Equal(SortIndicatorState.Sorted, state);
    }

    [Fact]
    public void UnparseableRowsCanStillRevealAGenuineOutOfOrderPair()
    {
        var state = SortOrderChecker.Classify(new List<string?> { "3.00", "abc", "2.00" });
        Assert.Equal(SortIndicatorState.NotSorted, state);
    }

    [Theory]
    [MemberData(nameof(FewerThanTwoParseableCases))]
    public void FewerThanTwoParseableValuesClassifiesAsUnknown(List<string?> texts)
    {
        Assert.Equal(SortIndicatorState.Unknown, SortOrderChecker.Classify(texts));
    }

    public static IEnumerable<object[]> FewerThanTwoParseableCases()
    {
        yield return new object[] { new List<string?>() };
        yield return new object[] { new List<string?> { "5.00" } };
        yield return new object[] { new List<string?> { "5.00", "abc", "N/A" } };
        yield return new object[] { new List<string?> { null, "" } };
    }

    [Fact]
    public void DescribeReturnsNullForUnknownRatherThanGuessing()
    {
        Assert.Null(SortOrderChecker.Describe(SortIndicatorState.Unknown));
    }

    [Fact]
    public void DescribeReturnsDistinctTextForSortedAndNotSorted()
    {
        var sortedText = SortOrderChecker.Describe(SortIndicatorState.Sorted);
        var notSortedText = SortOrderChecker.Describe(SortIndicatorState.NotSorted);

        Assert.NotNull(sortedText);
        Assert.NotNull(notSortedText);
        Assert.NotEqual(sortedText, notSortedText);
        Assert.Contains("sorted", sortedText);
        Assert.Contains("not sorted", notSortedText);
    }
}
