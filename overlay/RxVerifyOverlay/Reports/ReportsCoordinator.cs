using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

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

        if (!_driver.FindMainWindow())
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
                result = item.Entry.ParameterKind == ReportParameterKind.PaymentsSearch
                    ? await _driver.RunPaymentsExport(item, log, ct).ConfigureAwait(false)
                    : await _driver.RunFinancialReport(item, log, ct).ConfigureAwait(false);
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

    private void RaiseProgress(ReportRunOutcome outcome) => ProgressChanged?.Invoke(this, new ReportProgressEventArgs(outcome));
}
