using System;
using System.Drawing;
using System.Drawing.Imaging;
using RxVerifyOverlay.OrderAssist.Ocr;
using RxVerifyOverlay.OrderAssist.Windows;
using Xunit;

namespace RxVerifyOverlay.Tests.OrderAssist;

/// <summary>
/// End-to-end (real System.Drawing Bitmap, Windows only -- no-ops elsewhere)
/// checks that the two RowNormalizerModes really select different paths and
/// that the 60% safety valve leaves the bitmap untouched.
/// </summary>
public class RowHighlightNormalizerModeTests
{
    private static readonly Color PaleYellow = Color.FromArgb(255, 255, 179);

    private static Bitmap TintedBandBitmap()
    {
        var bmp = new Bitmap(200, 66, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            using var brush = new SolidBrush(PaleYellow);
            g.FillRectangle(brush, 0, 22, 200, 22);
        }

        // Thin black strokes (text-like) and one mid-gray mark, all inside the band.
        for (var x = 10; x < 50; x += 4)
            for (var y = 28; y < 35; y++)
                bmp.SetPixel(x, y, Color.Black);
        for (var y = 26; y < 37; y++)
            bmp.SetPixel(60, y, Color.FromArgb(128, 128, 128));

        return bmp;
    }

    [Fact]
    public void LocalModeKeepsContinuousGrayAndLegacyModeBinarizes()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var local = TintedBandBitmap();
        var localResult = RowHighlightNormalizer.NormalizeInPlace(local, RowNormalizerMode.Local);
        Assert.NotEmpty(localResult.AcceptedBands);
        Assert.False(localResult.SafetyValveTripped);
        Assert.Equal(255, local.GetPixel(100, 25).R);   // tinted background became white
        Assert.Equal(128, local.GetPixel(60, 30).R);    // proportional gray survived

        using var legacy = TintedBandBitmap();
        var legacyResult = RowHighlightNormalizer.NormalizeInPlace(legacy, RowNormalizerMode.Legacy);
        Assert.NotEmpty(legacyResult.AcceptedBands);
        Assert.Equal(255, legacy.GetPixel(100, 25).R);  // legacy also whitens the band...
        Assert.Equal(0, legacy.GetPixel(60, 30).R);     // ...but hard-binarizes everything else to black
    }

    [Fact]
    public void DefaultModeIsLocal()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var bmp = TintedBandBitmap();
        RowHighlightNormalizer.NormalizeInPlace(bmp);
        Assert.Equal(128, bmp.GetPixel(60, 30).R);
    }

    [Fact]
    public void SafetyValveTripFallsBackToTheLegacyNormalizerAndReportsIt()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var bmp = new Bitmap(200, 66, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) g.Clear(PaleYellow); // 100% tinted: local pass would rewrite everything
        for (var x = 10; x < 50; x += 4)
            for (var y = 28; y < 35; y++)
                bmp.SetPixel(x, y, Color.Black);
        for (var y = 26; y < 37; y++)
            bmp.SetPixel(60, y, Color.FromArgb(128, 128, 128));

        var result = RowHighlightNormalizer.NormalizeInPlace(bmp, RowNormalizerMode.Local);

        Assert.True(result.SafetyValveTripped);
        Assert.NotEmpty(result.AcceptedBands);           // legacy output, not the raw bitmap
        Assert.Equal(255, bmp.GetPixel(100, 5).R);       // tinted background whitened by legacy
        Assert.Equal(0, bmp.GetPixel(10, 30).R);         // text black
        Assert.Equal(0, bmp.GetPixel(60, 30).R);         // legacy's hard binarization (local would keep 128)
    }

    [Fact]
    public void CatalogSubstitutionDefaultPixelsAreIdenticalToTheLegacyNormalizer()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var viaPolicy = TintedBandBitmap();
        var mode = RowNormalizerWindowPolicy.Resolve(OrderAssistWindowKind.CatalogSubstitution, "local");
        RowHighlightNormalizer.NormalizeInPlace(viaPolicy, mode);

        using var direct = TintedBandBitmap();
        LegacyRowHighlightNormalizer.NormalizeInPlace(direct);

        for (var y = 0; y < direct.Height; y++)
            for (var x = 0; x < direct.Width; x++)
                Assert.Equal(direct.GetPixel(x, y).ToArgb(), viaPolicy.GetPixel(x, y).ToArgb());
    }
}
