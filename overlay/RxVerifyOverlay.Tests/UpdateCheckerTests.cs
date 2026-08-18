using RxVerifyOverlay.Update;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for UpdateChecker (Update/UpdateChecker.cs) — the pure logic
/// behind the "Update ready" banner (branch
/// fix/rightclick-all-feedback-compact, task 4): parsing GitHub's
/// commits/main response body and deciding whether this build's own
/// (truncated) commit sha is stale against it.
/// </summary>
public class UpdateCheckerTests
{
    // ---- TryParseLatestCommitSha ------------------------------------

    [Fact]
    public void TryParseLatestCommitShaReadsTheShaField()
    {
        const string json = "{\"sha\":\"1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9a0b\",\"commit\":{\"message\":\"synthetic commit message\"}}";

        var sha = UpdateChecker.TryParseLatestCommitSha(json);

        Assert.Equal("1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9a0b", sha);
    }

    [Fact]
    public void TryParseLatestCommitShaReturnsNullForMissingShaField()
    {
        const string json = "{\"commit\":{\"message\":\"no sha at the top level\"}}";

        Assert.Null(UpdateChecker.TryParseLatestCommitSha(json));
    }

    [Fact]
    public void TryParseLatestCommitShaReturnsNullForMalformedJson()
    {
        Assert.Null(UpdateChecker.TryParseLatestCommitSha("not json at all"));
    }

    [Fact]
    public void TryParseLatestCommitShaReturnsNullForNullOrBlankInput()
    {
        Assert.Null(UpdateChecker.TryParseLatestCommitSha(null));
        Assert.Null(UpdateChecker.TryParseLatestCommitSha(""));
        Assert.Null(UpdateChecker.TryParseLatestCommitSha("   "));
    }

    [Fact]
    public void TryParseLatestCommitShaReturnsNullWhenShaIsNotAString()
    {
        const string json = "{\"sha\":12345}";

        Assert.Null(UpdateChecker.TryParseLatestCommitSha(json));
    }

    [Fact]
    public void TryParseLatestCommitShaReturnsNullWhenShaIsEmptyString()
    {
        const string json = "{\"sha\":\"\"}";

        Assert.Null(UpdateChecker.TryParseLatestCommitSha(json));
    }

    // ---- IsUpdateAvailable --------------------------------------------

    [Fact]
    public void NoUpdateWhenRemoteShaStartsWithTheLocalShortSha()
    {
        // AppDiagnostics.GetCommitSha() returns an 8-char short sha (or
        // shorter) — GitHub's API returns the full 40-char sha, so the
        // comparison must be a prefix match, not equality.
        var available = UpdateChecker.IsUpdateAvailable("2a95682d", "2a95682d1234567890abcdef1234567890abcdef");

        Assert.False(available);
    }

    [Fact]
    public void UpdateAvailableWhenRemoteShaDoesNotStartWithTheLocalShortSha()
    {
        var available = UpdateChecker.IsUpdateAvailable("2a95682d", "deadbeef1234567890abcdef1234567890abcdef");

        Assert.True(available);
    }

    [Fact]
    public void PrefixComparisonIsCaseInsensitive()
    {
        var available = UpdateChecker.IsUpdateAvailable("2A95682D", "2a95682d1234567890abcdef1234567890abcdef");

        Assert.False(available);
    }

    [Theory]
    [InlineData(null, "deadbeef1234567890abcdef1234567890abcdef")]
    [InlineData("", "deadbeef1234567890abcdef1234567890abcdef")]
    [InlineData("  ", "deadbeef1234567890abcdef1234567890abcdef")]
    [InlineData("2a95682d", null)]
    [InlineData("2a95682d", "")]
    [InlineData("2a95682d", "   ")]
    public void NoUpdateWhenEitherShaIsNullOrBlank(string? localSha, string? remoteSha)
    {
        Assert.False(UpdateChecker.IsUpdateAvailable(localSha, remoteSha));
    }

    [Fact]
    public void NoUpdateWhenLocalShaIsTheUnknownSentinel()
    {
        // AppDiagnostics.GetCommitSha()'s own "couldn't resolve" fallback —
        // never nag with a banner that can never be satisfied.
        var available = UpdateChecker.IsUpdateAvailable("unknown", "deadbeef1234567890abcdef1234567890abcdef");

        Assert.False(available);
    }

    [Fact]
    public void UnknownSentinelComparisonIsCaseInsensitive()
    {
        var available = UpdateChecker.IsUpdateAvailable("Unknown", "deadbeef1234567890abcdef1234567890abcdef");

        Assert.False(available);
    }
}
