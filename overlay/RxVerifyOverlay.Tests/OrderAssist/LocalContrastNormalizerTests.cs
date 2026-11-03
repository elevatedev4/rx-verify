using System;
using System.Collections.Generic;
using RxVerifyOverlay.OrderAssist.Ocr;
using Xunit;

namespace RxVerifyOverlay.Tests.OrderAssist;

/// <summary>
/// Pixel-level tests for LocalContrastNormalizer on synthetic BGRA buffers
/// (no System.Drawing, runs on any OS). Each scenario is one table row of
/// 5x7-pixel digit "text" with a 35% anti-aliasing halo, on the background
/// under test. After normalizing, text core pixels must be dark and the
/// background pure white; plain white rows must not change AT ALL.
/// </summary>
public class LocalContrastNormalizerTests
{
    private const int W = 200;
    private const int H = 22;

    private static readonly (byte R, byte G, byte B) White = (255, 255, 255);
    private static readonly (byte R, byte G, byte B) Black = (0, 0, 0);

    private static readonly Dictionary<char, string[]> Glyphs = new()
    {
        ['1'] = new[] { "00100", "01100", "00100", "00100", "00100", "00100", "01110" },
        ['2'] = new[] { "01110", "10001", "00001", "00010", "00100", "01000", "11111" },
        ['3'] = new[] { "11110", "00001", "00001", "01110", "00001", "00001", "11110" },
        ['5'] = new[] { "11111", "10000", "11110", "00001", "00001", "10001", "01110" },
        ['8'] = new[] { "01110", "10001", "10001", "01110", "10001", "10001", "01110" },
        ['E'] = new[] { "11111", "10000", "10000", "11110", "10000", "10000", "11111" },
        ['T'] = new[] { "11111", "00100", "00100", "00100", "00100", "00100", "00100" },
    };

    /// <summary>Small BGRA image helper; stride may be wider than width*4 (row padding).</summary>
    private sealed class Img
    {
        public readonly byte[] Buf;
        public readonly int Width;
        public readonly int Height;
        public readonly int Stride;

        public Img(int width, int height, (byte R, byte G, byte B) bg, int strideExtra = 0)
        {
            Width = width;
            Height = height;
            Stride = width * 4 + strideExtra;
            Buf = new byte[Stride * height];
            Fill(0, 0, width, height, bg);
        }

        public void Set(int x, int y, (byte R, byte G, byte B) c)
        {
            var p = y * Stride + x * 4;
            Buf[p] = c.B; Buf[p + 1] = c.G; Buf[p + 2] = c.R; Buf[p + 3] = 255;
        }

        public (byte R, byte G, byte B) Get(int x, int y)
        {
            var p = y * Stride + x * 4;
            return (Buf[p + 2], Buf[p + 1], Buf[p]);
        }

        public void Fill(int x0, int y0, int x1, int y1, (byte R, byte G, byte B) c)
        {
            for (var y = y0; y < y1; y++)
                for (var x = x0; x < x1; x++)
                    Set(x, y, c);
        }

        /// <summary>Draws glyphs (7 px advance x scale); returns the solid core pixels (every scale x scale block). Anti-aliased halo on the 4-neighbours.</summary>
        public List<(int X, int Y)> Text(int x, int y, string s, (byte R, byte G, byte B) color, int scale = 1)
        {
            var cores = new List<(int X, int Y)>();
            var coreSet = new HashSet<(int, int)>();

            for (var ci = 0; ci < s.Length; ci++)
            {
                var g = Glyphs[s[ci]];
                for (var gy = 0; gy < g.Length; gy++)
                    for (var gx = 0; gx < g[gy].Length; gx++)
                        if (g[gy][gx] == '1')
                            for (var sy = 0; sy < scale; sy++)
                                for (var sx = 0; sx < scale; sx++)
                                {
                                    var px = x + ci * 7 * scale + gx * scale + sx;
                                    var py = y + gy * scale + sy;
                                    cores.Add((px, py));
                                    coreSet.Add((px, py));
                                }
            }

            // Halo: computed from the still-unpainted background, written after the cores.
            var halo = new Dictionary<(int, int), (byte R, byte G, byte B)>();
            foreach (var (px, py) in cores)
            {
                foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    var qx = px + dx;
                    var qy = py + dy;
                    if (coreSet.Contains((qx, qy)) || halo.ContainsKey((qx, qy)) || qx < 0 || qy < 0 || qx >= Width || qy >= Height) continue;
                    var bg = Get(qx, qy);
                    halo[(qx, qy)] = (Mix(bg.R, color.R), Mix(bg.G, color.G), Mix(bg.B, color.B));
                }
            }

            foreach (var (px, py) in cores) Set(px, py, color);
            foreach (var kv in halo) Set(kv.Key.Item1, kv.Key.Item2, kv.Value);

            return cores;
        }

        private static byte Mix(byte bg, byte fg) => (byte)Math.Round(bg * 0.65 + fg * 0.35);
    }

    /// <summary>Most scenarios below are entirely one colored row, which the production safety valve (60% of pixels) would rightly discard; the valve has its own tests.</summary>
    private static LocalContrastNormalizer.Result Run(Img img) =>
        LocalContrastNormalizer.Normalize(img.Buf, img.Width, img.Height, img.Stride, maxChangedFraction: 1.0);

    private static int Gray(Img img, int x, int y) => img.Get(x, y).R;

    private static void AssertCoreIsDark(Img img, List<(int X, int Y)> cores, int maxGray = 80)
    {
        foreach (var (x, y) in cores)
        {
            var (r, g, b) = img.Get(x, y);
            Assert.True(r == g && g == b, $"text pixel ({x},{y}) should be gray, was ({r},{g},{b})");
            Assert.True(r <= maxGray, $"text pixel ({x},{y}) should be dark (<= {maxGray}), was {r}");
        }
    }

    private static void AssertRegionIsWhite(Img img, int x0, int y0, int x1, int y1)
    {
        for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++)
                Assert.True(img.Get(x, y) == White, $"background ({x},{y}) should be pure white, was {img.Get(x, y)}");
    }

    [Fact]
    public void PlainWhiteRowIsByteForByteUnchanged()
    {
        var img = new Img(W, H, White);
        img.Text(10, 7, "1235", Black);
        img.Text(120, 7, "E8T", (0, 0, 160)); // colored text on white must also be left alone
        var before = (byte[])img.Buf.Clone();

        var result = Run(img);

        Assert.Equal(0, result.ChangedPixels);
        Assert.Empty(result.ChangedBands);
        Assert.Equal(before, img.Buf);
    }

    [Fact]
    public void NearWhiteAndLightGrayRowsAreLeftAlone()
    {
        var img = new Img(W, H, (240, 240, 240));
        img.Text(10, 7, "1235", Black);
        var before = (byte[])img.Buf.Clone();

        Assert.Equal(0, Run(img).ChangedPixels);
        Assert.Equal(before, img.Buf);
    }

    [Theory]
    [InlineData(255, 255, 179)] // pale yellow
    [InlineData(200, 220, 245)] // pale header blue
    [InlineData(173, 216, 230)] // light blue
    [InlineData(210, 235, 210)] // pale green banding
    [InlineData(255, 230, 0)]   // saturated yellow flag
    public void PaleOrBrightTintRowReadsAsDarkTextOnWhite(int r, int g, int b)
    {
        var img = new Img(W, H, ((byte)r, (byte)g, (byte)b));
        var cores = img.Text(10, 7, "1235", Black);
        cores.AddRange(img.Text(120, 7, "E8T", Black));

        var result = Run(img);

        Assert.True(result.ChangedPixels > 0);
        AssertCoreIsDark(img, cores);
        AssertRegionIsWhite(img, 0, 0, W, 5); // padding above the text
        AssertRegionIsWhite(img, 0, 16, W, H); // padding below
    }

    [Theory]
    [InlineData(49, 106, 197)] // selection blue
    [InlineData(20, 30, 80)]   // dark navy
    [InlineData(0, 100, 0)]    // dark green
    public void DarkFillWithWhiteTextBecomesDarkOnWhite(int r, int g, int b)
    {
        var img = new Img(W, H, ((byte)r, (byte)g, (byte)b));
        var cores = img.Text(10, 7, "1235", White);

        var result = Run(img);

        Assert.True(result.ChangedPixels > 0);
        AssertCoreIsDark(img, cores);
        AssertRegionIsWhite(img, 0, 0, W, 5);
        AssertRegionIsWhite(img, 0, 16, W, H);
    }

    [Fact]
    public void PartialWidthFillReadsBothTheFilledAndTheUnfilledSide()
    {
        // Left half is a blue selection fill holding right-aligned white
        // text that ends 12 px before the fill edge; the right half is a
        // plain white cell with black text. A whole-scanline median (the
        // old gate / round 6) sees mostly one color; this must not care.
        var img = new Img(W, H, White);
        img.Fill(0, 0, 100, H, (49, 106, 197));
        var blueSideCores = img.Text(60, 7, "1235", White);
        var whiteSideCores = img.Text(120, 7, "E8T", Black);
        var whiteSideBefore = (byte[])img.Buf.Clone();

        Run(img);

        AssertCoreIsDark(img, blueSideCores);
        AssertCoreIsDark(img, whiteSideCores);
        AssertRegionIsWhite(img, 0, 0, 100, 5);

        // The plain-white side (right of the fill edge + 4 px) is untouched.
        for (var y = 0; y < H; y++)
            for (var x = 104; x < W; x++)
                Assert.Equal(PixelBytes(whiteSideBefore, img.Stride, x, y), PixelBytes(img.Buf, img.Stride, x, y));
    }

    [Fact]
    public void FillNarrowerThanHalfTheCaptureStillReads()
    {
        // 20% fill (e.g. one colored cell among white ones), pale yellow.
        var img = new Img(W, H, White);
        img.Fill(20, 0, 60, H, (255, 255, 179));
        var cores = img.Text(26, 7, "123", Black);

        Run(img);

        AssertCoreIsDark(img, cores);
        AssertRegionIsWhite(img, 20, 0, 60, 5);
    }

    [Fact]
    public void WhiteInEditCellInsideAColoredRowIsKeptAndTheRestOfTheRowStillReads()
    {
        var img = new Img(W, H, (49, 106, 197));
        img.Fill(100, 3, 170, 19, White);
        var blueCores = img.Text(10, 7, "1235", White);
        var cellCores = img.Text(110, 7, "E8", Black);
        var cellBefore = (byte[])img.Buf.Clone();

        Run(img);

        AssertCoreIsDark(img, blueCores);
        AssertCoreIsDark(img, cellCores);

        // The white edit cell (interior, away from its 1-px frame) is byte-identical.
        for (var y = 4; y < 18; y++)
            for (var x = 101; x < 169; x++)
                Assert.Equal(PixelBytes(cellBefore, img.Stride, x, y), PixelBytes(img.Buf, img.Stride, x, y));
    }

    [Fact]
    public void AdjacentDifferentlyColoredCellsEachReadAgainstTheirOwnBackground()
    {
        var img = new Img(W, H, White);
        img.Fill(0, 0, 50, H, (255, 255, 179));
        img.Fill(50, 0, 100, H, (200, 220, 245));
        img.Fill(100, 0, 150, H, (49, 106, 197));
        var yellow = img.Text(22, 7, "1235", Black);
        var blue = img.Text(70, 7, "E8T", Black);
        var dark = img.Text(118, 7, "1235", White);

        Run(img);

        AssertCoreIsDark(img, yellow);
        AssertCoreIsDark(img, blue);
        AssertCoreIsDark(img, dark);
    }

    [Fact]
    public void TightRowPaddingStillFindsTheBackground()
    {
        var img = new Img(W, 12, (255, 255, 179));
        var cores = img.Text(10, 2, "1235", Black); // 2 px padding top and bottom

        Run(img);

        AssertCoreIsDark(img, cores);
    }

    [Fact]
    public void AntiAliasedEdgesStayContinuousNotBinarized()
    {
        var img = new Img(W, H, (255, 255, 179));
        var cores = img.Text(10, 7, "1235", Black);
        var coreSet = new HashSet<(int, int)>(cores);

        Run(img);

        // The halo pixels (35% black over pale yellow) must land strictly between
        // the core (~0) and the background (255): proportional, never snapped.
        var sawIntermediate = false;
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                if (coreSet.Contains((x, y))) continue;
                var g = Gray(img, x, y);
                if (g > 20 && g < 235) sawIntermediate = true;
            }
        Assert.True(sawIntermediate, "anti-aliased halo should keep intermediate gray levels");
    }

    [Fact]
    public void RowStridePaddingBytesAreNeverTouched()
    {
        var img = new Img(W, H, (255, 255, 179), strideExtra: 8);
        for (var y = 0; y < H; y++)
            for (var k = 0; k < 8; k++)
                img.Buf[y * img.Stride + W * 4 + k] = 0xAB;
        var cores = img.Text(10, 7, "1235", Black);

        Run(img);

        AssertCoreIsDark(img, cores);
        for (var y = 0; y < H; y++)
            for (var k = 0; k < 8; k++)
                Assert.Equal(0xAB, img.Buf[y * img.Stride + W * 4 + k]);
    }

    [Fact]
    public void AlphaChannelIsNeverTouched()
    {
        var img = new Img(W, H, (49, 106, 197));
        img.Text(10, 7, "1235", White);

        Run(img);

        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
                Assert.Equal(255, img.Buf[y * img.Stride + x * 4 + 3]);
    }

    [Fact]
    public void NormalizingTwiceChangesNothingTheSecondTime()
    {
        var img = new Img(W, H, (255, 255, 179));
        img.Text(10, 7, "1235", Black);
        Run(img);
        var afterFirst = (byte[])img.Buf.Clone();

        var second = Run(img);

        Assert.Equal(0, second.ChangedPixels);
        Assert.Equal(afterFirst, img.Buf);
    }

    [Fact]
    public void ChangedBandsCoverOnlyTheColoredRows()
    {
        // 3 stacked 22-px rows: white / pale yellow / white.
        var img = new Img(W, H * 3, White);
        img.Text(10, 7, "1235", Black);
        img.Fill(0, H, W, H * 2, (255, 255, 179));
        img.Text(10, H + 7, "1235", Black);
        img.Text(10, H * 2 + 7, "1235", Black);

        var result = Run(img);

        Assert.Single(result.ChangedBands);
        var (top, bottom) = result.ChangedBands[0];
        Assert.True(top >= H - 1 && top <= H + 1, $"band top {top}");
        Assert.True(bottom >= H * 2 - 1 && bottom <= H * 2 + 1, $"band bottom {bottom}");
    }

    [Fact]
    public void LongHorizontalStrokeOrThinGridLineIsNotTreatedAsBackground()
    {
        var img = new Img(W, H, (255, 255, 179));
        for (var x = 0; x < W; x++) img.Set(x, 0, (120, 120, 120)); // 1-px full-width line
        for (var x = 20; x < 40; x++) { img.Set(x, 10, Black); }     // 1-px 20-px-long stroke

        var flat = LocalContrastNormalizer.ComputeFlatMap(img.Buf, W, H, img.Stride);

        Assert.Equal(0, flat[0 * W + 50]);
        Assert.Equal(0, flat[10 * W + 30]);
        Assert.NotEqual(0, flat[5 * W + 100]); // ordinary fill pixel is background
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    public void DegenerateSizesDoNothing(int width, int height)
    {
        var result = LocalContrastNormalizer.Normalize(new byte[16], width, height, 4);
        Assert.Equal(0, result.ChangedPixels);
    }

    [Fact]
    public void AllPlainFullHdCaptureShortCircuitsQuickly()
    {
        // Coarse performance guard (not a benchmark): a 1920x1080-sized
        // all-plain capture short-circuits after the flat map.
        var img = new Img(1920, 1080, White);
        img.Text(100, 100, "1235", Black);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = Run(img);
        sw.Stop();

        Assert.Equal(0, result.ChangedPixels);
        Assert.True(sw.ElapsedMilliseconds < 3000, $"took {sw.ElapsedMilliseconds} ms");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void ScaledGlyphsOnPaleTintKeepTheirStrokes(int scale)
    {
        // 2x / 3x glyphs have solid bars 2-3 px thick and up to 15 px wide.
        // They must not be classified as background and whited out.
        var img = new Img(W, 14 * scale + 24, (255, 255, 179));
        var cores = img.Text(10, 12, "E8", Black, scale);

        Run(img);

        AssertCoreIsDark(img, cores);
        AssertRegionIsWhite(img, 0, 0, W, 8);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void ScaledWhiteGlyphsOnDarkFillKeepTheirStrokes(int scale)
    {
        var img = new Img(W, 14 * scale + 24, (49, 106, 197));
        var cores = img.Text(10, 12, "E8", White, scale);

        Run(img);

        AssertCoreIsDark(img, cores);
        AssertRegionIsWhite(img, 0, 0, W, 8);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void ScaledGlyphsOnPlainWhiteAreUntouched(int scale)
    {
        var img = new Img(W, 14 * scale + 24, White);
        img.Text(10, 12, "E8", Black, scale);
        var before = (byte[])img.Buf.Clone();

        Assert.Equal(0, Run(img).ChangedPixels);
        Assert.Equal(before, img.Buf);
    }

    [Theory]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    public void TightAlternatingWhiteAndBlueRowsEachReadAgainstTheirOwnBackground(int rowHeight)
    {
        const int rows = 6;
        var img = new Img(W, rowHeight * rows, White);
        var blueCores = new List<(int X, int Y)>();
        var whiteCores = new List<(int X, int Y)>();
        for (var r = 0; r < rows; r++)
        {
            var top = r * rowHeight;
            var textY = top + (rowHeight - 7) / 2;
            if (r % 2 == 1)
            {
                img.Fill(0, top, W, top + rowHeight, (49, 106, 197));
                blueCores.AddRange(img.Text(10, textY, "1235", White));
            }
            else
            {
                whiteCores.AddRange(img.Text(10, textY, "1235", Black));
            }
        }
        var before = (byte[])img.Buf.Clone();

        Run(img);

        AssertCoreIsDark(img, blueCores);

        // A white row must never reference the neighbouring blue row: its
        // text pixels are byte-for-byte what they were.
        foreach (var (x, y) in whiteCores)
            Assert.Equal(PixelBytes(before, img.Stride, x, y), PixelBytes(img.Buf, img.Stride, x, y));
    }

    [Fact]
    public void SafetyValveDiscardsARewriteThatWouldChangeMostOfTheCapture()
    {
        var img = new Img(W, H, (255, 255, 179)); // the whole capture is one tinted fill
        img.Text(10, 7, "1235", Black);
        var before = (byte[])img.Buf.Clone();

        var result = LocalContrastNormalizer.Normalize(img.Buf, img.Width, img.Height, img.Stride);

        Assert.True(result.Discarded);
        Assert.Equal(0, result.ChangedPixels);
        Assert.True(result.WouldChangePixels > W * H * LocalContrastNormalizer.MaxChangedFraction);
        Assert.Equal(before, img.Buf); // original bitmap left intact
    }

    [Fact]
    public void SafetyValveLetsANormalScreenWithAFewColoredRowsThrough()
    {
        var img = new Img(W, H * 5, White);
        img.Fill(0, H * 2, W, H * 3, (255, 255, 179)); // 20% of the capture
        var cores = img.Text(10, H * 2 + 7, "1235", Black);

        var result = LocalContrastNormalizer.Normalize(img.Buf, img.Width, img.Height, img.Stride);

        Assert.False(result.Discarded);
        Assert.True(result.ChangedPixels > 0);
        AssertCoreIsDark(img, cores);
    }

    [Fact]
    public void ReusedWorkspaceDoesNotLeakStateBetweenDifferentlySizedCaptures()
    {
        var big = new Img(400, 100, White);
        big.Fill(0, 30, 400, 60, (255, 255, 179));
        big.Text(10, 37, "1235", Black);
        Assert.True(Run(big).ChangedPixels > 0);

        var plainSmall = new Img(W, H, White);
        plainSmall.Text(10, 7, "1235", Black);
        Assert.Equal(0, Run(plainSmall).ChangedPixels);

        var coloredSmall = new Img(W, H, (255, 255, 179));
        var cores = coloredSmall.Text(10, 7, "1235", Black);
        Run(coloredSmall);
        AssertCoreIsDark(coloredSmall, cores);
    }

    [Fact]
    public void ColoredFullHdCaptureNormalizesWithinAGenerousBudget()
    {
        // Realistic path: a 1080p window with every 4th table row tinted
        // (about 25% of the capture), so the rewrite runs end to end.
        var img = new Img(1920, 1080, White);
        const int rowH = 22;
        var tintedCores = new List<(int X, int Y)>();
        for (var r = 0; r * rowH + rowH <= 1080; r++)
        {
            var top = r * rowH;
            if (r % 4 == 1)
            {
                img.Fill(0, top, 1920, top + rowH, (255, 255, 179));
                tintedCores.AddRange(img.Text(40, top + 7, "1235", Black));
            }
            else
            {
                img.Text(40, top + 7, "1235", Black);
            }
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = LocalContrastNormalizer.Normalize(img.Buf, img.Width, img.Height, img.Stride);
        sw.Stop();

        Assert.False(result.Discarded);
        Assert.True(result.ChangedPixels > 0);
        AssertCoreIsDark(img, tintedCores);
        Assert.True(sw.ElapsedMilliseconds < 5000, $"took {sw.ElapsedMilliseconds} ms");
    }

    private static (byte, byte, byte) PixelBytes(byte[] buf, int stride, int x, int y)
    {
        var p = y * stride + x * 4;
        return (buf[p], buf[p + 1], buf[p + 2]);
    }
}
