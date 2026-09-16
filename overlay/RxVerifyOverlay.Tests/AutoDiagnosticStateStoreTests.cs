using System;
using System.IO;
using RxVerifyOverlay.Diagnostics;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for AutoDiagnosticStateStore (Diagnostics/
/// AutoDiagnosticStateStore.cs) — the auto-diagnostic feature's persisted
/// rate-limit bookkeeping. Every test passes an explicit temp-file path
/// (same testability pattern as Reporting/PendingReportsQueueTests.cs) so
/// nothing here ever touches the real %AppData%\RxVerifyOverlay\
/// auto-diagnostic-state.json. All Rx numbers below are synthetic.
/// </summary>
public class AutoDiagnosticStateStoreTests
{
    private static string TempStatePath() =>
        Path.Combine(Path.GetTempPath(), $"rxverify-auto-diagnostic-state-test-{Guid.NewGuid():N}.json");

    [Fact]
    public void LoadReturnsAFreshEmptyStateWhenNoFileExists()
    {
        var path = TempStatePath();

        var state = AutoDiagnosticStateStore.Load(path);

        Assert.Empty(state.LastReportedUtcByRx);
        Assert.Empty(state.RecentReportTimestampsUtc);
    }

    [Fact]
    public void SaveThenLoadRoundTripsExactly()
    {
        var path = TempStatePath();
        try
        {
            var state = new AutoDiagnosticRateLimitState();
            state.LastReportedUtcByRx["RX-1001"] = new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);
            state.RecentReportTimestampsUtc.Add(new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc));

            AutoDiagnosticStateStore.Save(state, path);
            var loaded = AutoDiagnosticStateStore.Load(path);

            Assert.Equal(state.LastReportedUtcByRx["RX-1001"], loaded.LastReportedUtcByRx["RX-1001"]);
            Assert.Single(loaded.RecentReportTimestampsUtc);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void LoadReturnsAFreshEmptyStateOnCorruptJson()
    {
        var path = TempStatePath();
        try
        {
            File.WriteAllText(path, "{ this is not valid json");

            var state = AutoDiagnosticStateStore.Load(path);

            Assert.Empty(state.LastReportedUtcByRx);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void SaveReturnsFalseWhenTheParentPathIsAFileNotADirectory()
    {
        var blockingFile = TempStatePath();
        File.WriteAllText(blockingFile, "not a directory");
        var path = Path.Combine(blockingFile, "auto-diagnostic-state.json");

        try
        {
            var result = AutoDiagnosticStateStore.Save(new AutoDiagnosticRateLimitState(), path);

            Assert.False(result);
        }
        finally
        {
            if (File.Exists(blockingFile)) File.Delete(blockingFile);
        }
    }
}
