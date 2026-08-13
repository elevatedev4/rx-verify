using System;
using System.IO;
using RxVerifyOverlay.Reporting;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for PendingReportsQueue (Reporting/PendingReportsQueue.cs) —
/// the store-and-forward JSONL file behind "Report error…"'s fail-soft
/// behavior when HQ's endpoint is missing/unreachable (see
/// Reporting/RxReportSubmitter.cs). Every test passes an explicit
/// temp-file path (the same "optional override param, defaults to the
/// real path" testability pattern as Models/OverlaySettings.cs
/// ResolveDefaultCliPath) so nothing here ever touches the real
/// %AppData%\RxVerifyOverlay\pending-reports.jsonl. All payload values are
/// synthetic.
/// </summary>
public class PendingReportsQueueTests
{
    private static string TempQueuePath() =>
        Path.Combine(Path.GetTempPath(), $"rxverify-pending-reports-test-{Guid.NewGuid():N}.jsonl");

    private static RxReportPayload MakePayload(string field = "quantity", string correction = "should be 30") => new()
    {
        App = "rx-verify",
        EngineBuild = "abc123 2026-08-13T00:00:00Z",
        Commit = "deadbee",
        Field = field,
        Source = "30",
        Entered = "60",
        Status = "red",
        ReasonCode = "qty_mismatch",
        Explanation = "Quantity mismatch",
        Correction = correction,
        CreatedAt = new DateTime(2026, 8, 13, 12, 0, 0, DateTimeKind.Utc)
    };

    [Fact]
    public void DequeueAllReturnsEmptyListWhenNoQueueFileExists()
    {
        var path = TempQueuePath();

        var result = PendingReportsQueue.DequeueAll(path);

        Assert.Empty(result);
    }

    [Fact]
    public void EnqueueThenDequeueAllRoundTripsAllFieldsExactly()
    {
        var path = TempQueuePath();
        try
        {
            var payload = MakePayload();

            PendingReportsQueue.Enqueue(payload, path);
            var result = PendingReportsQueue.DequeueAll(path);

            var dequeued = Assert.Single(result);
            Assert.Equal(payload.App, dequeued.App);
            Assert.Equal(payload.EngineBuild, dequeued.EngineBuild);
            Assert.Equal(payload.Commit, dequeued.Commit);
            Assert.Equal(payload.Field, dequeued.Field);
            Assert.Equal(payload.Source, dequeued.Source);
            Assert.Equal(payload.Entered, dequeued.Entered);
            Assert.Equal(payload.Status, dequeued.Status);
            Assert.Equal(payload.ReasonCode, dequeued.ReasonCode);
            Assert.Equal(payload.Explanation, dequeued.Explanation);
            Assert.Equal(payload.Correction, dequeued.Correction);
            Assert.Equal(payload.CreatedAt, dequeued.CreatedAt);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void MultipleEnqueuesAppendAsSeparateLinesAndAllComeBack()
    {
        var path = TempQueuePath();
        try
        {
            PendingReportsQueue.Enqueue(MakePayload(field: "quantity"), path);
            PendingReportsQueue.Enqueue(MakePayload(field: "refills"), path);
            PendingReportsQueue.Enqueue(MakePayload(field: "drug"), path);

            var result = PendingReportsQueue.DequeueAll(path);

            Assert.Equal(3, result.Count);
            Assert.Contains(result, r => r.Field == "quantity");
            Assert.Contains(result, r => r.Field == "refills");
            Assert.Contains(result, r => r.Field == "drug");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void DequeueAllDeletesTheFileSoNothingIsDoubleSubmitted()
    {
        var path = TempQueuePath();
        try
        {
            PendingReportsQueue.Enqueue(MakePayload(), path);

            var first = PendingReportsQueue.DequeueAll(path);
            var second = PendingReportsQueue.DequeueAll(path);

            Assert.Single(first);
            Assert.Empty(second);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void OneCorruptLineDoesNotLoseTheOtherQueuedReports()
    {
        var path = TempQueuePath();
        try
        {
            PendingReportsQueue.Enqueue(MakePayload(field: "quantity"), path);
            File.AppendAllText(path, "{ this is not valid json" + Environment.NewLine);
            PendingReportsQueue.Enqueue(MakePayload(field: "refills"), path);

            var result = PendingReportsQueue.DequeueAll(path);

            Assert.Equal(2, result.Count);
            Assert.Contains(result, r => r.Field == "quantity");
            Assert.Contains(result, r => r.Field == "refills");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
