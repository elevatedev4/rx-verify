using System;
using System.Threading;
using System.Threading.Tasks;

namespace RxVerifyOverlay.Reports;

/// <summary>
/// Terminal state ReportsCoordinator/ReportsWindow show per report — kept
/// to exactly the four states the GOAL brief names for the picker's
/// per-report status list ("Pending / Running / Saved &lt;path&gt; / Failed
/// &lt;reason&gt;"). A Stop request never gets its own status: a report that
/// was never started when Stop was pressed simply stays Pending (the
/// coordinator never touches it), and a report that WAS mid-run when
/// cancellation was observed is reported Failed with a "Stopped" reason
/// (see ReportsCoordinator.RunAsync).
/// </summary>
public enum ReportRunStatus
{
    Pending,
    Running,
    Saved,
    Failed
}

/// <summary>
/// What one call to IPioneerReportDriver's run methods reports back.
/// SavedPath is set only when Status is Saved; FailureReason only when
/// Status is Failed. Elapsed is wall-clock time for that single report,
/// logged by ReportsCoordinator's summary line.
/// </summary>
public sealed record ReportRunResult(ReportRunStatus Status, string? SavedPath, string? FailureReason, TimeSpan Elapsed)
{
    public static ReportRunResult Saved(string path, TimeSpan elapsed) => new(ReportRunStatus.Saved, path, null, elapsed);

    public static ReportRunResult Failed(string reason, TimeSpan elapsed) => new(ReportRunStatus.Failed, null, reason, elapsed);
}

/// <summary>
/// Everything ReportsCoordinator needs from Pioneer automation, behind an
/// interface so the coordinator (sequencing, one-failure-continues,
/// Stop/cancel behavior) is unit-testable with a fake — see
/// RxVerifyOverlay.Tests/Reports/FakeReportDriver.cs — without ever
/// touching FlaUI/UIA or a real PioneerRx window. PioneerReportDriver.cs
/// is the one real implementation.
/// </summary>
public interface IPioneerReportDriver
{
    /// <summary>
    /// Locates PioneerRx's main window (title/class discovery — NOT the
    /// Pre-Check/Edit/New-Rx window Uia/PioneerRxWindow.cs attaches to)
    /// and brings it to the foreground. Returns false (does not throw) if
    /// no PioneerRx main window is currently open — ReportsCoordinator
    /// treats that as an immediate failure for every selected report
    /// rather than attempting any input.
    ///
    /// Round 3 fix: <paramref name="log"/> added (was parameterless) so
    /// every candidate window this considers — and why it picked (or
    /// didn't pick) one — reaches the run log; the previous silent
    /// version picked a same-process helper window with no visible UI
    /// (pid, handle=0x0) with no way to diagnose it after the fact. See
    /// Reports/AutomationWindowSelection.cs MainWindowSelector/
    /// MainWindowCandidateLog for the pure ranking/formatting behind it.
    ///
    /// Review fix (PR #11, non-blocking): <paramref name="ct"/> added so
    /// the ~5s retry loop can be interrupted by Stop instead of always
    /// running to completion — same "returns false, does not throw"
    /// contract as before (a cancelled token makes this return false
    /// promptly, it does not surface as OperationCanceledException).
    /// </summary>
    bool FindMainWindow(Action<string> log, CancellationToken ct);

    /// <summary>
    /// Runs one "Analysis &gt; Financial Reports &gt; Run Financial Reports"
    /// row end-to-end: select the row, fill Begin/End (or the single As
    /// Of Date), View - F12, wait for the preview, export to PDF, Save
    /// As with the full <paramref name="item"/>.OutputFilePath, verify
    /// the file landed on disk, close the preview. Never throws for an
    /// ordinary automation failure (missed control, timeout, unexpected
    /// dialog) — those come back as ReportRunResult.Failed so
    /// ReportsCoordinator can continue with the next report;
    /// OperationCanceledException is the only exception callers should
    /// expect, and only when <paramref name="ct"/> was already cancelled.
    /// </summary>
    Task<ReportRunResult> RunFinancialReport(ReportRunItem item, Action<string> log, CancellationToken ct);

    /// <summary>
    /// Runs the Payments export: Third Party &gt; Payments &gt; Search tab,
    /// fill "Payment Confirmed Between" Begin/End, Search - F12, wait for
    /// the Results grid, export to Excel, Save As with the full
    /// <paramref name="item"/>.OutputFilePath, verify the file landed on
    /// disk. Same never-throws-for-automation-failure contract as
    /// RunFinancialReport.
    /// </summary>
    Task<ReportRunResult> RunPaymentsExport(ReportRunItem item, Action<string> log, CancellationToken ct);
}
