using System;

namespace RxVerifyOverlay.Update;

/// <summary>
/// Pure logic behind the "Update ready" banner (branch
/// fix/rightclick-all-feedback-compact, task 4; reworked on
/// feat/private-repo-ssh) — parsing `git ls-remote` output and deciding
/// whether the running build is stale against it. Split out from
/// Update/UpdateService.cs (the actual git process + 4-hour polling /
/// caching) the same "pure logic tested, transport plumbing isn't" way
/// Engine/EngineClient.cs splits ParseResponseLine from the process-spawning
/// transport around it — see RxVerifyOverlay.Tests/UpdateCheckerTests.cs.
/// </summary>
public static class UpdateChecker
{
    /// <summary>
    /// Parses the sha of refs/heads/main out of the stdout of
    /// `git ls-remote origin refs/heads/main` — one line per matching ref,
    /// "&lt;40-hex sha&gt;&lt;TAB&gt;refs/heads/main". Returns null for
    /// blank input, a line with a non-hex sha, or output with no
    /// refs/heads/main line at all (e.g. an error text) — same "never throw,
    /// return null on any unexpected shape" posture as
    /// EngineClient.TryParseHandshakeLine.
    /// </summary>
    public static string? TryParseLsRemoteSha(string? lsRemoteOutput)
    {
        if (string.IsNullOrWhiteSpace(lsRemoteOutput)) return null;

        foreach (var rawLine in lsRemoteOutput.Split('\n'))
        {
            var line = rawLine.Trim();
            var parts = line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) continue;
            if (!string.Equals(parts[1], "refs/heads/main", StringComparison.Ordinal)) continue;
            if (!IsHexSha(parts[0])) continue;
            return parts[0];
        }

        return null;
    }

    private static bool IsHexSha(string value)
    {
        if (value.Length < 40 || value.Length > 64) return false;
        foreach (var c in value)
        {
            var hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!hex) return false;
        }
        return true;
    }

    /// <summary>
    /// True when <paramref name="remoteCommitSha"/> (git's full 40-char sha
    /// for origin/main) does NOT start with <paramref name="localCommitSha"/>
    /// (this build's own AppDiagnostics.GetCommitSha() — truncated to 8
    /// chars, or shorter, or the literal "unknown" when unresolvable —
    /// see that method's own doc). A prefix comparison, not equality,
    /// because the two are different lengths by design; case-insensitive
    /// because git shas are conventionally lowercase hex but nothing
    /// guarantees either source normalizes case the same way.
    ///
    /// Deliberately conservative about FALSE POSITIVES (never nag when we
    /// can't actually tell): returns false — "no update" — whenever either
    /// value is null/blank, or localCommitSha is the literal "unknown"
    /// (AppDiagnostics' own "couldn't resolve" sentinel, e.g. a build that
    /// somehow shipped without a .git directory reachable from
    /// AppContext.BaseDirectory) — showing an "Update ready" banner that
    /// can never actually be satisfied (this build has no comparable
    /// commit to update FROM) would be a permanent, un-clearable false
    /// alarm, worse than not checking at all.
    /// </summary>
    public static bool IsUpdateAvailable(string? localCommitSha, string? remoteCommitSha)
    {
        if (string.IsNullOrWhiteSpace(localCommitSha) || string.IsNullOrWhiteSpace(remoteCommitSha)) return false;
        if (string.Equals(localCommitSha, "unknown", StringComparison.OrdinalIgnoreCase)) return false;

        return !remoteCommitSha.Trim().StartsWith(localCommitSha.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
