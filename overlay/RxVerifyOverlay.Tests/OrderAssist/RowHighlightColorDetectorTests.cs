using System.Collections.Generic;
using RxVerifyOverlay.OrderAssist.Ocr;
using Xunit;

namespace RxVerifyOverlay.Tests.OrderAssist;

/// <summary>
/// Unit tests for RowHighlightColorDetector (round 3 — replaces round 2's
/// SelectionRowColorDetector, which only ever matched one hardcoded blue
/// hue). Colors below are synthetic RGB triples chosen to represent the
/// GENERAL FAMILIES this detector needs to tell apart (a saturated
/// selection/flag fill of ANY hue vs. ordinary light row tints vs. plain
/// text) — none are sampled from a real screenshot (not possible from this
/// environment — see that class's own "STILL AN ESTIMATE" doc).
/// </summary>
public class RowHighlightColorDetectorTests
{
    // ---- IsHighlightedBackgroundColor -------------------------------------

    [Fact]
    public void MediumDarkBlueSelectionFillIsAHighlightColor()
    {
        Assert.True(RowHighlightColorDetector.IsHighlightedBackgroundColor(r: 20, g: 90, b: 170));
    }

    [Fact]
    public void BrightSaturatedYellowHighlightIsAHighlightColor()
    {
        // Round 3's actual new case — Will: "sometimes rows may be
        // highlighted in yellow ... this one skipped the yellow item".
        // Round 2's blue-only detector had no bounds at all for this.
        Assert.True(RowHighlightColorDetector.IsHighlightedBackgroundColor(r: 255, g: 255, b: 0));
    }

    [Fact]
    public void DarkGoldHighlightWithLightTextPolarityIsAHighlightColor()
    {
        // A plausible "flag" style highlight (amber/gold background, white
        // or pale glyph/text) — inverted polarity like the blue selection
        // case, different hue. Round 3 no longer needs to special-case
        // this separately from blue: chroma+luminance alone catches it.
        Assert.True(RowHighlightColorDetector.IsHighlightedBackgroundColor(r: 204, g: 153, b: 0));
    }

    [Fact]
    public void SaturatedGreenIsNowAlsoAHighlightColor()
    {
        // DELIBERATE behavior change from round 2 (which was blue-only and
        // explicitly rejected this as "rules out teal-ish tones"): round 3
        // is hue-agnostic by design, so any sufficiently saturated,
        // mid-luminance fill counts, including green flags Pioneer might
        // use that round 2 never had any bounds for.
        Assert.True(RowHighlightColorDetector.IsHighlightedBackgroundColor(r: 20, g: 170, b: 90));
    }

    [Fact]
    public void SaturatedRedIsNowAlsoAHighlightColor()
    {
        Assert.True(RowHighlightColorDetector.IsHighlightedBackgroundColor(r: 170, g: 30, b: 40));
    }

    [Fact]
    public void NearWhiteBackgroundIsNotAHighlightColor()
    {
        Assert.False(RowHighlightColorDetector.IsHighlightedBackgroundColor(r: 250, g: 250, b: 250));
    }

    [Fact]
    public void PaleGreenRowTintIsNotAHighlightColor()
    {
        // Ordinary alternating-row banding must stay untouched — low chroma
        // (pastel), unlike a genuine flag/selection fill.
        Assert.False(RowHighlightColorDetector.IsHighlightedBackgroundColor(r: 210, g: 235, b: 210));
    }

    [Fact]
    public void PaleYellowRowTintIsNotAHighlightColor()
    {
        Assert.False(RowHighlightColorDetector.IsHighlightedBackgroundColor(r: 250, g: 245, b: 200));
    }

    [Fact]
    public void ClassicLightBlueHeaderTintIsNotAHighlightColor()
    {
        Assert.False(RowHighlightColorDetector.IsHighlightedBackgroundColor(r: 173, g: 216, b: 230));
    }

    [Fact]
    public void PaleHeaderTintBlueIsNotAHighlightColor()
    {
        Assert.False(RowHighlightColorDetector.IsHighlightedBackgroundColor(r: 200, g: 220, b: 245));
    }

    [Fact]
    public void BlackTextIsNotAHighlightColor()
    {
        Assert.False(RowHighlightColorDetector.IsHighlightedBackgroundColor(r: 10, g: 10, b: 10));
    }

    [Fact]
    public void WhiteTextIsNotAHighlightColor()
    {
        Assert.False(RowHighlightColorDetector.IsHighlightedBackgroundColor(r: 255, g: 255, b: 255));
    }

    [Fact]
    public void DarkSaturatedColorBelowTheLuminanceFloorIsNotAHighlightColor()
    {
        // Chroma alone isn't enough -- a color can be strongly saturated
        // (chroma 65 here, above the 60 bound) and still be too DARK to be
        // a row FILL rather than ordinary dark text; the luminance floor is
        // what rejects it.
        Assert.False(RowHighlightColorDetector.IsHighlightedBackgroundColor(r: 5, g: 65, b: 0));
    }

    // ---- EstimateDominantColor ---------------------------------------------

    [Fact]
    public void DominantColorOfAUniformScanlineIsThatColor()
    {
        var pixels = new List<(byte, byte, byte)>();
        for (var i = 0; i < 50; i++) pixels.Add(((byte)255, (byte)255, (byte)0));

        var dominant = RowHighlightColorDetector.EstimateDominantColor(pixels);

        Assert.Equal((255, 255, 0), ((int)dominant.R, (int)dominant.G, (int)dominant.B));
    }

    [Fact]
    public void DominantColorIgnoresAMinorityOfTextPixels()
    {
        var pixels = new List<(byte, byte, byte)>();
        for (var i = 0; i < 80; i++) pixels.Add(((byte)255, (byte)255, (byte)0)); // yellow fill
        for (var i = 0; i < 20; i++) pixels.Add(((byte)0, (byte)0, (byte)0));     // black text

        var dominant = RowHighlightColorDetector.EstimateDominantColor(pixels);

        Assert.Equal((255, 255, 0), ((int)dominant.R, (int)dominant.G, (int)dominant.B));
    }

    // ---- IsCloseToColor -----------------------------------------------------

    [Fact]
    public void ColorWithinToleranceIsClose()
    {
        Assert.True(RowHighlightColorDetector.IsCloseToColor(r: 250, g: 250, b: 10, target: (255, 255, 0)));
    }

    [Fact]
    public void ColorOutsideToleranceIsNotClose()
    {
        Assert.False(RowHighlightColorDetector.IsCloseToColor(r: 0, g: 0, b: 0, target: (255, 255, 0)));
    }

    // ---- IsHighlightScanline -------------------------------------------------

    [Fact]
    public void ScanlineMajorityFilledWithSelectionBlueIsAHighlightScanline()
    {
        var selectionBlue = ((byte)20, (byte)90, (byte)170);
        var pixels = new List<(byte, byte, byte)>();
        for (var i = 0; i < 100; i++) pixels.Add(selectionBlue);

        Assert.True(RowHighlightColorDetector.IsHighlightScanline(pixels));
    }

    [Fact]
    public void ScanlineMajorityFilledWithHighlightYellowIsAHighlightScanline()
    {
        var highlightYellow = ((byte)255, (byte)255, (byte)0);
        var pixels = new List<(byte, byte, byte)>();
        for (var i = 0; i < 100; i++) pixels.Add(highlightYellow);

        Assert.True(RowHighlightColorDetector.IsHighlightScanline(pixels));
    }

    [Fact]
    public void ScanlineWithOnlyASmallFractionOfHighlightColorIsNotAHighlightScanline()
    {
        // Mirrors a thin hyperlink text stroke surrounded by ordinary light
        // background -- the row's own DOMINANT color is still the light
        // background, so this never even reaches the fraction check.
        var selectionBlue = ((byte)20, (byte)90, (byte)170);
        var lightBackground = ((byte)245, (byte)245, (byte)245);

        var pixels = new List<(byte, byte, byte)>();
        for (var i = 0; i < 15; i++) pixels.Add(selectionBlue);
        for (var i = 0; i < 85; i++) pixels.Add(lightBackground);

        Assert.False(RowHighlightColorDetector.IsHighlightScanline(pixels));
    }

    [Fact]
    public void OrdinaryRowWithBlackTextOnWhiteIsNeverAHighlightScanline()
    {
        var pixels = new List<(byte, byte, byte)>();
        for (var i = 0; i < 90; i++) pixels.Add(((byte)255, (byte)255, (byte)255));
        for (var i = 0; i < 10; i++) pixels.Add(((byte)10, (byte)10, (byte)10));

        Assert.False(RowHighlightColorDetector.IsHighlightScanline(pixels));
    }

    [Fact]
    public void ScanlineExactlyAtTheFractionThresholdCounts()
    {
        var selectionBlue = ((byte)20, (byte)90, (byte)170);
        var other = ((byte)245, (byte)245, (byte)245);

        var pixels = new List<(byte, byte, byte)>();
        for (var i = 0; i < 60; i++) pixels.Add(selectionBlue);
        for (var i = 0; i < 40; i++) pixels.Add(other);

        Assert.True(RowHighlightColorDetector.IsHighlightScanline(pixels, minFraction: 0.6));
    }

    [Fact]
    public void EmptyScanlineIsNeverAHighlightScanline()
    {
        Assert.False(RowHighlightColorDetector.IsHighlightScanline(new List<(byte, byte, byte)>()));
    }
}
