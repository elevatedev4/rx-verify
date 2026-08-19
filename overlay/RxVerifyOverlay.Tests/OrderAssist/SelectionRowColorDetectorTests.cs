using System.Collections.Generic;
using RxVerifyOverlay.OrderAssist.Ocr;
using Xunit;

namespace RxVerifyOverlay.Tests.OrderAssist;

/// <summary>
/// Unit tests for SelectionRowColorDetector (W-T85 bug 2 fix) — the pure
/// byte-level color classification behind SelectionRowNormalizer's bitmap
/// preprocessing. Colors below are synthetic RGB triples chosen to
/// represent the GENERAL FAMILIES this detector needs to tell apart (a
/// dark selection-blue fill vs. a brighter hyperlink-blue vs. ordinary
/// light row tints) — none are sampled from a real screenshot (not
/// possible from this environment — see that class's own "ESTIMATE, NOT
/// YET VERIFIED" doc).
/// </summary>
public class SelectionRowColorDetectorTests
{
    // ---- IsSelectionBackgroundColor -------------------------------------

    [Fact]
    public void MediumDarkBlueDominantColorIsSelectionBackground()
    {
        // A representative "accent blue" selection fill: blue clearly
        // dominant, red low, medium overall brightness.
        Assert.True(SelectionRowColorDetector.IsSelectionBackgroundColor(r: 20, g: 90, b: 170));
    }

    [Fact]
    public void BrighterMoreSaturatedHyperlinkBlueIsNotSelectionBackground()
    {
        // Classic "link blue" is much closer to pure blue (near-zero red
        // AND green) and often brighter than a selection fill -- still
        // blue-dominant by the raw margin check, but this specific shade
        // (very low green too) sits outside the detector's medium-range
        // blue bound in a real link-blue palette; use a value near
        // (0, 0, 238), the classic web hyperlink blue.
        Assert.False(SelectionRowColorDetector.IsSelectionBackgroundColor(r: 0, g: 0, b: 238));
    }

    [Fact]
    public void NearWhiteBackgroundIsNotSelectionBackground()
    {
        Assert.False(SelectionRowColorDetector.IsSelectionBackgroundColor(r: 250, g: 250, b: 250));
    }

    [Fact]
    public void PaleGreenRowTintIsNotSelectionBackground()
    {
        Assert.False(SelectionRowColorDetector.IsSelectionBackgroundColor(r: 210, g: 235, b: 210));
    }

    [Fact]
    public void PaleYellowRowTintIsNotSelectionBackground()
    {
        Assert.False(SelectionRowColorDetector.IsSelectionBackgroundColor(r: 250, g: 245, b: 200));
    }

    [Fact]
    public void BlackTextIsNotSelectionBackground()
    {
        Assert.False(SelectionRowColorDetector.IsSelectionBackgroundColor(r: 10, g: 10, b: 10));
    }

    [Fact]
    public void WhiteTextIsNotSelectionBackground()
    {
        Assert.False(SelectionRowColorDetector.IsSelectionBackgroundColor(r: 255, g: 255, b: 255));
    }

    [Fact]
    public void GreenDominantColorIsNotSelectionBackground()
    {
        // Blue not dominant over green -- rules out teal-ish tones.
        Assert.False(SelectionRowColorDetector.IsSelectionBackgroundColor(r: 20, g: 170, b: 90));
    }

    [Fact]
    public void RedDominantColorIsNotSelectionBackground()
    {
        Assert.False(SelectionRowColorDetector.IsSelectionBackgroundColor(r: 170, g: 30, b: 40));
    }

    // ---- IsSelectionScanline ---------------------------------------------

    [Fact]
    public void ScanlineMajorityFilledWithSelectionBlueIsASelectionScanline()
    {
        var selectionBlue = ((byte)20, (byte)90, (byte)170);
        var pixels = new List<(byte, byte, byte)>();
        for (var i = 0; i < 100; i++) pixels.Add(selectionBlue);

        Assert.True(SelectionRowColorDetector.IsSelectionScanline(pixels));
    }

    [Fact]
    public void ScanlineWithOnlyASmallFractionOfSelectionBlueIsNotASelectionScanline()
    {
        // Mirrors a thin hyperlink text stroke surrounded by ordinary
        // light background -- exactly the case this threshold exists to
        // reject, per the class's own doc.
        var selectionBlue = ((byte)20, (byte)90, (byte)170);
        var lightBackground = ((byte)245, (byte)245, (byte)245);

        var pixels = new List<(byte, byte, byte)>();
        for (var i = 0; i < 15; i++) pixels.Add(selectionBlue);
        for (var i = 0; i < 85; i++) pixels.Add(lightBackground);

        Assert.False(SelectionRowColorDetector.IsSelectionScanline(pixels));
    }

    [Fact]
    public void ScanlineExactlyAtTheFractionThresholdCounts()
    {
        var selectionBlue = ((byte)20, (byte)90, (byte)170);
        var other = ((byte)245, (byte)245, (byte)245);

        var pixels = new List<(byte, byte, byte)>();
        for (var i = 0; i < 60; i++) pixels.Add(selectionBlue);
        for (var i = 0; i < 40; i++) pixels.Add(other);

        Assert.True(SelectionRowColorDetector.IsSelectionScanline(pixels, minFraction: 0.6));
    }

    [Fact]
    public void EmptyScanlineIsNeverASelectionScanline()
    {
        Assert.False(SelectionRowColorDetector.IsSelectionScanline(new List<(byte, byte, byte)>()));
    }
}
