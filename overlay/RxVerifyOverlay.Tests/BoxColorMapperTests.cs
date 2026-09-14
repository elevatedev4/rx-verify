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

    // Field report (owner, 2026-08-25, prescriberPhone, synthetic values
    // here): a source that was never read at all ("not_provided") must
    // never draw a box at all — not the red "check it" box IsGreenBox's
    // binary collapse would otherwise give it, since there is nothing to
    // check it against. See BoxColorMapper.ShouldDrawBox's doc.
    [Fact]
    public void NotProvidedReasonCodeDrawsNoBox()
    {
        Assert.False(BoxColorMapper.ShouldDrawBox("not_provided"));
    }

    [Fact]
    public void UnparseableDateReasonCodeDrawsNoBox()
    {
        Assert.False(BoxColorMapper.ShouldDrawBox("unparseable_date"));
    }

    [Fact]
    public void UnparseableQuantityReasonCodeDrawsNoBox()
    {
        Assert.False(BoxColorMapper.ShouldDrawBox("unparseable_quantity"));
    }

    [Fact]
    public void OrdinaryMismatchReasonCodeStillDrawsABox()
    {
        // Unchanged behavior for every OTHER yellow/red reasonCode —
        // this fix is scoped to the three unreadable codes only, never a
        // general "hide the box" escape hatch.
        Assert.True(BoxColorMapper.ShouldDrawBox("address_differs"));
        Assert.True(BoxColorMapper.ShouldDrawBox("phone_differs"));
        Assert.True(BoxColorMapper.ShouldDrawBox("refills_mismatch"));
        Assert.True(BoxColorMapper.ShouldDrawBox(null));
    }
}
