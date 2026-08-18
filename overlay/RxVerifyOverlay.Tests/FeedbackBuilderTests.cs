using System;
using RxVerifyOverlay.Reporting;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for FeedbackBuilder (Reporting/FeedbackPayload.cs) — the
/// pure function behind Integrated/FeedbackWindow.xaml.cs's Send button
/// (branch fix/rightclick-all-feedback-compact, task 2). Mirrors
/// RxReportBuilderTests' style/coverage for the much simpler payload shape.
/// All values below are synthetic.
/// </summary>
public class FeedbackBuilderTests
{
    private static readonly DateTime CreatedAt = new(2026, 8, 18, 9, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void BuildCarriesEveryFieldThrough()
    {
        var payload = FeedbackBuilder.Build("SYNTHETIC feedback: the box for quantity is misaligned", "abc123 2026-08-18T00:00:00Z", "deadbee", CreatedAt);

        Assert.Equal("rx-verify", payload.App);
        Assert.Equal("SYNTHETIC feedback: the box for quantity is misaligned", payload.Message);
        Assert.Equal("abc123 2026-08-18T00:00:00Z", payload.EngineBuild);
        Assert.Equal("deadbee", payload.Commit);
        Assert.Equal(CreatedAt, payload.CreatedAt);
    }

    [Fact]
    public void NullEngineBuildAndCommitPassThroughAsNull()
    {
        var payload = FeedbackBuilder.Build("SYNTHETIC message", engineBuild: null, commit: null, CreatedAt);

        Assert.Null(payload.EngineBuild);
        Assert.Null(payload.Commit);
    }

    [Fact]
    public void EmptyMessageBecomesEmptyStringNotNull()
    {
        var payload = FeedbackBuilder.Build("", null, null, CreatedAt);

        Assert.Equal("", payload.Message);
    }

    [Fact]
    public void NullMessageIsNormalizedToEmptyString()
    {
        var payload = FeedbackBuilder.Build(null!, null, null, CreatedAt);

        Assert.Equal("", payload.Message);
    }
}
