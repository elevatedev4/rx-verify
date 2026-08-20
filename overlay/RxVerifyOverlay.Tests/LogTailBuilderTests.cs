using System.Collections.Generic;
using System.Linq;
using RxVerifyOverlay.Diagnostics;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for LogTailBuilder (Diagnostics/LogTailBuilder.cs) — the
/// PHI-safe line allowlist behind the error-report "logTail" attachment
/// (Integrated/ReportErrorWindow.xaml.cs Submit -&gt; RxReportPayload.LogTail).
/// All log lines below are synthetic (fake names/DOBs/addresses that were
/// never real patient data) but shaped exactly like Ocr/OcrLogger.cs's
/// real output, to prove the allowlist rejects them by structure, not by
/// content-sniffing.
/// </summary>
public class LogTailBuilderTests
{
    private const string Ts = "[2026-08-17 10:15:00.123] ";

    [Fact]
    public void TimingLinesAreIncluded()
    {
        var line = Ts + "Timing: detect->render 612ms (attach 3 [hit] + uia 62 [find 4 + read 58] + capture 56 [region 3 + hidewait 0 + blit 53] + ocr 105 + engine 180 + render 8)";
        Assert.True(LogTailBuilder.IsSafeLine(line));
    }

    [Fact]
    public void RightClickDiagLinesAreIncluded()
    {
        var line = Ts + "[RIGHTCLICK-DIAG] MainWindow: handler entered fieldKey=quantity";
        Assert.True(LogTailBuilder.IsSafeLine(line));
    }

    [Fact]
    public void RightClickDiagExceptionLineIsExcluded()
    {
        // Only line in the RIGHTCLICK-DIAG family that interpolates a full
        // exception rather than fixed identifiers/booleans — excluded per
        // "when in doubt, exclude the line", even though this call path
        // never puts field values into an exception.
        var line = Ts + "[RIGHTCLICK-DIAG] MainWindow: EXCEPTION constructing/showing dialog: System.InvalidOperationException: Window already has a Window as its Owner.";
        Assert.False(LogTailBuilder.IsSafeLine(line));
    }

    [Fact]
    public void RawOcrTextLineWithFakePatientDataIsExcluded()
    {
        // Shaped exactly like OcrLogger.LogRead's raw-text body: no leading
        // timestamp bracket at all (see OcrLogger.LogRead's plain
        // sb.AppendLine(rawText) call) — this alone must exclude it,
        // regardless of content. Synthetic fake identity, never real PHI.
        var rawOcrLine = "Patient: Jordan Q. Testperson DOB: 04/12/1975 123 Fake Elm St, Sample City, KS 66047";
        Assert.False(LogTailBuilder.IsSafeLine(rawOcrLine));
    }

    [Fact]
    public void OcrReadBlockHeaderLineIsExcluded()
    {
        // Has the timestamp bracket (OcrLogger.LogRead writes it) but the
        // remainder ("OCR read") isn't on the allowlist — the raw text
        // that follows it in the real file never even gets this far.
        var line = Ts + "OCR read";
        Assert.False(LogTailBuilder.IsSafeLine(line));
    }

    [Fact]
    public void OcrErrorBlockLineIsExcluded()
    {
        var line = Ts + "OCR ERROR: System.Exception: something went wrong reading the screen";
        Assert.False(LogTailBuilder.IsSafeLine(line));
    }

    [Fact]
    public void OrderAssistWindowTitleLineIsExcluded()
    {
        // OrderAssistCoordinator's own doc already flags this exact line
        // shape as a PHI caveat (an Edit-Rx window title can carry a
        // patient name) — must stay excluded even though it flows through
        // the same OcrLogger.LogTiming call as safe Timing:/[RIGHTCLICK-DIAG]
        // lines.
        var line = Ts + "OrderAssist: no target window matched this tick. Visible PioneerRx window titles: [Edit Rx - 0000123 - Amoxicillin - Testperson, Jordan]";
        Assert.False(LogTailBuilder.IsSafeLine(line));
    }

    [Fact]
    public void OrderAssistColumnDiagnosticLineIsExcluded()
    {
        var line = Ts + "OrderAssist[CreateRecommendedOrders]: column resolution failed for Order Qty. Resolved header bands this tick: [Item | Description]";
        Assert.False(LogTailBuilder.IsSafeLine(line));
    }

    [Fact]
    public void OrderAssistMultiCandidateColumnDiagnosticLineIsExcluded()
    {
        // 2026-08-18 (W-T76/78/81 fix): the SAME "OrderAssist[{kind}]:
        // column resolution failed for ..." prefix as
        // OrderAssistColumnDiagnosticLineIsExcluded above (never changed —
        // per that class's own doc, no new allowlist entry was added for
        // it) but a materially different tail — OrderAssistCoordinator.
        // LogColumnFailureOnce now echoes EVERY candidate row-window it
        // scanned (each with its own resolved band labels — screen text,
        // same PHI-adjacency as before) rather than just the winner. Must
        // stay excluded exactly like the single-candidate shape did.
        var line = Ts + "OrderAssist[CreateRecommendedOrders]: column resolution failed for Order Quantity. 3 candidate row-window(s) scanned: [rows=0-0 y=0-14 score=0 bands=(Create Recommended Orders | Actions)] [rows=1-1 y=20-32 score=0 bands=(Filter Type All)] [rows=1-2 y=20-52 score=1 bands=(Filter Type All Cost Per Unit)]";
        Assert.False(LogTailBuilder.IsSafeLine(line));
    }

    [Fact]
    public void RowHighlightNormalizerLineIsExcluded()
    {
        // W-T85 round 2 bug 2 fix, round 3 generalized (Ocr/RowHighlightNormalizer,
        // formerly SelectionRowNormalizer): OrderAssistCoordinator.LogSelectionBandsIfChanged
        // logs local-only band Y-ranges (no OCR'd text, no screenshot) —
        // still under the "OrderAssist:" prefix, which is not one of
        // IsSafeLine's two recognized tags, so this stays excluded the
        // same way every other OrderAssist diagnostic line already does.
        var line = Ts + "OrderAssist: row-highlight normalizer binarized 1 band(s) this tick: [40-52]";
        Assert.False(LogTailBuilder.IsSafeLine(line));
    }

    [Fact]
    public void PreCheckGateLineIsExcluded()
    {
        // Integrated/IntegratedOverlayCoordinator.cs's TickCore
        // (branch fix/precheck-mode-gate) logs the observed PioneerRx
        // window title next to its gate decision — this MUST stay off
        // the report allowlist (same reasoning as
        // OrderAssistWindowTitleLineIsExcluded above): "[PRECHECK-GATE]"
        // is deliberately not one of IsSafeLine's two recognized tags.
        var line = Ts + "[PRECHECK-GATE] mode changed to EditRx shouldRunVerifyChecks=False title=\"Edit Rx - 0000123 - Amoxicillin - Testperson, Jordan\"";
        Assert.False(LogTailBuilder.IsSafeLine(line));
    }

    [Fact]
    public void UntaggedLogTimingLineIsExcluded()
    {
        // e.g. UiaTreeWalker's diagnostic line, which has the timestamp
        // bracket but no recognized tag — default-deny.
        var line = Ts + "UiaTreeWalker.FindDescendantTabItemByNamePrefix: 2 TabItems matched prefix 'Rx' — using the first one found (document order).";
        Assert.False(LogTailBuilder.IsSafeLine(line));
    }

    [Fact]
    public void BuildSafeTailFiltersAndPreservesOrder()
    {
        var lines = new List<string>
        {
            Ts + "=====================================================",
            Ts + "OCR read",
            "Patient: Jordan Q. Testperson DOB: 04/12/1975",
            "--- end raw text ---",
            Ts + "Timing: detect->render 100ms (attach 1 + uia 2 [find 1 + read 1] + capture 3 [region 0 + hidewait 0 + blit 3] + ocr 4 + engine 5 + render 1)",
            Ts + "[RIGHTCLICK-DIAG] MainWindow: handler entered fieldKey=drug",
            Ts + "OrderAssist: no target window matched this tick. Visible PioneerRx window titles: [Edit Rx - Testperson, Jordan]"
        };

        var tail = LogTailBuilder.BuildSafeTail(lines);
        var resultLines = tail.Split('\n');

        Assert.Equal(2, resultLines.Length);
        Assert.Contains("Timing: detect->render 100ms", resultLines[0]);
        Assert.Contains("[RIGHTCLICK-DIAG] MainWindow: handler entered fieldKey=drug", resultLines[1]);
        Assert.DoesNotContain("Testperson", tail);
        Assert.DoesNotContain("Jordan", tail);
    }

    [Fact]
    public void BuildSafeTailReturnsEmptyStringWhenNothingIsSafe()
    {
        var lines = new List<string> { "Patient: Jordan Q. Testperson", Ts + "OCR read" };
        Assert.Equal("", LogTailBuilder.BuildSafeTail(lines));
    }

    [Fact]
    public void BuildSafeTailReturnsEmptyStringForEmptyInput()
    {
        Assert.Equal("", LogTailBuilder.BuildSafeTail(Enumerable.Empty<string>()));
    }

    [Fact]
    public void BuildSafeTailCapsToMaxLines()
    {
        var lines = Enumerable.Range(0, 200)
            .Select(i => Ts + $"Timing: detect->render {i}ms (attach 0 + uia 0 [find 0 + read 0] + capture 0 [region 0 + hidewait 0 + blit 0] + ocr 0 + engine 0 + render 0)")
            .ToList();

        var tail = LogTailBuilder.BuildSafeTail(lines, maxLines: 10);
        var resultLines = tail.Split('\n');

        Assert.Equal(10, resultLines.Length);
        // Most recent lines survive (front gets dropped), not the earliest ones.
        Assert.Contains("199ms", resultLines[^1]);
        Assert.Contains("190ms", resultLines[0]);
    }

    [Fact]
    public void BuildSafeTailCapsToMaxBytesByTrimmingOldestFirst()
    {
        var longLine = Ts + "Timing: " + new string('x', 5000);
        var lines = new List<string> { longLine, longLine, longLine, longLine };

        var tail = LogTailBuilder.BuildSafeTail(lines, maxLines: 80, maxBytes: 8000);

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(tail) <= 8000);
        // Only the most recent line(s) should survive a tight byte cap.
        Assert.True(tail.Split('\n').Length < 4);
    }
}
