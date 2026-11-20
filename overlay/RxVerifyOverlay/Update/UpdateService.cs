using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace RxVerifyOverlay.Update;

/// <summary>
/// Background "is a newer build available" check (branch
/// fix/rightclick-all-feedback-compact, task 4; reworked on
/// feat/private-repo-ssh) — runs `git ls-remote origin refs/heads/main`
/// INSIDE this app's own checkout and compares the answer against this
/// build's own embedded commit via UpdateChecker.IsUpdateAvailable.
///
/// It used to GET api.github.com's anonymous commits/main endpoint, which
/// stops working once the GitHub repo is private. Asking git instead means
/// the check uses exactly the transport the checkout is configured for:
/// HTTPS while the repo is public, the read-only SSH deploy key (repo-local
/// core.sshCommand, see scripts/GitSsh.ps1) afterwards. GIT_TERMINAL_PROMPT=0
/// and GCM_INTERACTIVE=never guarantee git can never pop a credential
/// prompt in front of a pharmacist.
///
/// Called once at startup and every 4 hours by MainWindow.xaml.cs (see
/// CheckForUpdateIntervalMs there) — this class owns no timer of its own,
/// same "caller drives the schedule" split OrderAssistCoordinator uses for
/// its own OCR timer.
///
/// FAIL SOFT (unchanged hard requirement): git missing from PATH, no repo
/// found, no network, a rejected key, a timeout, or unparseable output NEVER
/// surfaces to the pharmacist and NEVER blocks startup — CheckAsync is
/// always called fire-and-forget (`_ = ...`) from MainWindow, and any
/// failure here just means the banner keeps whatever state it last had (see
/// LastKnownUpdateAvailable's own doc) rather than flickering to "no update"
/// on a transient hiccup.
///
/// Deliberately NOT unit tested itself (spawns a real git process, same
/// posture as Reporting/RxReportSubmitter.cs's HTTP) — the logic worth
/// testing (output->sha parsing, stale-vs-current comparison, repo
/// discovery) lives in the pure Update/UpdateChecker.cs and
/// Update/RepoLocator.cs this class just calls.
/// </summary>
public sealed class UpdateService
{
    /// <summary>How long one `git ls-remote` may take before it is killed (an SSH connect to an unreachable network can otherwise sit for minutes).</summary>
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// True if the most recently COMPLETED successful check found the
    /// remote ahead of this build. Starts false (never nag before the
    /// first check has ever completed). A failed check (no network, git
    /// error, timeout, unparseable output) leaves this exactly as it was —
    /// see class doc's FAIL SOFT section — which is the "cache last-checked
    /// sha so the banner does not flicker" behavior the branch brief asks
    /// for: a momentary hiccup 4 hours into a shift can never make an
    /// already-showing banner disappear, and can never make one appear
    /// before a real successful check has actually confirmed it.
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
            var repoRoot = RepoLocator.ResolveRepoRoot();
            if (repoRoot is null) return LastKnownUpdateAvailable;

            var output = await RunLsRemoteAsync(repoRoot).ConfigureAwait(false);
            var remoteSha = UpdateChecker.TryParseLsRemoteSha(output);
            if (remoteSha is null) return LastKnownUpdateAvailable;

            LastKnownUpdateAvailable = UpdateChecker.IsUpdateAvailable(localCommitSha, remoteSha);
        }
        catch
        {
            // git not installed/not on PATH, process start failure, etc —
            // fail soft, see class doc. LastKnownUpdateAvailable intentionally left untouched.
        }

        return LastKnownUpdateAvailable;
    }

    /// <summary>
    /// stdout of `git ls-remote origin refs/heads/main` run in
    /// <paramref name="repoRoot"/>, or null on a non-zero exit / timeout.
    /// The whole process tree is killed on timeout (git spawns ssh).
    /// </summary>
    private static async Task<string?> RunLsRemoteAsync(string repoRoot)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("ls-remote");
        psi.ArgumentList.Add("origin");
        psi.ArgumentList.Add("refs/heads/main");
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GCM_INTERACTIVE"] = "never";

        using var process = Process.Start(psi);
        if (process is null) return null;

        // Drain both pipes concurrently so a chatty git can never fill a pipe buffer and deadlock.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(GitTimeout);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return null;
        }

        await stderrTask.ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        return process.ExitCode == 0 ? stdout : null;
    }
}
