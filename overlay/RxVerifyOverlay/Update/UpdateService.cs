using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace RxVerifyOverlay.Update;

/// <summary>
/// Background "is a newer build available" check (branch
/// fix/rightclick-all-feedback-compact, task 4) — GETs GitHub's public,
/// unauthenticated commits/main endpoint for elevatedev4/rx-verify and
/// compares against this build's own embedded commit via
/// UpdateChecker.IsUpdateAvailable. Called once at startup and every 4
/// hours by MainWindow.xaml.cs (see CheckForUpdateIntervalMs there) —
/// this class owns no timer of its own, same "caller drives the schedule"
/// split OrderAssistCoordinator uses for its own OCR timer.
///
/// FAIL SOFT (branch brief, task 4, hard requirement): a DNS/timeout/
/// network error or non-2xx response NEVER surfaces to the pharmacist and
/// NEVER blocks startup — CheckAsync is always called fire-and-forget
/// (`_ = ...`) from MainWindow, and any failure here just means the
/// banner keeps whatever state it last had (see LastKnownUpdateAvailable's
/// own doc) rather than flickering to "no update" on a transient hiccup.
///
/// Deliberately NOT unit tested itself (no HTTP-mocking infrastructure in
/// this test project — same posture as Reporting/RxReportSubmitter.cs and
/// Reporting/FeedbackSubmitter.cs) — the logic worth testing
/// (JSON-&gt;sha parsing, stale-vs-current comparison) already lives in the
/// pure Update/UpdateChecker.cs this class just calls.
/// </summary>
public sealed class UpdateService
{
    /// <summary>Shared across every instance — same "one long-lived HttpClient" reasoning as Reporting/RxReportSubmitter.cs's own Http field.</summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    /// <summary>
    /// GitHub's REST API requires SOME User-Agent header on every request
    /// (an unauthenticated request with none is rejected with 403) — this
    /// is that header's value, not a secret. No auth token is sent at all:
    /// this is the same public, unauthenticated read the bootstrap
    /// one-liner itself already relies on (raw.githubusercontent.com), so
    /// there is nothing here for a pharmacy workstation to leak.
    /// </summary>
    private const string UserAgent = "rx-verify-overlay";

    private const string LatestCommitUrl = "https://api.github.com/repos/elevatedev4/rx-verify/commits/main";

    /// <summary>
    /// True if the most recently COMPLETED successful check found the
    /// remote ahead of this build. Starts false (never nag before the
    /// first check has ever completed). A failed check (network error,
    /// non-2xx, unparseable body) leaves this exactly as it was — see
    /// class doc's FAIL SOFT section — which is the "cache last-checked
    /// sha so the banner does not flicker" behavior the branch brief asks
    /// for: a momentary GitHub API hiccup 4 hours into a shift can never
    /// make an already-showing banner disappear, and can never make one
    /// appear before a real successful check has actually confirmed it.
    /// </summary>
    public bool LastKnownUpdateAvailable { get; private set; }

    /// <summary>
    /// Runs one check. Never throws — every failure mode is caught and
    /// simply leaves LastKnownUpdateAvailable unchanged (see its own doc).
    /// Returns the (possibly unchanged) LastKnownUpdateAvailable purely as
    /// a convenience for the caller; the property is the source of truth
    /// either way, so a caller that only cares about "read the current
    /// banner state" never needs to await a fresh call at all.
    /// </summary>
    public async Task<bool> CheckAsync(string localCommitSha)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestCommitUrl);
            request.Headers.UserAgent.ParseAdd(UserAgent);

            using var response = await Http.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return LastKnownUpdateAvailable;

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var remoteSha = UpdateChecker.TryParseLatestCommitSha(json);
            if (remoteSha is null) return LastKnownUpdateAvailable;

            LastKnownUpdateAvailable = UpdateChecker.IsUpdateAvailable(localCommitSha, remoteSha);
        }
        catch
        {
            // DNS/timeout/network error/etc — fail soft, see class doc.
            // LastKnownUpdateAvailable intentionally left untouched.
        }

        return LastKnownUpdateAvailable;
    }
}
