using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RxVerifyOverlay.Reports;

namespace RxVerifyOverlay.Tests.Reports;

/// <summary>
/// Test double for IPioneerReportDriver — no FlaUI/UIA/Windows involved.
/// Scripted per report key via <see cref="Results"/> (defaults to
/// Saved), records every call in <see cref="Calls"/> for sequencing
/// assertions, and can simulate FindMainWindow failing or a report
/// throwing/observing cancellation.
/// </summary>
public sealed class FakeReportDriver : IPioneerReportDriver
{
    public bool MainWindowFound { get; set; } = true;
    public List<string> Calls { get; } = new();
    public Dictionary<string, ReportRunResult> Results { get; } = new();

    /// <summary>Report key(s) that should throw OperationCanceledException instead of returning a result.</summary>
    public HashSet<string> ThrowCancelledFor { get; } = new();

    /// <summary>Report key(s) that should throw a plain Exception instead of returning a result.</summary>
    public Dictionary<string, Exception> ThrowFor { get; } = new();

    public bool FindMainWindow(Action<string> log)
    {
        Calls.Add("FindMainWindow");
        return MainWindowFound;
    }

    public Task<ReportRunResult> RunFinancialReport(ReportRunItem item, Action<string> log, CancellationToken ct) =>
        RunAny(item, ct);

    public Task<ReportRunResult> RunPaymentsExport(ReportRunItem item, Action<string> log, CancellationToken ct) =>
        RunAny(item, ct);

    private Task<ReportRunResult> RunAny(ReportRunItem item, CancellationToken ct)
    {
        Calls.Add(item.Entry.Key);

        if (ThrowCancelledFor.Contains(item.Entry.Key))
        {
            throw new OperationCanceledException(ct);
        }

        if (ThrowFor.TryGetValue(item.Entry.Key, out var ex))
        {
            throw ex;
        }

        if (Results.TryGetValue(item.Entry.Key, out var scripted))
        {
            return Task.FromResult(scripted);
        }

        return Task.FromResult(ReportRunResult.Saved(item.OutputFilePath, TimeSpan.FromSeconds(1)));
    }
}
