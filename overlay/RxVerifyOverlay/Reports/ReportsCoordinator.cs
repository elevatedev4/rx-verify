using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RxVerifyOverlay.Update;

namespace RxVerifyOverlay.Reports;

/// <summary>Snapshot of one report's outcome, returned in order by ReportsCoordinator.RunAsync and also broadcast live via ProgressChanged as each report starts/finishes.</summary>
public sealed record ReportRunOutcome(ReportRunItem Item, ReportRunStatus Status, string? SavedPath, string? Detail);

public sealed class ReportProgressEventArgs : EventArgs
{
    public ReportRunOutcome Outcome { get; }
    public ReportProgressEventArgs(ReportRunOutcome outcome) => Outcome = outcome;
}

/// <summary>
/// Runs a batch of ReportRunItems sequentially against an
/// IPioneerReportDriver, on a background Task with cancellation — the
/// pure sequencing/error-handling policy the GOAL brief asks for,
/// deliberately kept free of WPF/ReportsWindow so it's unit-testable
/// with a fake driver (see RxVerifyOverlay.Tests/Reports/
/// ReportsCoordinatorTests.cs: success, one-failure-continues, and
/// cancel-stops).
///
/// POLICY:
///   - If IPioneerReportDriver.FindMainWindow() fails up front, every
///     selected report is reported Failed immediately (no per-report
///     input is ever attempted) — Pioneer isn't open/attachable, so
///     nothing downstream could succeed either.
///   - Reports run ONE AT A TIME, in the order given. A report that
///     throws or returns Failed does NOT stop the batch — the next
///     report still runs (GOAL brief step 6: "sequentially ... updates
///     the window rows").
///   - A CancellationToken already cancelled (or cancelled while a
///     report is running, surfaced as OperationCanceledException from
///     the driver) stops the batch: the in-flight report is recorded
///     Failed with a "Stopped" detail, and every report after it is left
///     untouched — its ReportRunOutcome keeps ReportRunStatus.Pending,
///     matching the picker's own initial row state (see
///     IPioneerReportDriver.cs ReportRunStatus doc).
/// </summary>
public sealed class ReportsCoordinator
{
    private readonly IPioneerReportDriver _driver;
    private readonly Action<string> _appLog;
    private readonly Action<string, string, string> _runLog;

    public event EventHandler<ReportProgressEventArgs>? ProgressChanged;

    /// <param name="driver">Real PioneerReportDriver or a test fake.</param>
    /// <param name="appLog">Defaults to ReportsLog.Append; overridable so tests never touch the filesystem.</param>
    /// <param name="runLog">(outputFolder, runLogFileName, line) — defaults to ReportsLog.AppendToRunLog; overridable for the same reason.</param>
    public ReportsCoordinator(IPioneerReportDriver driver, Action<string>? appLog = null, Action<string, string, string>? runLog = null)
    {
        _driver = driver;
        _appLog = appLog ?? ReportsLog.Append;
        _runLog = runLog ?? ((folder, fileName, line) => ReportsLog.AppendToRunLog(folder, fileName, line));
    }

    /// <summary>Same "run-&lt;timestamp&gt;.log" file name for every line written during one RunAsync call.</summary>
    public static string BuildRunLogFileName(DateTime startedAt) => $"run-{startedAt:yyyyMMdd-HHmmss}.log";

    public async Task<IReadOnlyList<ReportRunOutcome>> RunAsync(IReadOnlyList<ReportRunItem> items, Action<string> log, CancellationToken ct)
    {
        var outcomes = new List<ReportRunOutcome>(items.Count);
        foreach (var item in items)
        {
            outcomes.Add(new ReportRunOutcome(item, ReportRunStatus.Pending, null, null));
        }

        if (items.Count == 0) return outcomes;

        var runLogFileName = BuildRunLogFileName(DateTime.Now);
        var runLogFolder = items[0].OutputFolder;

        void WriteLine(string text)
        {
            log(text);
            _appLog(text);
            _runLog(runLogFolder, runLogFileName, text);
        }

        // W-T92 round 3 (GOAL brief step 1): first line of every run log,
        // so a run's own log file always says which build actually
        // produced it - see Update/BuildInfo.cs's own doc for why.
        WriteLine(BuildInfo.Summary);

        if (!_driver.FindMainWindow(WriteLine, ct))
        {
            WriteLine("Could not find the main PioneerRx window - is Pioneer open?");
            for (var i = 0; i < items.Count; i++)
            {
                outcomes[i] = outcomes[i] with { Status = ReportRunStatus.Failed, Detail = "PioneerRx main window not found" };
                RaiseProgress(outcomes[i]);
            }
            return outcomes;
        }

        for (var i = 0; i < items.Count; i++)
        {
            if (ct.IsCancellationRequested)
            {
                WriteLine("Stopped - remaining reports were not run.");
                break;
            }

            var item = items[i];
            RaiseProgress(outcomes[i] = outcomes[i] with { Status = ReportRunStatus.Running });
            WriteLine($"Running {item.Entry.DisplayName}...");

            ReportRunResult result;
            try
            {
                // Round 2 fix ("make the next run self-diagnosing"): the
                // driver's own step-by-step log() calls (ribbon navigation
                // attempts, diagnostic dumps on a lookup failure, etc.)
                // used to go ONLY to the UI log box (the raw `log`
                // parameter) - never into ReportsLog.Append or this run's
                // run-<timestamp>.log file, so they were lost the moment
                // the window closed. Passing WriteLine instead fans every
                // one of those lines out to all three destinations, same
                // as this method's own "Running .../saved.../failed..."
                // lines already did.
                result = item.Entry.ParameterKind == ReportParameterKind.PaymentsSearch
                    ? await _driver.RunPaymentsExport(item, WriteLine, ct).ConfigureAwait(false)
                    : await _driver.RunFinancialReport(item, WriteLine, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                outcomes[i] = outcomes[i] with { Status = ReportRunStatus.Failed, Detail = "Stopped" };
                RaiseProgress(outcomes[i]);
                WriteLine($"{item.Entry.DisplayName}: stopped.");
                break;
            }
            catch (Exception ex)
            {
                result = ReportRunResult.Failed(ex.Message, TimeSpan.Zero);
                outcomes[i] = outcomes[i] with { Status = result.Status, SavedPath = result.SavedPath, Detail = result.FailureReason };
                RaiseProgress(outcomes[i]);
                WriteLine($"{item.Entry.DisplayName}: failed - {ex.Message}");
                continue;
            }

            var detail = result.Status == ReportRunStatus.Saved ? result.SavedPath : result.FailureReason;
            outcomes[i] = outcomes[i] with { Status = result.Status, SavedPath = result.SavedPath, Detail = detail };
            RaiseProgress(outcomes[i]);

            WriteLine(result.Status == ReportRunStatus.Saved
                ? $"{item.Entry.DisplayName}: saved to {result.SavedPath} ({result.Elapsed.TotalSeconds:0.0}s)"
                : $"{item.Entry.DisplayName}: failed - {result.FailureReason}");
        }

        return outcomes;
    }

    /// <summary>Result of <see cref="RunDailyAsync"/>.</summary>
    public sealed record DailyRunSummary(int Saved, int Total, DailyReportStep? FailedStep, string? FailureReason, bool Stopped, bool NeedsSearchDrawers = false);

    /// <summary>
    /// "Daily Sales + POS": runs each step of DailySalesPlan.Build in order
    /// (macro order: all POS days, then all Sales days). A Sales step is
    /// the existing, verified RunFinancialReport flow for the Sales Summary
    /// row with Begin = End = that day and the macro's file name
    /// (Sales_mm-dd-yy); Save As is the strict Win32 path, and a duplicate
    /// of our exact target is overwritten. The FIRST failure stops the run:
    /// no later day is attempted (so no key goes to a screen that may still
    /// have a stray window on top), and the log names the day and report.
    /// A POS step is the macro's "Search Drawers" flow (RunPosSummary): the
    /// driver verifies that screen before the first key and stops with a
    /// plain message if it is not open.
    /// </summary>
    public async Task<DailyRunSummary> RunDailyAsync(IReadOnlyList<DailyReportStep> steps, Action<string> log, CancellationToken ct)
    {
        var refusal = DailySalesPlan.CheckRunnable(steps);
        if (refusal is not null)
        {
            _appLog(refusal);
            log(refusal);
            return new DailyRunSummary(0, steps.Count, null, refusal, false);
        }

        var runLogFileName = BuildRunLogFileName(DateTime.Now);
        var runLogFolder = steps[0].OutputFolder;

        void WriteLine(string text)
        {
            log(text);
            _appLog(text);
            _runLog(runLogFolder, runLogFileName, text);
        }

        WriteLine(BuildInfo.Summary);
        WriteLine($"Daily run: {steps.Count} report(s), {DailySalesPlan.DateText(steps[0].Day)} onward, saving to the chosen folder.");

        if (!_driver.FindMainWindow(WriteLine, ct))
        {
            const string notFound = "Could not find the main PioneerRx window - is Pioneer open?";
            WriteLine(notFound);
            return new DailyRunSummary(0, steps.Count, null, notFound, false);
        }

        // W-T98: POS Summary starts on Pioneer's Search Drawers screen. Check that BEFORE
        // anything is sent; if it is not open this is a request to the user (NeedsSearchDrawers,
        // shown as a dialog by the Reports window), not a failure and not a counted day.
        if (steps.Any(s => s.Kind == DailyReportKind.Pos) && !_driver.IsSearchDrawersOpen(WriteLine))
        {
            WriteLine(PosSummaryPlan.OpenSearchDrawersFirstLogLine);
            return new DailyRunSummary(0, steps.Count, null, null, false, NeedsSearchDrawers: true);
        }

        // Macro: <date><ALT>e<date> - Alt+e reaches the End field (Tab only as the driver's fallback).
        var salesEntry = ReportCatalog.FindByKey(ReportCatalog.SalesSummaryKey)! with { EndFieldAccelerator = DailySalesPlan.EndFieldAccelerator };
        var posEntry = ReportCatalog.FindByKey(ReportCatalog.PosDailyKey)!;
        var saved = 0;

        foreach (var step in steps)
        {
            if (ct.IsCancellationRequested)
            {
                WriteLine($"Stopped before {DailySalesPlan.DescribeStep(step)} - the rest was not run.");
                return new DailyRunSummary(saved, steps.Count, null, null, true);
            }

            WriteLine($"{DailySalesPlan.DescribeStep(step)}: running...");

            ReportRunResult result;
            try
            {
                result = step.Kind == DailyReportKind.Pos
                    ? await _driver.RunPosSummary(new ReportRunItem(posEntry, step.Day, step.Day, step.OutputFolder, step.OutputFilePath), WriteLine, ct).ConfigureAwait(false)
                    : await _driver.RunFinancialReport(new ReportRunItem(salesEntry, step.Day, step.Day, step.OutputFolder, step.OutputFilePath), WriteLine, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                WriteLine($"{DailySalesPlan.DescribeStep(step)}: stopped - the rest was not run.");
                return new DailyRunSummary(saved, steps.Count, null, null, true);
            }
            catch (Exception ex)
            {
                result = ReportRunResult.Failed(ex.Message, TimeSpan.Zero);
            }

            if (result.Status == ReportRunStatus.Saved)
            {
                saved++;
                WriteLine($"{DailySalesPlan.DescribeStep(step)}: OK - saved {Path.GetFileName(step.OutputFilePath)} ({result.Elapsed.TotalSeconds:0.0}s).");
                continue;
            }

            var reason = result.FailureReason ?? "unknown failure";
            WriteLine($"{DailySalesPlan.DescribeStep(step)}: FAILED - {reason}");
            WriteLine($"Daily run stopped at {DailySalesPlan.DisplayName(step.Kind)} for {DailySalesPlan.DateText(step.Day)}: {saved} of {steps.Count} saved; the remaining {steps.Count - saved - 1} were not run.");
            return new DailyRunSummary(saved, steps.Count, step, reason, false);
        }

        WriteLine($"Daily run finished: {saved} of {steps.Count} saved.");
        return new DailyRunSummary(saved, steps.Count, null, null, false);
    }

    /// <summary>
    /// "Test date entry" button (W-T92 round 3, GOAL brief step 3): the
    /// same FindMainWindow-then-drive-one-thing shape as RunAsync, but for
    /// exactly one item and IPioneerReportDriver.TestDateEntry instead of
    /// RunFinancialReport/RunPaymentsExport - never touches the picker's
    /// row-status list (ReportsWindow doesn't pass this through
    /// ProgressChanged), only the log.
    /// </summary>
    public async Task<ReportRunResult> TestDateEntryAsync(ReportRunItem item, Action<string> log, CancellationToken ct)
    {
        var runLogFileName = BuildRunLogFileName(DateTime.Now);
        var runLogFolder = item.OutputFolder;

        void WriteLine(string text)
        {
            log(text);
            _appLog(text);
            _runLog(runLogFolder, runLogFileName, text);
        }

        WriteLine($"Test date entry - {BuildInfo.Summary}");

        if (!_driver.FindMainWindow(WriteLine, ct))
        {
            WriteLine("Could not find the main PioneerRx window - is Pioneer open?");
            return ReportRunResult.Failed("PioneerRx main window not found", TimeSpan.Zero);
        }

        try
        {
            var result = await _driver.TestDateEntry(item, WriteLine, ct).ConfigureAwait(false);
            WriteLine(result.Status == ReportRunStatus.Saved
                ? "Test date entry: done - check the Begin/End lines above."
                : $"Test date entry: failed - {result.FailureReason}");
            return result;
        }
        catch (OperationCanceledException)
        {
            WriteLine("Test date entry: stopped.");
            return ReportRunResult.Failed("Stopped", TimeSpan.Zero);
        }
    }

    private void RaiseProgress(ReportRunOutcome outcome) => ProgressChanged?.Invoke(this, new ReportProgressEventArgs(outcome));
}
