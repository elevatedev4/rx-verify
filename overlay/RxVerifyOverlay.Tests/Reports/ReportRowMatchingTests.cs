using System.Collections.Generic;
using RxVerifyOverlay.Reports;
using Xunit;

namespace RxVerifyOverlay.Tests.Reports;

/// <summary>Unit tests for Reports/ReportRowMatching.cs — pure row-text matching/redaction/sequencing logic, no FlaUI/UIA involved.</summary>
public class ReportRowMatchingTests
{
    public class ReportRowTextMatcherTests
    {
        [Fact]
        public void NormalizeTrimsAndCollapsesWhitespace()
        {
            Assert.Equal("Inventory Valuation", ReportRowTextMatcher.Normalize("  Inventory   Valuation \t"));
        }

        [Fact]
        public void NormalizeReturnsEmptyForNullOrWhitespace()
        {
            Assert.Equal(string.Empty, ReportRowTextMatcher.Normalize(null));
            Assert.Equal(string.Empty, ReportRowTextMatcher.Normalize("   "));
        }

        [Fact]
        public void IsExactMatch_TrueForIdenticalText()
        {
            Assert.True(ReportRowTextMatcher.IsExactMatch("Inventory Valuation", "Inventory Valuation"));
        }

        [Fact]
        public void IsExactMatch_IsCaseInsensitive()
        {
            Assert.True(ReportRowTextMatcher.IsExactMatch("inventory valuation", "Inventory Valuation"));
        }

        [Fact]
        public void IsExactMatch_TrueDespiteIrregularWhitespace()
        {
            Assert.True(ReportRowTextMatcher.IsExactMatch("Inventory   Valuation", "Inventory Valuation"));
        }

        /// <summary>The GOAL brief's own named collision: "Inventory Valuation" must never match "Inventory Valuation (With Last Supplier)" or "Inventory Valuation for Excel".</summary>
        [Theory]
        [InlineData("Inventory Valuation (With Last Supplier)")]
        [InlineData("Inventory Valuation for Excel")]
        [InlineData("Inventory Valuation Detail")]
        public void IsExactMatch_FalseForALongerCollidingRowName(string candidate)
        {
            Assert.False(ReportRowTextMatcher.IsExactMatch(candidate, "Inventory Valuation"));
        }

        [Fact]
        public void IsExactMatch_FalseForAShorterPrefixOfTheTarget()
        {
            Assert.False(ReportRowTextMatcher.IsExactMatch("Inventory", "Inventory Valuation"));
        }

        [Fact]
        public void IsExactMatch_FalseWhenEitherSideIsEmpty()
        {
            Assert.False(ReportRowTextMatcher.IsExactMatch("", "Inventory Valuation"));
            Assert.False(ReportRowTextMatcher.IsExactMatch("Inventory Valuation", ""));
            Assert.False(ReportRowTextMatcher.IsExactMatch(null, "Inventory Valuation"));
        }
    }

    public class ReportRowNameRedactionTests
    {
        [Fact]
        public void KnownCatalogRowNameIsSafe()
        {
            Assert.True(ReportRowNameRedaction.IsKnownSafeValue("Inventory Valuation"));
            Assert.Equal("Inventory Valuation", ReportRowNameRedaction.RedactIfNeeded("Inventory Valuation"));
        }

        [Fact]
        public void KnownCatalogRowNameAliasIsSafe()
        {
            // Round 3 fix: the alias on ThirdPartyAgedTrialBalanceKey.
            Assert.True(ReportRowNameRedaction.IsKnownSafeValue("Third Party Aged Trial Balance As of Date"));
        }

        [Theory]
        [InlineData("Report Name")]
        [InlineData("Report Description")]
        public void KnownColumnHeaderIsSafe(string header)
        {
            Assert.True(ReportRowNameRedaction.IsKnownSafeValue(header));
            Assert.Equal(header, ReportRowNameRedaction.RedactIfNeeded(header));
        }

        [Fact]
        public void UnknownValueIsRedacted()
        {
            Assert.False(ReportRowNameRedaction.IsKnownSafeValue("123-45-6789 Jane Doe"));
            Assert.Equal(UiaNameRedaction.RedactedName, ReportRowNameRedaction.RedactIfNeeded("123-45-6789 Jane Doe"));
        }

        [Fact]
        public void EmptyOrNullIsRedacted()
        {
            Assert.False(ReportRowNameRedaction.IsKnownSafeValue(null));
            Assert.False(ReportRowNameRedaction.IsKnownSafeValue(""));
        }

        [Fact]
        public void MatchingIsCaseInsensitiveAndWhitespaceTolerant()
        {
            Assert.True(ReportRowNameRedaction.IsKnownSafeValue("  inventory   valuation "));
        }
    }

    public class ReportRowSelectionSequencerTests
    {
        [Fact]
        public void BuildRowTextCandidates_ReturnsOnlyPrimaryWhenNoAliasIsSet()
        {
            var entry = ReportCatalog.FindByKey(ReportCatalog.InventoryValuationKey)!;

            var candidates = ReportRowSelectionSequencer.BuildRowTextCandidates(entry);

            Assert.Equal(new[] { "Inventory Valuation" }, candidates);
        }

        [Fact]
        public void BuildRowTextCandidates_ReturnsPrimaryThenAliasInOrder()
        {
            var entry = ReportCatalog.FindByKey(ReportCatalog.ThirdPartyAgedTrialBalanceKey)!;

            var candidates = ReportRowSelectionSequencer.BuildRowTextCandidates(entry);

            Assert.Equal(
                new[] { "Third Party Reconciliation Account Aged Trial Balance", "Third Party Aged Trial Balance As of Date" },
                candidates);
        }

        [Fact]
        public void TrySelect_StopsAtTheFirstSuccessfulStrategy()
        {
            var attempts = new List<(string RowText, int Strategy)>();
            bool Fails(string rowText) => false;
            bool Succeeds(string rowText) => true;

            var result = ReportRowSelectionSequencer.TrySelect(
                new[] { "Row A" },
                new List<System.Func<string, bool>> { Fails, Succeeds, Fails },
                (rowText, strategy) => attempts.Add((rowText, strategy)));

            Assert.True(result);
            // Strategy 3 never attempted - sequencer stopped at strategy 2's success.
            Assert.Equal(new[] { ("Row A", 1), ("Row A", 2) }, attempts);
        }

        [Fact]
        public void TrySelect_TriesTheAliasOnlyAfterAllStrategiesFailForThePrimaryRowText()
        {
            var attempts = new List<string>();
            bool SucceedsOnlyForAlias(string rowText) => rowText == "Alias Text";

            var result = ReportRowSelectionSequencer.TrySelect(
                new[] { "Primary Text", "Alias Text" },
                new List<System.Func<string, bool>> { SucceedsOnlyForAlias },
                (rowText, strategy) => attempts.Add(rowText));

            Assert.True(result);
            Assert.Equal(new[] { "Primary Text", "Alias Text" }, attempts);
        }

        [Fact]
        public void TrySelect_ReturnsFalseWhenEveryStrategyFailsForEveryCandidate()
        {
            bool AlwaysFails(string rowText) => false;

            var result = ReportRowSelectionSequencer.TrySelect(
                new[] { "Row A", "Row B" },
                new List<System.Func<string, bool>> { AlwaysFails, AlwaysFails, AlwaysFails });

            Assert.False(result);
        }

        [Fact]
        public void TrySelect_SkipsBlankRowTextCandidates()
        {
            var attempted = new List<string>();
            bool RecordAndFail(string rowText) { attempted.Add(rowText); return false; }

            ReportRowSelectionSequencer.TrySelect(
                new[] { "", "  ", "Row A" },
                new List<System.Func<string, bool>> { RecordAndFail });

            Assert.Equal(new[] { "Row A" }, attempted);
        }

        [Fact]
        public void TrySelect_TriesStrategiesInOrderWithinOneRowTextBeforeMovingOn()
        {
            var order = new List<int>();
            bool Strategy1(string rowText) { order.Add(1); return false; }
            bool Strategy2(string rowText) { order.Add(2); return false; }
            bool Strategy3(string rowText) { order.Add(3); return false; }

            ReportRowSelectionSequencer.TrySelect(
                new[] { "Row A" },
                new List<System.Func<string, bool>> { Strategy1, Strategy2, Strategy3 });

            Assert.Equal(new[] { 1, 2, 3 }, order);
        }
    }
}
