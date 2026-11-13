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

    /// <summary>What <see cref="IsSearchDrawersOpen"/> answers (the pre-run check for the daily POS pass).</summary>
    public bool SearchDrawersOpen { get; set; } = true;

    /// <summary>How many times the pre-run Search Drawers check was asked (kept out of <see cref="Calls"/>).</summary>
    public int SearchDrawersChecks { get; private set; }
    public List<string> Calls { get; } = new();

    /// <summary>Every item handed to a Run*/TestDateEntry call, in order.</summary>
    public List<ReportRunItem> Items { get; } = new();

    /// <summary>Output file names (not keys) that should come back Failed.</summary>
    public Dictionary<string, ReportRunResult> ResultsByFileName { get; } = new();
    public Dictionary<string, ReportRunResult> Results { get; } = new();

    /// <summary>Report key(s) that should throw OperationCanceledException instead of returning a result.</summary>
    public HashSet<string> ThrowCancelledFor { get; } = new();

    /// <summary>Report key(s) that should throw a plain Exception instead of returning a result.</summary>
    public Dictionary<string, Exception> ThrowFor { get; } = new();

    public bool FindMainWindow(Action<string> log, CancellationToken ct)
    {
        Calls.Add("FindMainWindow");
        return MainWindowFound;
    }

    public bool IsSearchDrawersOpen(Action<string> log)
    {
        SearchDrawersChecks++;
        return SearchDrawersOpen;
    }

    public Task<ReportRunResult> RunFinancialReport(ReportRunItem item, Action<string> log, CancellationToken ct) =>
        RunAny(item, ct);

    public Task<ReportRunResult> RunPaymentsExport(ReportRunItem item, Action<string> log, CancellationToken ct) =>
        RunAny(item, ct);

    public Task<ReportRunResult> RunPosSummary(ReportRunItem item, Action<string> log, CancellationToken ct) =>
        RunAny(item, ct);

    public Task<ReportRunResult> TestDateEntry(ReportRunItem item, Action<string> log, CancellationToken ct) =>
        RunAny(item, ct);

    private Task<ReportRunResult> RunAny(ReportRunItem item, CancellationToken ct)
    {
        Calls.Add(item.Entry.Key);
        Items.Add(item);

        if (ResultsByFileName.TryGetValue(System.IO.Path.GetFileName(item.OutputFilePath), out var byFile))
        {
            return Task.FromResult(byFile);
        }

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
