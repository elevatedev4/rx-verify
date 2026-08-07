using RxVerifyOverlay.Diagnostics;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for RefreshTiming (Diagnostics/RefreshTiming.cs) and
/// RxLogFormatter.FormatTimingLine (Diagnostics/RxLogFormatter.cs) — the
/// latency-fix instrumentation that turns a refresh's per-stage
/// Stopwatch readings into the compact "Timing: ..." line written to
/// Ocr/OcrLogger.cs and the "Copy logs" blob (see
/// ViewModels/OverlayViewModel.cs RefreshAsync/ApplyDrugResult). Pure
/// logic, no process/UIA/WPF involved.
/// </summary>
public class RefreshTimingFormatterTests
{
    private static RefreshTiming MakeTiming(long? phase2Ms = null) => new()
    {
        AttachMs = 40,
        UiaMs = 55,
        CaptureMs = 56,
        CaptureRegionResolveMs = 3,
        CaptureHideWaitMs = 0,
        CaptureBlitMs = 53,
        OcrMs = 105,
        EngineMs = 180,
        RenderMs = 8,
        Phase2Ms = phase2Ms
    };

    [Fact]
    public void Phase1TotalMsIsTheSumOfAllSixSegments()
    {
        var timing = MakeTiming();
        Assert.Equal(40 + 55 + 56 + 105 + 180 + 8, timing.Phase1TotalMs);
    }

    [Fact]
    public void Phase1TotalMsIgnoresPhase2()
    {
        var withoutPhase2 = MakeTiming();
        var withPhase2 = MakeTiming(phase2Ms: 999);
        Assert.Equal(withoutPhase2.Phase1TotalMs, withPhase2.Phase1TotalMs);
    }

    [Fact]
    public void FormatTimingLineWithoutPhase2MatchesTheDocumentedFormat()
    {
        var timing = MakeTiming();

        var line = RxLogFormatter.FormatTimingLine(timing);

        Assert.Equal(
            "Timing: detect->render 444ms (attach 40 + uia 55 + capture 56 [region 3 + hidewait 0 + blit 53] + ocr 105 + engine 180 + render 8)",
            line);
    }

    [Fact]
    public void FormatTimingLineWithPhase2AppendsTheSuffix()
    {
        var timing = MakeTiming(phase2Ms: 240);

        var line = RxLogFormatter.FormatTimingLine(timing);

        Assert.Equal(
            "Timing: detect->render 444ms (attach 40 + uia 55 + capture 56 [region 3 + hidewait 0 + blit 53] + ocr 105 + engine 180 + render 8) - phase2 +240ms",
            line);
    }

    [Fact]
    public void FormatTimingLineHandlesAllZeroSegments()
    {
        var timing = new RefreshTiming();

        var line = RxLogFormatter.FormatTimingLine(timing);

        Assert.Equal(
            "Timing: detect->render 0ms (attach 0 + uia 0 + capture 0 [region 0 + hidewait 0 + blit 0] + ocr 0 + engine 0 + render 0)",
            line);
    }

    [Fact]
    public void CaptureMsSubPartsAreExposedIndependentlyOfTheAggregate()
    {
        // CaptureMs is set independently by OcrFieldReader (region-resolve
        // + hide-wait + blit summed there — see Uia/OcrFieldReader.cs
        // ReadSourceFromOcrAsync), not derived from the sub-parts here, so
        // RefreshTiming itself doesn't enforce the sum — this just proves
        // the three sub-part properties round-trip independently of
        // CaptureMs and of each other, which is what FormatTimingLine
        // relies on to render the "[region + hidewait + blit]" breakdown.
        var timing = MakeTiming();

        Assert.Equal(3, timing.CaptureRegionResolveMs);
        Assert.Equal(0, timing.CaptureHideWaitMs);
        Assert.Equal(53, timing.CaptureBlitMs);
    }
}
