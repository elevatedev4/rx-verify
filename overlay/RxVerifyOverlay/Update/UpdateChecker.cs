using System;
using System.Text.Json;

namespace RxVerifyOverlay.Update;

/// <summary>
/// Pure logic behind the "Update ready" banner (branch
/// fix/rightclick-all-feedback-compact, task 4) — parsing GitHub's
/// commits/main response and deciding whether the running build is stale
/// against it. Split out from Update/UpdateService.cs (the actual HTTP
/// fetch + 4-hour polling / caching) the same "pure logic tested, transport
/// plumbing isn't" way Engine/EngineClient.cs splits ParseResponseLine from
/// the process-spawning transport around it — see
/// RxVerifyOverlay.Tests/UpdateCheckerTests.cs.
/// </summary>
public static class UpdateChecker
{
    /// <summary>
    /// Parses the "sha" field out of GitHub's
    /// https://api.github.com/repos/elevatedev4/rx-verify/commits/main
    /// response body (a much larger object — commit message, author,
    /// files, etc. — only "sha" is ever read). Returns null for anything
    /// that doesn't parse as JSON or doesn't have a non-empty string "sha"
    /// at the top level — same "never throw, return null on any
    /// unexpected shape" posture as EngineClient.TryParseHandshakeLine.
    /// </summary>
    public static string? TryParseLatestCommitSha(string? responseJson)
    {
        if (string.IsNullOrWhiteSpace(responseJson)) return null;

        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            if (!doc.RootElement.TryGetProperty("sha", out var shaEl) || shaEl.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var sha = shaEl.GetString();
            return string.IsNullOrWhiteSpace(sha) ? null : sha;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when <paramref name="remoteCommitSha"/> (GitHub's full 40-char
    /// sha for origin/main) does NOT start with <paramref name="localCommitSha"/>
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
