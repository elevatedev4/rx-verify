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
    // CaptureMs and UiaMs are NOT set here — both are computed properties
    // (mirroring Phase1TotalMs) so they can never drift from their
    // sub-parts: CaptureMs = CaptureRegionResolveMs + CaptureHideWaitMs +
    // CaptureBlitMs = 3 + 0 + 53 = 56; UiaMs = UiaFindMs + UiaReadMs =
    // 4 + 51 = 55. See CaptureMsIsAlwaysTheSumOfItsThreeSubParts /
    // UiaMsIsAlwaysTheSumOfItsTwoSubParts.
    private static RefreshTiming MakeTiming(long? phase2Ms = null, bool? attachCacheHit = null) => new()
    {
        AttachMs = 40,
        AttachCacheHit = attachCacheHit,
        UiaFindMs = 4,
        UiaReadMs = 51,
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
            "Timing: detect->render 444ms (attach 40 + uia 55 [find 4 + read 51] + capture 56 [region 3 + hidewait 0 + blit 53] + ocr 105 + engine 180 + render 8)",
            line);
    }

    [Fact]
    public void FormatTimingLineWithPhase2AppendsTheSuffix()
    {
        var timing = MakeTiming(phase2Ms: 240);

        var line = RxLogFormatter.FormatTimingLine(timing);

        Assert.Equal(
            "Timing: detect->render 444ms (attach 40 + uia 55 [find 4 + read 51] + capture 56 [region 3 + hidewait 0 + blit 53] + ocr 105 + engine 180 + render 8) - phase2 +240ms",
            line);
    }

    [Fact]
    public void FormatTimingLineHandlesAllZeroSegments()
    {
        var timing = new RefreshTiming();

        var line = RxLogFormatter.FormatTimingLine(timing);

        Assert.Equal(
            "Timing: detect->render 0ms (attach 0 + uia 0 [find 0 + read 0] + capture 0 [region 0 + hidewait 0 + blit 0] + ocr 0 + engine 0 + render 0)",
            line);
    }

    [Fact]
    public void CaptureMsSubPartsRoundTripIndependently()
    {
        var timing = MakeTiming();

        Assert.Equal(3, timing.CaptureRegionResolveMs);
        Assert.Equal(0, timing.CaptureHideWaitMs);
        Assert.Equal(53, timing.CaptureBlitMs);
    }

    [Fact]
    public void CaptureMsIsAlwaysTheSumOfItsThreeSubParts()
    {
        // Post-review fix: CaptureMs is a computed property (mirroring
        // Phase1TotalMs) instead of a separately-set field, specifically
        // so it can never drift from CaptureRegionResolveMs +
        // CaptureHideWaitMs + CaptureBlitMs.
        var timing = MakeTiming();

        Assert.Equal(3 + 0 + 53, timing.CaptureMs);

        timing.CaptureBlitMs = 999;

        Assert.Equal(3 + 0 + 999, timing.CaptureMs);
    }

    [Fact]
    public void UiaMsIsAlwaysTheSumOfItsTwoSubParts()
    {
        // uia-read-latency branch: UiaMs is a computed property
        // (mirroring CaptureMs/Phase1TotalMs) instead of a separately-set
        // field, so it can never drift from UiaFindMs + UiaReadMs.
        var timing = MakeTiming();

        Assert.Equal(4 + 51, timing.UiaMs);

        timing.UiaFindMs = 500;

        Assert.Equal(500 + 51, timing.UiaMs);
    }

    [Fact]
    public void FormatTimingLineOmitsAttachTagWhenAttachCacheHitIsNull()
    {
        var timing = MakeTiming(attachCacheHit: null);

        var line = RxLogFormatter.FormatTimingLine(timing);

        Assert.Contains("attach 40 + uia", line);
        Assert.DoesNotContain("[hit]", line);
        Assert.DoesNotContain("[resolve]", line);
    }

    [Fact]
    public void FormatTimingLineTagsAttachHitWhenAttachCacheHitIsTrue()
    {
        var timing = MakeTiming(attachCacheHit: true);

        var line = RxLogFormatter.FormatTimingLine(timing);

        Assert.Contains("attach 40 [hit] + uia", line);
    }

    [Fact]
    public void FormatTimingLineTagsAttachResolveWhenAttachCacheHitIsFalse()
    {
        var timing = MakeTiming(attachCacheHit: false);

        var line = RxLogFormatter.FormatTimingLine(timing);

        Assert.Contains("attach 40 [resolve] + uia", line);
    }

    [Fact]
    public void FormatTimingLineAppendsExclusionOnWhenCaptureExclusionActiveIsTrue()
    {
        var timing = MakeTiming();

        var line = RxLogFormatter.FormatTimingLine(timing, captureExclusionActive: true);

        Assert.Equal(
            "Timing: detect->render 444ms (attach 40 + uia 55 [find 4 + read 51] + capture 56 [region 3 + hidewait 0 + blit 53] + ocr 105 + engine 180 + render 8) · exclusion on",
            line);
    }

    [Fact]
    public void FormatTimingLineAppendsExclusionOffWhenCaptureExclusionActiveIsFalse()
    {
        var timing = MakeTiming();

        var line = RxLogFormatter.FormatTimingLine(timing, captureExclusionActive: false);

        Assert.Equal(
            "Timing: detect->render 444ms (attach 40 + uia 55 [find 4 + read 51] + capture 56 [region 3 + hidewait 0 + blit 53] + ocr 105 + engine 180 + render 8) · exclusion off",
            line);
    }

    [Fact]
    public void FormatTimingLineOmitsExclusionTokenWhenCaptureExclusionActiveIsNull()
    {
        // Null means "no visibility controller wired up at all" (e.g. a
        // test host with no live WPF window) — must NOT print a
        // misleading "exclusion off", since that would read as "the
        // fallback ran" when really nothing about capture-exclusion is
        // known at all. This is also the default (all the other tests in
        // this file call FormatTimingLine(timing) with no second arg),
        // so it doubles as regression coverage that the parameter stays
        // opt-in.
        var timing = MakeTiming();

        var line = RxLogFormatter.FormatTimingLine(timing, captureExclusionActive: null);

        Assert.DoesNotContain("exclusion", line);
    }

    [Fact]
    public void FormatTimingLineWithPhase2AndExclusionOrdersThePhase2SuffixFirst()
    {
        var timing = MakeTiming(phase2Ms: 240);

        var line = RxLogFormatter.FormatTimingLine(timing, captureExclusionActive: true);

        Assert.Equal(
            "Timing: detect->render 444ms (attach 40 + uia 55 [find 4 + read 51] + capture 56 [region 3 + hidewait 0 + blit 53] + ocr 105 + engine 180 + render 8) - phase2 +240ms · exclusion on",
            line);
    }
}
