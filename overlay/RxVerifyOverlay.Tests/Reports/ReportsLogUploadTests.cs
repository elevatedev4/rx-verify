using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.Reporting;
using RxVerifyOverlay.Reports;
using Xunit;

namespace RxVerifyOverlay.Tests.Reports;

/// <summary>W-Q13: the Reports log goes to HQ as one message when a run stops. Synthetic text only; the HTTP sink is a fake handler.</summary>
public class ReportsLogUploadTests
{
    private const string Sink = "https://sink.invalid/api/rxverify-feedback";

    private sealed class FakeSink : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Received { get; } = new();
        public HttpStatusCode Status { get; set; } = HttpStatusCode.Created;
        public bool Throw { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Throw) throw new HttpRequestException("network down");
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Received.Add((request, body));
            return new HttpResponseMessage(Status);
        }
    }

    private static OverlaySettings WithKey(string key) => new() { RxVerifyReportKey = key };

    private static ReportRunItem Item() =>
        ReportRunPlan.Build(ReportCatalog.All[0], new DateTime(2026, 10, 1), new DateTime(2026, 10, 31), "out");

    // ---- ShouldSend ----

    [Fact]
    public void RunWithEverySavedReportDoesNotSend()
    {
        var outcomes = new List<ReportRunOutcome> { new(Item(), ReportRunStatus.Saved, "a.xlsx", null) };
        Assert.False(ReportsLogUpload.ShouldSend(outcomes));
        Assert.False(ReportsLogUpload.ShouldSend(new List<ReportRunOutcome>()));
    }

    [Theory]
    [InlineData(ReportRunStatus.Failed)]
    [InlineData(ReportRunStatus.Pending)]
    [InlineData(ReportRunStatus.Running)]
    public void RunWithAnyUnsavedReportSends(ReportRunStatus status)
    {
        var outcomes = new List<ReportRunOutcome>
        {
            new(Item(), ReportRunStatus.Saved, "a.xlsx", null),
            new(Item(), status, null, "stopped")
        };
        Assert.True(ReportsLogUpload.ShouldSend(outcomes));
        Assert.Equal("1 of 2 saved", ReportsLogUpload.RunCounts(outcomes));
    }

    [Fact]
    public void DailyRunSendsOnEveryKindOfStopButNotOnFullSuccess()
    {
        Assert.False(ReportsLogUpload.ShouldSend(new ReportsCoordinator.DailyRunSummary(4, 4, null, null, false)));
        Assert.True(ReportsLogUpload.ShouldSend(new ReportsCoordinator.DailyRunSummary(1, 4, null, null, true)));
        Assert.True(ReportsLogUpload.ShouldSend(new ReportsCoordinator.DailyRunSummary(0, 4, null, "Could not find the main PioneerRx window - is Pioneer open?", false)));
        Assert.True(ReportsLogUpload.ShouldSend(new ReportsCoordinator.DailyRunSummary(0, 4, null, null, false, NeedsSearchDrawers: true)));
        Assert.Equal("3 of 62 saved", ReportsLogUpload.DailyCounts(new ReportsCoordinator.DailyRunSummary(3, 62, null, null, true)));
    }

    [Fact]
    public void DateEntryTestSendsOnlyWhenItDidNotComplete()
    {
        Assert.False(ReportsLogUpload.ShouldSend(ReportRunResult.Saved("x", TimeSpan.Zero)));
        Assert.True(ReportsLogUpload.ShouldSend(ReportRunResult.Failed("no window", TimeSpan.Zero)));
    }

    // ---- BuildMessage ----

    [Fact]
    public void ShortLogIsHeaderThenEveryLineUnchanged()
    {
        var lines = new[] { "10:00:01 Rx Verify build abc", "10:00:02 Running Sales Summary..." };

        var message = ReportsLogUpload.BuildMessage("Reports log - Run stopped.", lines);

        Assert.Equal("Reports log - Run stopped.\n10:00:01 Rx Verify build abc\n10:00:02 Running Sales Summary...", message);
    }

    [Fact]
    public void LongLogKeepsTheNewestLinesAndFitsTheLimit()
    {
        var lines = Enumerable.Range(1, 600).Select(i => $"10:00:00 step {i:000} " + new string('x', 20)).ToList();

        var message = ReportsLogUpload.BuildMessage("Reports log - Run daily stopped (3 of 62 saved).", lines);

        Assert.True(message.Length <= ReportsLogUpload.MaxMessageChars);
        Assert.StartsWith("Reports log - Run daily stopped (3 of 62 saved).\n(", message);
        Assert.Contains("earlier line(s) not sent)", message);
        Assert.EndsWith("step 600 " + new string('x', 20), message);
        Assert.DoesNotContain("step 001 ", message);
        // What is kept is a contiguous tail.
        var kept = message.Split('\n').Skip(2).ToList();
        Assert.Equal(lines.Skip(lines.Count - kept.Count).ToList(), kept);
    }

    [Fact]
    public void OneHugeLineIsCutFromTheFrontNotDropped()
    {
        var lines = new[] { new string('a', 9000) + "END" };

        var message = ReportsLogUpload.BuildMessage("H", lines);

        Assert.True(message.Length <= ReportsLogUpload.MaxMessageChars);
        Assert.EndsWith("aaaEND", message);
    }

    [Fact]
    public void HeaderHasCountsAndBuildOnly()
    {
        Assert.Equal("Reports log - Run daily stopped (3 of 62 saved). Rx Verify build abc", ReportsLogUpload.BuildHeader("Run daily", "3 of 62 saved", "Rx Verify build abc"));
        Assert.Equal("Reports log - Test date entry stopped. Rx Verify build abc", ReportsLogUpload.BuildHeader("Test date entry", null, "Rx Verify build abc"));
    }

    // ---- SendAsync against a fake HTTP sink ----

    [Fact]
    public async Task SendPostsOneMessageWithTheReportKeyToTheFeedbackEndpoint()
    {
        var sink = new FakeSink();
        var submitter = new FeedbackSubmitter(WithKey("synthetic-key"), sink, Sink);

        var result = await ReportsLogUpload.SendAsync(submitter, true, "Reports log - Run stopped.\n10:00:01 line one", "abc12345");

        Assert.Equal("Log sent to HQ.", result);
        var (request, body) = Assert.Single(sink.Received);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(Sink, request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("synthetic-key", request.Headers.Authorization.Parameter);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("rx-verify", doc.RootElement.GetProperty("app").GetString());
        Assert.Equal("Reports log - Run stopped.\n10:00:01 line one", doc.RootElement.GetProperty("message").GetString());
        Assert.Equal("abc12345", doc.RootElement.GetProperty("commit").GetString());
    }

    [Fact]
    public async Task NoKeyMeansNothingIsSentAndTheResultSaysSo()
    {
        var sink = new FakeSink();
        var submitter = new FeedbackSubmitter(WithKey(""), sink, Sink);

        var result = await ReportsLogUpload.SendAsync(submitter, false, "m", null);

        Assert.Empty(sink.Received);
        Assert.Contains("no report key", result);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task HqErrorDoesNotThrowAndIsReportedInTheLog(HttpStatusCode status)
    {
        var sink = new FakeSink { Status = status };
        var submitter = new FeedbackSubmitter(WithKey("synthetic-key"), sink, Sink);

        var result = await ReportsLogUpload.SendAsync(submitter, true, "m", null);

        Assert.StartsWith("Log not sent to HQ", result);
        Assert.Single(sink.Received);
    }

    [Fact]
    public async Task NetworkFailureDoesNotThrow()
    {
        var sink = new FakeSink { Throw = true };
        var submitter = new FeedbackSubmitter(WithKey("synthetic-key"), sink, Sink);

        var result = await ReportsLogUpload.SendAsync(submitter, true, "m", null);

        Assert.StartsWith("Log not sent to HQ", result);
    }
}
