using System.Collections.Generic;
using RxVerifyOverlay.OrderAssist.Ocr;
using Xunit;

namespace RxVerifyOverlay.Tests.OrderAssist;

/// <summary>
/// Unit tests for RowHighlightColorDetector. ROUND 6 (Will, verbatim: "The
/// blue and yellow lines need to be read too, just as if they are white
/// regular lines. Currently they are being skipped.") — rounds 3-5's
/// accept/reject GATE (IsHighlightedBackgroundColor/IsHighlightScanline
/// and their chroma/luminance/fill-fraction thresholds) is gone; this
/// class now only exposes the raw color primitives RowHighlightNormalizer's
/// UNCONDITIONAL per-scanline binarization is built from, plus one
/// PURELY diagnostic classification (IsNotablyColored) used for field-report
/// logging only. Colors below are synthetic RGB triples chosen to represent
/// the GENERAL FAMILIES this module deals with (a saturated selection/flag
/// fill, a pale highlight tint, ordinary plain-background rows, plain
/// text) — none are sampled from a real screenshot (not possible from this
/// environment).
/// </summary>
public class RowHighlightColorDetectorTests
{
    // ---- MeasureColor --------------------------------------------------

    [Fact]
    public void MeasureColorReportsChromaAndLuminanceForAPaleYellowFill()
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

    [Fact]
    public void MeasureColorOfSaturatedSelectionBlueHasHighChroma()
    {
        var (chroma, _) = RowHighlightColorDetector.MeasureColor(r: 20, g: 90, b: 170);

        Assert.Equal(150, chroma);
    }

    [Fact]
    public void MeasureColorOfSaturatedHighlightYellowHasHighChroma()
    {
        var (chroma, _) = RowHighlightColorDetector.MeasureColor(r: 255, g: 255, b: 0);

        Assert.Equal(255, chroma);
    }

    // ---- IsNotablyColored (ROUND 6 diagnostic-only classification) ------
    //
    // NOTE: none of these tests exercise a GATE any more -- round 6 has no
    // gate. They confirm IsNotablyColored's own diagnostic-log-only
    // classification matches the color families Will reported, so
    // OrderAssistCoordinator's per-tick "N colored scanlines" log line
    // means what its doc claims.

    [Fact]
    public void SaturatedSelectionBlueIsNotablyColored()
    {
        var (chroma, _) = RowHighlightColorDetector.MeasureColor(r: 20, g: 90, b: 170);

        Assert.True(RowHighlightColorDetector.IsNotablyColored(chroma));
    }

    [Fact]
    public void SaturatedHighlightYellowIsNotablyColored()
    {
        var (chroma, _) = RowHighlightColorDetector.MeasureColor(r: 255, g: 255, b: 0);

        Assert.True(RowHighlightColorDetector.IsNotablyColored(chroma));
    }

    [Fact]
    public void PaleYellowRowTintIsNotablyColored()
    {
        // Will: "the yellow and blue colored rows are valid" -- a pale
        // tint must still register as "colored" for the diagnostic log
        // even though it's nowhere near as saturated as the cases above.
        var (chroma, _) = RowHighlightColorDetector.MeasureColor(r: 250, g: 245, b: 200);

        Assert.True(RowHighlightColorDetector.IsNotablyColored(chroma));
    }

    [Fact]
    public void PaleLightBlueRowTintIsNotablyColored()
    {
        var (chroma, _) = RowHighlightColorDetector.MeasureColor(r: 173, g: 216, b: 230);

        Assert.True(RowHighlightColorDetector.IsNotablyColored(chroma));
    }

    [Fact]
    public void PlainWhiteIsNotNotablyColored()
    {
        var (chroma, _) = RowHighlightColorDetector.MeasureColor(r: 255, g: 255, b: 255);

        Assert.False(RowHighlightColorDetector.IsNotablyColored(chroma));
    }

    [Fact]
    public void NearWhiteIsNotNotablyColored()
    {
        var (chroma, _) = RowHighlightColorDetector.MeasureColor(r: 250, g: 250, b: 250);

        Assert.False(RowHighlightColorDetector.IsNotablyColored(chroma));
    }

    [Fact]
    public void LightGrayIsNotNotablyColored()
    {
        // Neutral gray (equal channels) must never register as "colored"
        // regardless of brightness.
        var (chroma, _) = RowHighlightColorDetector.MeasureColor(r: 240, g: 240, b: 240);

        Assert.False(RowHighlightColorDetector.IsNotablyColored(chroma));
    }

    [Fact]
    public void PlainBlackTextIsNotNotablyColored()
    {
        var (chroma, _) = RowHighlightColorDetector.MeasureColor(r: 10, g: 10, b: 10);

        Assert.False(RowHighlightColorDetector.IsNotablyColored(chroma));
    }

    [Fact]
    public void DiagnosticColoredRowChromaFloorMatchesDocumentedRound6Value()
    {
        // Pins the actual floor so a future accidental tuning change shows
        // up as a failing test, not silent drift. NOTE: unlike round 5's
        // equivalent constant, this value gates a LOG LINE only -- it has
        // no effect on whether a scanline gets binarized (every scanline
        // does, unconditionally -- see RowHighlightNormalizer).
        Assert.Equal(15, RowHighlightColorDetector.DiagnosticColoredRowChromaFloor);
    }

    // ---- EstimateDominantColor -------------------------------------------

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

    [Fact]
    public void DominantColorOfAnOrdinaryWhiteRowWithBlackTextIsWhite()
    {
        // ROUND 6: this is the case that must keep working identically to
        // "left untouched" -- an ordinary plain row's own dominant color
        // is its white background, majority by construction.
        var pixels = new List<(byte, byte, byte)>();
        for (var i = 0; i < 90; i++) pixels.Add(((byte)255, (byte)255, (byte)255));
        for (var i = 0; i < 10; i++) pixels.Add(((byte)10, (byte)10, (byte)10));

        var dominant = RowHighlightColorDetector.EstimateDominantColor(pixels);

        Assert.Equal((255, 255, 255), ((int)dominant.R, (int)dominant.G, (int)dominant.B));
    }

    [Fact]
    public void DominantColorOfATextDominantScanlineIsTheTextColor()
    {
        // ROUND 6 failure-mode regression guard: documents (rather than
        // "fixes", since it can't be told apart from a genuine highlight
        // without color knowledge this module deliberately no longer has)
        // the dense/text-dominant edge case called out in
        // EstimateDominantColor's own round-6 doc -- when text pixels
        // actually outnumber background pixels, the median legitimately
        // returns the text color. RowHighlightNormalizer still produces a
        // clean (if polarity-flipped) binary image in this case -- see
        // that class's own doc.
        var pixels = new List<(byte, byte, byte)>();
        for (var i = 0; i < 60; i++) pixels.Add(((byte)10, (byte)10, (byte)10));   // dense black text
        for (var i = 0; i < 40; i++) pixels.Add(((byte)255, (byte)255, (byte)255)); // background

        var dominant = RowHighlightColorDetector.EstimateDominantColor(pixels);

        Assert.Equal((10, 10, 10), ((int)dominant.R, (int)dominant.G, (int)dominant.B));
    }

    [Fact]
    public void EmptyScanlineDominantColorDefaultsToWhite()
    {
        var dominant = RowHighlightColorDetector.EstimateDominantColor(new List<(byte, byte, byte)>());

        Assert.Equal((255, 255, 255), ((int)dominant.R, (int)dominant.G, (int)dominant.B));
    }

    // ---- IsCloseToColor (the per-pixel binarization test) -----------------

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

    [Fact]
    public void PaleYellowRowBinarizesTextDistinctlyFromBackground()
    {
        // Confirms the binarization decision (IsCloseToColor against the
        // row's own dominant color -- the same test
        // RowHighlightNormalizer.NormalizeInPlaceCore now applies to EVERY
        // scanline, unconditionally) does NOT collapse a pale background
        // to all-white by also pulling in the dark text. Not testable
        // through the normalizer itself on this Mac (System.Drawing.Common
        // blocks real Bitmap pixel access outside Windows) so this
        // exercises the exact same per-pixel decision it delegates to.
        var pixels = new List<(byte R, byte G, byte B)>();
        for (var i = 0; i < 80; i++) pixels.Add((250, 245, 200)); // pale yellow fill
        for (var i = 0; i < 20; i++) pixels.Add((20, 20, 20));    // dark text

        var dominant = RowHighlightColorDetector.EstimateDominantColor(pixels);

        // Background pixels binarize to white (close to dominant)...
        Assert.True(RowHighlightColorDetector.IsCloseToColor(250, 245, 200, dominant));
        // ...and text pixels binarize to black (NOT close to dominant).
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

    [Fact]
    public void SaturatedSelectionBlueRowBinarizesTextDistinctlyFromBackground()
    {
        // ROUND 6 end-to-end intent check: a SATURATED (not just pale)
        // selection-blue row must binarize exactly the same way a plain
        // white row does -- background -> white, text -> black -- with no
        // separate gate deciding whether this row gets the treatment.
        var pixels = new List<(byte R, byte G, byte B)>();
        for (var i = 0; i < 85; i++) pixels.Add((20, 90, 170));    // saturated selection blue fill
        for (var i = 0; i < 15; i++) pixels.Add((255, 255, 255)); // light/white text on the dark fill

        var dominant = RowHighlightColorDetector.EstimateDominantColor(pixels);

        Assert.True(RowHighlightColorDetector.IsCloseToColor(20, 90, 170, dominant));
        Assert.False(RowHighlightColorDetector.IsCloseToColor(255, 255, 255, dominant));
    }

    [Fact]
    public void OrdinaryWhiteRowBinarizesIdenticallyToTheColoredRowCases()
    {
        // ROUND 6 core invariant: "a white row with black text produces
        // the same output as today's untouched path" -- demonstrated here
        // as "the SAME binarization test, applied the SAME way, regardless
        // of whether the row's own dominant color happens to be white or a
        // saturated highlight fill."
        var pixels = new List<(byte R, byte G, byte B)>();
        for (var i = 0; i < 90; i++) pixels.Add((255, 255, 255)); // plain white background
        for (var i = 0; i < 10; i++) pixels.Add((10, 10, 10));    // ordinary black text

        var dominant = RowHighlightColorDetector.EstimateDominantColor(pixels);

        Assert.True(RowHighlightColorDetector.IsCloseToColor(255, 255, 255, dominant));
        Assert.False(RowHighlightColorDetector.IsCloseToColor(10, 10, 10, dominant));
    }
}
