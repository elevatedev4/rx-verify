using System.Collections.Generic;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.OrderAssist.Scanning;
using Xunit;

namespace RxVerifyOverlay.Tests.OrderAssist;

/// <summary>
/// W-T96 (Will: "it did not identify the cheapest mckesson row because it
/// chose the first white mckesson row and ignored the colored ones"). Pins
/// the contract that the McKesson baseline is the cheapest McKesson row
/// REGARDLESS of the row's background colour, from synthetic OCR word grids
/// (all suppliers/prices made up).
///
/// The scanner works on OCR words only, so colour never reaches it: a blue
/// or yellow row is just a row whose words either made it out of OCR or did
/// not (see Ocr/RowHighlightColorDetector and LocalContrastNormalizer for the
/// pixel step that decides that). These fixtures therefore model the
/// "colored row WAS read" case, with the row tint recorded only to name the
/// scenario and with a small Y jitter / different word widths on colored
/// rows like a real binarized capture. If these pass and the live app still
/// picks a white row, the colored row's words are being lost BEFORE this
/// code, in the bitmap/OCR step.
/// </summary>
public class CatalogColoredRowBaselineTests
{
    private enum Tint { White, Blue, Yellow }

    private static OcrWord Word(string text, double x, double y, double w, double h = 12) =>
        new() { Text = text, X = x, Y = y, W = w, H = h };

    private static List<OcrWord> Header() => new()
    {
        Word("Supplier", 0, 0, 50),
        Word("Rebate", 350, 0, 45),
        Word("Cost", 400, 0, 35),
        Word("Per", 440, 0, 25),
        Word("Unit", 470, 0, 35),
    };

    /// <summary>One data row. A colored row gets a 1px Y jitter and a wider cost word, the way a binarized colored row reads in practice; it must not matter.</summary>
    private static List<OcrWord> Row(double y, Tint tint, string supplier, string cost)
    {
        var jitter = tint == Tint.White ? 0 : 1;
        var costWidth = tint == Tint.White ? 30 : 34;
        return new List<OcrWord>
        {
            Word(supplier, 10, y + jitter, 40),
            Word(cost, 370, y + jitter, costWidth),
        };
    }

    private static CatalogSubstitutionScanner.CatalogAnnotations Analyze(params (Tint Tint, string Supplier, string Cost)[] rows)
    {
        var words = Header();
        var y = 30.0;
        foreach (var r in rows)
        {
            words.AddRange(Row(y, r.Tint, r.Supplier, r.Cost));
            y += 24;
        }
        return CatalogSubstitutionScanner.Analyze(words);
    }

    [Fact]
    public void WhiteOnly_PicksCheapestMcKesson()
    {
        var a = Analyze(
            (Tint.White, "McKesson", "5.00"),
            (Tint.White, "McKesson", "4.00"),
            (Tint.White, "IPC", "3.00"));

        Assert.NotNull(a.McKessonBaselineMarker);
        Assert.Equal(1, a.McKessonBaselineMarker!.RowIndex);
        var badge = Assert.Single(a.SavingsBadges);
        Assert.Equal(2, badge.RowIndex);
        Assert.Equal("25%", badge.SavingsDisplay); // (4-3)/4, against the cheapest McKesson
    }

    [Fact]
    public void CheapestMcKessonIsBlue_IsTheBaseline()
    {
        var a = Analyze(
            (Tint.White, "McKesson", "5.00"),
            (Tint.Blue, "McKesson", "4.00"),
            (Tint.White, "McKesson", "4.50"),
            (Tint.White, "IPC", "3.00"));

        Assert.NotNull(a.McKessonBaselineMarker);
        Assert.Equal(1, a.McKessonBaselineMarker!.RowIndex);
        var badge = Assert.Single(a.SavingsBadges);
        Assert.Equal("25%", badge.SavingsDisplay);
    }

    [Fact]
    public void CheapestMcKessonIsYellow_IsTheBaseline()
    {
        var a = Analyze(
            (Tint.White, "McKesson", "5.00"),
            (Tint.White, "McKesson", "4.50"),
            (Tint.Yellow, "McKesson", "4.00"),
            (Tint.White, "ANDA", "3.00"));

        Assert.NotNull(a.McKessonBaselineMarker);
        Assert.Equal(2, a.McKessonBaselineMarker!.RowIndex);
        var badge = Assert.Single(a.SavingsBadges);
        Assert.Equal(3, badge.RowIndex);
        Assert.Equal("25%", badge.SavingsDisplay);
    }

    [Fact]
    public void Mixed_BlueAndYellowAndWhite_CheapestWinsWhateverItsColor()
    {
        var a = Analyze(
            (Tint.Blue, "McKesson", "6.00"),
            (Tint.White, "McKesson", "5.00"),
            (Tint.Yellow, "McKesson", "4.00"),
            (Tint.Blue, "ParMed", "3.00"),
            (Tint.White, "McKesson", "4.20"));

        Assert.Equal(2, a.McKessonBaselineMarker!.RowIndex); // yellow $4.00
        var badge = Assert.Single(a.SavingsBadges);
        Assert.Equal(3, badge.RowIndex);                     // blue secondary is also compared
        Assert.Equal("25%", badge.SavingsDisplay);
    }

    [Fact]
    public void Tie_BetweenColoredAndWhite_KeepsTheFirstRow()
    {
        var a = Analyze(
            (Tint.Blue, "McKesson", "4.00"),
            (Tint.White, "McKesson", "4.00"));

        Assert.Equal(0, a.McKessonBaselineMarker!.RowIndex); // same ThenBy(RowIndex) rule as today
    }

    [Fact]
    public void ColoredRowWithUnreadableCost_IsStillIgnored_AsToday()
    {
        var a = Analyze(
            (Tint.White, "McKesson", "5.00"),
            (Tint.Yellow, "McKesson", ""));   // blank cost: never treated as free

        Assert.Equal(0, a.McKessonBaselineMarker!.RowIndex);
    }
}
