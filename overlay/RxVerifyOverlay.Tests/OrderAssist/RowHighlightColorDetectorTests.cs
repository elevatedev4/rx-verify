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
        // (pastel), unlike a genuine flag/selection fill. chroma=25, below
        // ROUND 5's 30 floor with a 5-point margin.
        Assert.False(RowHighlightColorDetector.IsHighlightedBackgroundColor(r: 210, g: 235, b: 210));
    }

    [Fact]
    public void PaleYellowRowTintIsAHighlightColor()
    {
        // ROUND 5 (Will: "the yellow and blue colored rows are valid" but
        // "it's only reading the white ones"): round 3/4 wrongly treated
        // this as inert row-banding to exclude. chroma=50, now above the
        // corrected 30 floor -- this is exactly the pale-yellow-row symptom
        // Will reported.
        Assert.True(RowHighlightColorDetector.IsHighlightedBackgroundColor(r: 250, g: 245, b: 200));
    }

    [Fact]
    public void ClassicLightBlueHeaderTintIsAHighlightColor()
    {
        // ROUND 5: chroma=57, above the corrected 30 floor -- one of Will's
        // reported "blue colored rows are valid" cases.
        Assert.True(RowHighlightColorDetector.IsHighlightedBackgroundColor(r: 173, g: 216, b: 230));
    }

    [Fact]
    public void PaleHeaderTintBlueIsAHighlightColor()
    {
        // ROUND 5: chroma=45, above the corrected 30 floor.
        Assert.True(RowHighlightColorDetector.IsHighlightedBackgroundColor(r: 200, g: 220, b: 245));
    }

    [Fact]
    public void PlainWhiteIsNotAHighlightColor()
    {
        // ROUND 5 regression guard: chroma=0 -- must never trip the lowered
        // floor just because it's uniformly light.
        Assert.False(RowHighlightColorDetector.IsHighlightedBackgroundColor(r: 255, g: 255, b: 255));
    }

    [Fact]
    public void LightGrayIsNotAHighlightColor()
    {
        // ROUND 5 regression guard: chroma=0 (equal channels) -- a neutral
        // gray must never be mistaken for a colored fill regardless of how
        // low the chroma floor goes.
        Assert.False(RowHighlightColorDetector.IsHighlightedBackgroundColor(r: 240, g: 240, b: 240));
    }

    [Fact]
    public void NearWhiteRowIsNotAHighlightColor()
    {
        // ROUND 5 regression guard: chroma=0, distinct from the existing
        // (250,250,250) near-white case above.
        Assert.False(RowHighlightColorDetector.IsHighlightedBackgroundColor(r: 245, g: 245, b: 245));
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
    public void RealPioneerPaleYellowFlagRowMeasuredFromWillsScreenshotIsAHighlightColor()
    {
        // ROUND 4: (255,255,179) is pixel-measured directly from the
        // owner's own Create Recommended Orders screenshot (order screen
        // round 4 qty-0 report) -- the row Pioneer itself tints to flag
        // it. chroma=76, luminance=246.3 -- the OLD 245 ceiling rejected
        // this by 1.3; see MaxLuminanceForHighlight's own round-4 doc.
        Assert.True(RowHighlightColorDetector.IsHighlightedBackgroundColor(r: 255, g: 255, b: 179));
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

    [Fact]
    public void ScanlineWithFiftyFivePercentFillNowCountsAfterTheRound5FractionLowering()
    {
        // ROUND 5 (Will, still after the chroma-30 fix: "Order is still not
        // reading the colored rows" -- Catalog Substitution specifically).
        // See DefaultMinHighlightFraction's own doc: the captured region is
        // the WHOLE target window, so a real highlighted row's fill can be
        // diluted below the old 0.6 floor by non-grid chrome/margin pixels
        // on the same scanline. 55% would have FAILED under round 3/4's 0.6
        // default; the round-5 default of 0.5 now accepts it.
        var selectionBlue = ((byte)20, (byte)90, (byte)170);
        var chrome = ((byte)245, (byte)245, (byte)245);

        var pixels = new List<(byte, byte, byte)>();
        for (var i = 0; i < 55; i++) pixels.Add(selectionBlue);
        for (var i = 0; i < 45; i++) pixels.Add(chrome);

        Assert.True(RowHighlightColorDetector.IsHighlightScanline(pixels));
    }

    [Fact]
    public void ScanlineWellBelowHalfFillStillNeverCountsRegardlessOfTheRound5Lowering()
    {
        // Regression guard: the round-5 lowering (0.6 -> 0.5) still rejects
        // a scanline where the highlight color is a genuine MINORITY, not
        // just "not quite 60%" -- same "reject a thin colored stroke"
        // property the fraction check exists for at all.
        var selectionBlue = ((byte)20, (byte)90, (byte)170);
        var chrome = ((byte)245, (byte)245, (byte)245);

        var pixels = new List<(byte, byte, byte)>();
        for (var i = 0; i < 35; i++) pixels.Add(selectionBlue);
        for (var i = 0; i < 65; i++) pixels.Add(chrome);

        Assert.False(RowHighlightColorDetector.IsHighlightScanline(pixels));
    }

    [Fact]
    public void DefaultMinHighlightFractionMatchesDocumentedRound5Value()
    {
        // Pins the actual default so a future accidental tuning change
        // shows up as a failing test, not silent drift.
        Assert.Equal(0.5, RowHighlightColorDetector.DefaultMinHighlightFraction);
    }

    // ---- ROUND 5: pale-row full pipeline + binarization readability -------

    [Fact]
    public void ScanlineMajorityFilledWithPaleYellowWithDarkTextIsAHighlightScanline()
    {
        // Confirms the 0.6 dominant-fraction still holds for a pale row
        // with a minority of dark text pixels (task 1's "verify the 0.6
        // fraction still holds for pale rows with dark text" check) --
        // matches Will's reported "pale yellow row is valid" case end to
        // end, not just the single-color IsHighlightedBackgroundColor unit.
        var paleYellow = ((byte)250, (byte)245, (byte)200);
        var darkText = ((byte)20, (byte)20, (byte)20);

        var pixels = new List<(byte, byte, byte)>();
        for (var i = 0; i < 80; i++) pixels.Add(paleYellow);
        for (var i = 0; i < 20; i++) pixels.Add(darkText);

        Assert.True(RowHighlightColorDetector.IsHighlightScanline(pixels));
    }

    [Fact]
    public void ScanlineMajorityFilledWithLightBlueWithDarkTextIsAHighlightScanline()
    {
        var lightBlue = ((byte)173, (byte)216, (byte)230);
        var darkText = ((byte)15, (byte)15, (byte)60);

        var pixels = new List<(byte, byte, byte)>();
        for (var i = 0; i < 80; i++) pixels.Add(lightBlue);
        for (var i = 0; i < 20; i++) pixels.Add(darkText);

        Assert.True(RowHighlightColorDetector.IsHighlightScanline(pixels));
    }

    [Fact]
    public void PaleYellowRowBinarizesTextDistinctlyFromBackground()
    {
        // Task 2: confirm the binarization decision (IsCloseToColor against
        // the row's own dominant color -- the same test
        // RowHighlightNormalizer.NormalizeInPlaceCore applies per pixel)
        // does NOT collapse a pale background to all-white by also pulling
        // in the dark text. Not testable through the normalizer itself on
        // this Mac (System.Drawing.Common blocks real Bitmap pixel access
        // outside Windows -- see that class's own doc) so this exercises
        // the exact same per-pixel decision it delegates to.
        var pixels = new List<(byte R, byte G, byte B)>();
        for (var i = 0; i < 80; i++) pixels.Add((250, 245, 200)); // pale yellow fill
        for (var i = 0; i < 20; i++) pixels.Add((20, 20, 20));    // dark text

        var dominant = RowHighlightColorDetector.EstimateDominantColor(pixels);

        // Background pixels binarize to white (close to dominant)...
        Assert.True(RowHighlightColorDetector.IsCloseToColor(250, 245, 200, dominant));
        // ...and text pixels binarize to black (NOT close to dominant) --
        // i.e. this is NOT the all-white failure mode task 2 warns about.
        Assert.False(RowHighlightColorDetector.IsCloseToColor(20, 20, 20, dominant));
    }

    [Fact]
    public void LightBlueRowBinarizesTextDistinctlyFromBackground()
    {
        var pixels = new List<(byte R, byte G, byte B)>();
        for (var i = 0; i < 80; i++) pixels.Add((173, 216, 230)); // light blue fill
        for (var i = 0; i < 20; i++) pixels.Add((10, 10, 10));    // dark text

        var dominant = RowHighlightColorDetector.EstimateDominantColor(pixels);

        Assert.True(RowHighlightColorDetector.IsCloseToColor(173, 216, 230, dominant));
        Assert.False(RowHighlightColorDetector.IsCloseToColor(10, 10, 10, dominant));
    }

    // ---- MeasureColor (ROUND 5 diagnostic helper) --------------------------

    [Fact]
    public void MeasureColorReportsChromaAndLuminanceMatchingIsHighlightedBackgroundColor()
    {
        var (chroma, luminance) = RowHighlightColorDetector.MeasureColor(r: 250, g: 245, b: 200);

        Assert.Equal(50, chroma);
        Assert.True(luminance is > 240 and < 242);
    }

    [Fact]
    public void MeasureColorOfNeutralGrayHasZeroChroma()
    {
        var (chroma, _) = RowHighlightColorDetector.MeasureColor(r: 240, g: 240, b: 240);

        Assert.Equal(0, chroma);
    }
}
