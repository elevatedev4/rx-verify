using System.Collections.Generic;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.Reports;
using Xunit;

namespace RxVerifyOverlay.Tests.Reports;

/// <summary>
/// Unit tests for Reports/ReportGridOcr.cs — pure OCR-word matching, no
/// OCR engine/Bitmap/capture involved. Word coordinates loosely mimic the
/// owner's screenshot (~12px rows, "Report Name" / "Report Description"
/// header) purely as realistic shapes; the exact pixel values don't
/// matter to the assertions.
/// </summary>
public class ReportGridOcrMatcherTests
{
    private static OcrWord Word(string text, double x, double y, double w = 60, double h = 12) =>
        new() { Text = text, X = x, Y = y, W = w, H = h };

    public class GroupIntoLinesTests
    {
        [Fact]
        public void GroupsWordsOnTheSameRowIntoOneLine()
        {
            var words = new List<OcrWord>
            {
                Word("Inventory", 10, 130),
                Word("Valuation", 75, 131),
            };

            var lines = ReportGridOcrMatcher.GroupIntoLines(words);

            Assert.Single(lines);
            Assert.Equal(2, lines[0].Words.Count);
        }

        [Fact]
        public void SeparatesWordsOnDifferentRows()
        {
            var words = new List<OcrWord>
            {
                Word("Row1", 10, 130),
                Word("Row2", 10, 142),
                Word("Row3", 10, 154),
            };

            var lines = ReportGridOcrMatcher.GroupIntoLines(words);

            Assert.Equal(3, lines.Count);
        }

        [Fact]
        public void OrdersWordsWithinALineLeftToRight()
        {
            var words = new List<OcrWord>
            {
                Word("Valuation", 75, 130),
                Word("Inventory", 10, 130),
            };

            var lines = ReportGridOcrMatcher.GroupIntoLines(words);

            Assert.Equal("Inventory", lines[0].Words[0].Text);
            Assert.Equal("Valuation", lines[0].Words[1].Text);
        }

        [Fact]
        public void OrdersLinesTopToBottom()
        {
            var words = new List<OcrWord>
            {
                Word("Second", 10, 142),
                Word("First", 10, 130),
            };

            var lines = ReportGridOcrMatcher.GroupIntoLines(words);

            Assert.Equal("First", lines[0].Words[0].Text);
            Assert.Equal("Second", lines[1].Words[0].Text);
        }

        [Fact]
        public void IgnoresBlankWords()
        {
            var words = new List<OcrWord> { Word("", 10, 130), Word("Real", 20, 130) };

            var lines = ReportGridOcrMatcher.GroupIntoLines(words);

            Assert.Single(lines);
            Assert.Single(lines[0].Words);
        }
    }

    public class FindReportDescriptionColumnXTests
    {
        [Fact]
        public void FindsTheColumnBoundaryFromTheHeaderRow()
        {
            var words = new List<OcrWord>
            {
                Word("Report", 10, 100),
                Word("Name", 75, 100),
                Word("Report", 300, 100),
                Word("Description", 365, 100),
            };

            var boundary = ReportGridOcrMatcher.FindReportDescriptionColumnX(words);

            Assert.Equal(300, boundary);
        }

        [Fact]
        public void ReturnsNullWhenTheHeaderIsNotPresent()
        {
            var words = new List<OcrWord> { Word("Inventory", 10, 130), Word("Valuation", 75, 130) };

            var boundary = ReportGridOcrMatcher.FindReportDescriptionColumnX(words);

            Assert.Null(boundary);
        }
    }

    public class FindReportNameLineTests
    {
        /// <summary>Builds the header row plus a handful of report rows from the owner's screenshot list, including the exact collision the GOAL brief calls out by name.</summary>
        private static List<OcrWord> BuildGridWords()
        {
            var words = new List<OcrWord>
            {
                Word("Report", 10, 100), Word("Name", 75, 100),
                Word("Report", 300, 100), Word("Description", 365, 100),

                Word("A/R", 10, 130), Word("Control", 40, 130), Word("Balance", 80, 130),
                Word("by", 130, 130), Word("Date", 150, 130), Word("Range", 190, 130),
                Word("Some", 300, 130), Word("description", 340, 130),

                Word("Inventory", 10, 142), Word("Valuation", 75, 142),
                Word("Some", 300, 142), Word("description", 340, 142),

                Word("Inventory", 10, 154), Word("Valuation", 75, 154),
                Word("(With", 130, 154), Word("Last", 170, 154), Word("Supplier)", 200, 154),
                Word("Some", 300, 154), Word("description", 340, 154),

                Word("Inventory", 10, 166), Word("Valuation", 75, 166), Word("for", 130, 166), Word("Excel", 155, 166),
                Word("Some", 300, 166), Word("description", 340, 166),
            };

            return words;
        }

        [Fact]
        public void MatchesTheExactRowAndNotACollidingLongerRow()
        {
            var words = BuildGridWords();

            var match = ReportGridOcrMatcher.FindReportNameLine(words, "Inventory Valuation");

            Assert.NotNull(match);
            // The plain "Inventory Valuation" row is at Y=142, not the
            // "(With Last Supplier)" (Y=154) or "for Excel" (Y=166) rows.
            Assert.Equal(142, match!.Value.Y, precision: 0);
        }

        [Fact]
        public void DoesNotMatchAPrefixOfALongerRowName()
        {
            var words = new List<OcrWord>
            {
                Word("Inventory", 10, 130), Word("Valuation", 75, 130),
                Word("(With", 130, 130), Word("Last", 170, 130), Word("Supplier)", 200, 130),
            };

            var match = ReportGridOcrMatcher.FindReportNameLine(words, "Inventory Valuation");

            Assert.Null(match);
        }

        [Fact]
        public void RestrictsMatchingToWordsLeftOfTheDescriptionColumn()
        {
            // "Some description" text lives in the Description column on
            // the SAME line as a target row two rows up would collide
            // with if the column boundary weren't respected.
            var words = new List<OcrWord>
            {
                Word("Report", 10, 100), Word("Name", 75, 100),
                Word("Report", 300, 100), Word("Description", 365, 100),

                Word("A/R", 10, 130), Word("Control", 40, 130), Word("Balance", 80, 130),
                Word("Description", 300, 130), Word("of", 340, 130), Word("A/R", 360, 130), Word("Control", 380, 130), Word("Balance", 420, 130),
            };

            var match = ReportGridOcrMatcher.FindReportNameLine(words, "A/R Control Balance");

            Assert.NotNull(match);
        }

        [Fact]
        public void MatchesExactlyForTheThirdPartyReconciliationCatalogRow()
        {
            var words = new List<OcrWord>
            {
                Word("Third", 10, 200), Word("Party", 40, 200), Word("Reconciliation", 70, 200),
                Word("Account", 150, 200), Word("Aged", 190, 200), Word("Trial", 220, 200), Word("Balance", 250, 200),
            };

            var match = ReportGridOcrMatcher.FindReportNameLine(words, "Third Party Reconciliation Account Aged Trial Balance");

            Assert.NotNull(match);
        }

        [Fact]
        public void ReturnsNullWhenNoLineMatches()
        {
            var words = BuildGridWords();

            var match = ReportGridOcrMatcher.FindReportNameLine(words, "Nonexistent Report");

            Assert.Null(match);
        }

        [Fact]
        public void ReturnsNullForABlankTargetName()
        {
            var words = BuildGridWords();

            Assert.Null(ReportGridOcrMatcher.FindReportNameLine(words, ""));
            Assert.Null(ReportGridOcrMatcher.FindReportNameLine(words, null!));
        }

        [Fact]
        public void CenterIsWithinTheMatchedWordsBoundingBox()
        {
            var words = new List<OcrWord> { Word("Inventory", 10, 142, w: 60, h: 12), Word("Valuation", 75, 142, w: 60, h: 12) };

            var match = ReportGridOcrMatcher.FindReportNameLine(words, "Inventory Valuation");

            Assert.NotNull(match);
            Assert.InRange(match!.Value.CenterX, 10, 135);
            Assert.InRange(match.Value.CenterY, 142, 154);
        }
    }
}
