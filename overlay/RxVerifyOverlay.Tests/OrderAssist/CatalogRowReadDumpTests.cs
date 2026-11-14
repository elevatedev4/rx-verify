using System.Collections.Generic;
using System.Linq;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.OrderAssist.Scanning;
using Xunit;

namespace RxVerifyOverlay.Tests.OrderAssist;

/// <summary>W-T96 row-read dump: pure parts (row read, color class, sanitizing, message). Synthetic data only.</summary>
public class CatalogRowReadDumpTests
{
    private static OcrWord Word(string text, double x, double y, double w, double h = 12) =>
        new() { Text = text, X = x, Y = y, W = w, H = h };

    private static List<OcrWord> Grid() => new()
    {
        Word("Supplier", 0, 0, 50),
        Word("Rebate", 350, 0, 45), Word("Cost", 400, 0, 35), Word("Per", 440, 0, 25), Word("Unit", 470, 0, 35),
        Word("McKesson", 10, 40, 50), Word("10.00", 370, 40, 30),
        Word("McKesson", 10, 60, 50), // cost cell unread (e.g. a colored row the OCR missed)
        Word("ANDA", 10, 80, 30), Word("4.50", 370, 80, 30),
    };

    [Fact]
    public void ReadBodyRowsReturnsSupplierAndCostPerRowAndBlanksForUnreadCells()
    {
        var rows = CatalogRowReadDump.ReadBodyRows(Grid());

        Assert.Equal(3, rows.Count);
        Assert.Equal(("McKesson", "10.00"), (rows[0].Supplier, rows[0].Cost));
        Assert.Equal(("McKesson", ""), (rows[1].Supplier, rows[1].Cost));
        Assert.Equal(("ANDA", "4.50"), (rows[2].Supplier, rows[2].Cost));
        Assert.Equal(60, rows[1].Top);
    }

    [Fact]
    public void ReadBodyRowsIsEmptyWhenColumnsCannotBeResolved()
    {
        Assert.Empty(CatalogRowReadDump.ReadBodyRows(new List<OcrWord> { Word("1.00", 0, 0, 30) }));
    }

    [Theory]
    [InlineData(255, 255, 255, "white")]
    [InlineData(245, 245, 245, "white")]
    [InlineData(200, 200, 200, "neutral")]
    [InlineData(210, 235, 210, "neutral")]      // pale green banding, chroma 25
    [InlineData(255, 255, 179, "yellow")]
    [InlineData(250, 245, 200, "yellow")]       // pale yellow
    [InlineData(173, 216, 230, "blue")]         // light blue
    [InlineData(0, 70, 160, "blue")]            // dark selection blue
    [InlineData(230, 90, 60, "other-colored")]
    public void ClassifyColorNamesTheRowBackground(int r, int g, int b, string expected)
    {
        Assert.Equal(expected, CatalogRowReadDump.ClassifyColor((byte)r, (byte)g, (byte)b).Class);
    }

    [Fact]
    public void SanitizersDropEverythingButSupplierAndPriceCharacters()
    {
        Assert.Equal("McKesson Corp", CatalogRowReadDump.SanitizeSupplier("  McKesson Corp  "));
        Assert.Equal("ABC Pharma Inc", CatalogRowReadDump.SanitizeSupplier("ABC <Pharma> Inc|\n"));
        Assert.Equal(CatalogRowReadDump.MaxSupplierChars, CatalogRowReadDump.SanitizeSupplier(new string('A', 200)).Length);
        Assert.Equal("$12.50", CatalogRowReadDump.SanitizeCost("$12.50 |x"));
        Assert.Equal("", CatalogRowReadDump.SanitizeCost(null));
    }

    [Fact]
    public void BuildMessageListsEveryRowAndMarksBlankCells()
    {
        var lines = new List<CatalogRowReadDump.RowLine>
        {
            new(0, "McKesson", "10.00", "white", 0, 255, false),
            new(1, "", "", "blue", 90, 120, true),
        };

        var message = CatalogRowReadDump.BuildMessage("header", lines);

        Assert.Equal(
            "header\n2 body row(s) read\n" +
            "row 0: supplier=McKesson cost=10.00 color=white(c0,l255) normalized=no\n" +
            "row 1: supplier=(blank) cost=(blank) color=blue(c90,l120) normalized=yes",
            message);
    }

    [Fact]
    public void BuildMessageCapsTheRowCount()
    {
        var lines = Enumerable.Range(0, CatalogRowReadDump.MaxRows + 5)
            .Select(i => new CatalogRowReadDump.RowLine(i, "ANDA", "1.00", "white", 0, 255, false))
            .ToList();

        var message = CatalogRowReadDump.BuildMessage("h", lines);

        Assert.EndsWith("(5 more row(s) not listed)", message);
        Assert.Equal(CatalogRowReadDump.MaxRows, message.Split('\n').Count(l => l.StartsWith("row ")));
    }
}
