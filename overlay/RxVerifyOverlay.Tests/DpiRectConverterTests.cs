using System.Drawing;
using RxVerifyOverlay.Integrated;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for DpiRectConverter (Integrated/DpiRectConverter.cs) — the
/// pure DPI math behind the integrated boxes layer's field-outline
/// positioning. This is the "classic bug" the owner's spec explicitly
/// flagged: UIA's BoundingRectangle is always physical pixels, WPF layout
/// is always DIPs, and a workstation running above 100% scaling (125%
/// etc. are common) silently mis-draws boxes if that conversion is
/// skipped or wrong.
/// </summary>
public class DpiRectConverterTests
{
    [Fact]
    public void At100PercentScalingDipsEqualPhysicalPixelsMinusWindowOrigin()
    {
        var fieldRect = new Rectangle(x: 150, y: 250, width: 300, height: 20);
        var windowOrigin = new Point(100, 200);

        var dip = DpiRectConverter.ToDipRect(fieldRect, windowOrigin, dpiScaleX: 1.0, dpiScaleY: 1.0);

        Assert.Equal(50, dip.X);
        Assert.Equal(50, dip.Y);
        Assert.Equal(300, dip.Width);
        Assert.Equal(20, dip.Height);
    }

    [Fact]
    public void At125PercentScalingDipsAreScaledDownByTheDpiFactor()
    {
        // 125% scaling (a common pharmacy-workstation setting per the
        // owner's spec) — a field at physical (250, 350) on a window
        // whose own physical origin is (100, 100) should land at DIP
        // (120, 200): (150/1.25, 250/1.25).
        var fieldRect = new Rectangle(x: 250, y: 350, width: 250, height: 25);
        var windowOrigin = new Point(100, 100);

        var dip = DpiRectConverter.ToDipRect(fieldRect, windowOrigin, dpiScaleX: 1.25, dpiScaleY: 1.25);

        Assert.Equal(120, dip.X);
        Assert.Equal(200, dip.Y);
        Assert.Equal(200, dip.Width);
        Assert.Equal(20, dip.Height);
    }

    [Fact]
    public void FieldAtTheWindowsOwnOriginConvertsToZeroZero()
    {
        var fieldRect = new Rectangle(x: 500, y: 400, width: 100, height: 30);
        var windowOrigin = new Point(500, 400);

        var dip = DpiRectConverter.ToDipRect(fieldRect, windowOrigin, dpiScaleX: 1.5, dpiScaleY: 1.5);

        Assert.Equal(0, dip.X);
        Assert.Equal(0, dip.Y);
    }

    [Fact]
    public void SupportsDifferentXAndYScaleFactorsIndependently()
    {
        // Not a realistic Windows DPI configuration (X/Y scale are always
        // equal in practice), but DpiRectConverter takes them as
        // independent parameters — confirm it actually uses each
        // independently rather than only ever applying dpiScaleX.
        var fieldRect = new Rectangle(x: 100, y: 100, width: 200, height: 40);
        var windowOrigin = new Point(0, 0);

        var dip = DpiRectConverter.ToDipRect(fieldRect, windowOrigin, dpiScaleX: 2.0, dpiScaleY: 4.0);

        Assert.Equal(50, dip.X);
        Assert.Equal(25, dip.Y);
        Assert.Equal(100, dip.Width);
        Assert.Equal(10, dip.Height);
    }
}
