using RxVerifyOverlay.Update;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for UpdateChecker (Update/UpdateChecker.cs) — the pure logic
/// behind the "Update ready" banner: parsing `git ls-remote origin
/// refs/heads/main` output (branch feat/private-repo-ssh replaced the old
/// api.github.com JSON poll) and deciding whether this build's own
/// (truncated) commit sha is stale against it. Shas below are synthetic.
/// </summary>
public class UpdateCheckerTests
{
    private const string Sha = "1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9a0b";

    // ---- TryParseLsRemoteSha ------------------------------------------

    [Fact]
    public void TryParseLsRemoteShaReadsTheMainLine()
    {
        Assert.Equal(Sha, UpdateChecker.TryParseLsRemoteSha(Sha + "\trefs/heads/main\n"));
    }

    [Fact]
    public void TryParseLsRemoteShaToleratesCrLfAndSpaceSeparators()
    {
        Assert.Equal(Sha, UpdateChecker.TryParseLsRemoteSha(Sha + " refs/heads/main\r\n"));
    }

    [Fact]
    public void TryParseLsRemoteShaPicksTheMainLineAmongSeveral()
    {
        const string other = "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef";
        var output = other + "\trefs/heads/main-old\n" + other + "\trefs/tags/main\n" + Sha + "\trefs/heads/main\n";

        Assert.Equal(Sha, UpdateChecker.TryParseLsRemoteSha(output));
    }

    [Fact]
    public void TryParseLsRemoteShaReturnsNullWhenThereIsNoMainRef()
    {
        Assert.Null(UpdateChecker.TryParseLsRemoteSha(Sha + "\trefs/heads/develop\n"));
    }

    [Fact]
    public void TryParseLsRemoteShaReturnsNullForErrorTextOrBadSha()
    {
        Assert.Null(UpdateChecker.TryParseLsRemoteSha("fatal: Could not read from remote repository."));
        Assert.Null(UpdateChecker.TryParseLsRemoteSha("nothex-nothex-nothex-nothex-nothex-nothex\trefs/heads/main"));
        Assert.Null(UpdateChecker.TryParseLsRemoteSha("abc123\trefs/heads/main"));
    }

    [Fact]
    public void TryParseLsRemoteShaReturnsNullForNullOrBlankInput()
    {
        Assert.Null(UpdateChecker.TryParseLsRemoteSha(null));
        Assert.Null(UpdateChecker.TryParseLsRemoteSha(""));
        Assert.Null(UpdateChecker.TryParseLsRemoteSha("   "));
    }

    // ---- IsUpdateAvailable --------------------------------------------

    [Fact]
    public void NoUpdateWhenRemoteShaStartsWithTheLocalShortSha()
    {
        // AppDiagnostics.GetCommitSha() returns an 8-char short sha (or
        // shorter) — git returns the full 40-char sha, so the
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
