using RxVerifyOverlay.Integrated;
using RxVerifyOverlay.Models;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for BoxColorMapper (Integrated/BoxColorMapper.cs) — the
/// owner's explicit binary verdict-box spec: Green draws a green box,
/// everything else (Yellow AND Red) draws a red "check it" box. The
/// separate window's own 3-color rendering (MainWindow.xaml) is
/// unaffected by this — this mapping only ever feeds
/// IntegratedBoxesWindow.
/// </summary>
public class BoxColorMapperTests
{
    [Fact]
    public void GreenVerdictIsAGreenBox()
    {
        Assert.True(BoxColorMapper.IsGreenBox(VerdictStatus.Green));
    }

    [Fact]
    public void YellowVerdictCollapsesToARedBox()
    {
        Assert.False(BoxColorMapper.IsGreenBox(VerdictStatus.Yellow));
    }

    [Fact]
    public void RedVerdictIsARedBox()
    {
        Assert.False(BoxColorMapper.IsGreenBox(VerdictStatus.Red));
    }
}
