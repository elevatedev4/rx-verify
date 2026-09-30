using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace RxVerifyOverlay.VelopackIntegration;

/// <summary>
/// Velopack self-update check/download, wired as a fire-and-forget call
/// from App.xaml.cs.OnStartup AFTER the existing startup work (see that
/// call site) — never awaited there, never blocking launch, every
/// exception caught here and logged rather than propagated. Mirrors
/// vaccine-assist's Velopack/VelopackUpdater.cs.
///
/// LOAD-BEARING INVARIANT: <see cref="CheckAndApplyAsync"/> returns
/// immediately unless the running copy was actually installed by
/// Setup.exe/vpk (see <paramref name="isInstalledOverride"/> below and
/// <see cref="IsInstalled"/>). That is false for BOTH:
///   - a plain `dotnet build`/F5 run from a checkout (no Velopack install
///     metadata on disk), and
///   - the EXISTING bootstrap-fresh.ps1 + UpdateLauncher re-run flow,
///     which is just a `dotnet build` output launched directly — never
///     run through Setup.exe/vpk.
/// This is what keeps that whole existing path 100% Velopack-free with no
/// build-time #if/#define branching needed: the exact same binary quietly
/// no-ops here when it wasn't installed the new way. Program.cs's
/// unconditional VelopackApp.Build().Run() call is separately safe for the
/// same reason (Velopack detects "not installed" internally there too) —
/// this class's own gate is what stops THIS code from ever touching S3/the
/// network on that path.
///
/// NOTE ON THE GATE'S NAME: <c>VelopackRuntimeInfo.IsInstalled</c> does
/// not exist in the Velopack package pinned here (1.2.161 — verified
/// against github.com/velopack/velopack at that tag —
/// VelopackRuntimeInfo has no IsInstalled property). The equivalent,
/// documented member is the INSTANCE property
/// <c>UpdateManager.IsInstalled</c> (<c>Locator.CurrentlyInstalledVersion
/// != null</c>), used here instead — same invariant, correct API.
/// </summary>
public static class VelopackUpdater
{
    private const string ReleaseSourceFileName = "Velopack/release-source.json";

    /// <summary>
    /// Checks for and downloads (but does not apply) an update. Applying
    /// happens automatically on the NEXT launch via Program.cs's
    /// VelopackApp.Build().Run() call (its default SetAutoApplyOnStartup
    /// behavior) — there is no "apply now" call in this method. A future
    /// "Restart now" affordance can call
    /// <see cref="ApplyDownloadedUpdateAndRestart"/> for the immediate
    /// path; wiring that is out of scope here (Canon: existing
    /// hotkeys/overlay untouched).
    /// </summary>
    /// <param name="log">Operational-text-only logger. Never throws on
    /// this class's side regardless of what the delegate does.</param>
    /// <param name="isInstalledOverride">Test seam — see
    /// VelopackUpdaterTests.cs. Production callers omit this and get the
    /// real <see cref="IsInstalled"/> check.</param>
    public static async Task CheckAndApplyAsync(Action<string> log, Func<bool>? isInstalledOverride = null)
    {
        try
        {
            var isInstalled = isInstalledOverride?.Invoke() ?? IsInstalled();
            if (!isInstalled)
            {
                // See class doc comment — this is the whole invariant.
                return;
            }

            var configPath = Path.Combine(AppContext.BaseDirectory, ReleaseSourceFileName);
            var config = ReleaseSourceConfig.Load(configPath, log);
            if (config is null)
            {
                // Load already logged the specific reason.
                return;
            }

            var manager = new UpdateManager(new S3UpdateSource(config));

            var updateInfo = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (updateInfo is null)
            {
                log("[VelopackUpdater] no update available.");
                return;
            }

            log($"[VelopackUpdater] update available ({updateInfo.TargetFullRelease.Version}) — downloading.");
            await manager.DownloadUpdatesAsync(updateInfo).ConfigureAwait(false);
            log("[VelopackUpdater] update downloaded — will apply automatically on next launch.");
        }
        catch (Exception ex)
        {
            // Never let an update-check failure (network down, bad
            // credentials, bucket unreachable, ...) surface to the user or
            // affect the rest of startup — this channel is strictly
            // additional.
            log($"[VelopackUpdater] update check failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Applies an already-downloaded update immediately and restarts the
    /// app — for a FUTURE affordance to call (not wired to any UI by this
    /// change; see class doc comment). No-ops if not installed or if there
    /// is nothing downloaded yet.
    /// </summary>
    public static void ApplyDownloadedUpdateAndRestart(Action<string> log)
    {
        try
        {
            if (!IsInstalled())
            {
                return;
            }

            // The source is never touched by ApplyUpdatesAndRestart — it
            // only reads locally-downloaded package metadata via Locator —
            // so it doesn't matter that a fresh S3UpdateSource here has no
            // in-flight request state from CheckAndApplyAsync's instance.
            var configPath = Path.Combine(AppContext.BaseDirectory, ReleaseSourceFileName);
            var config = ReleaseSourceConfig.Load(configPath, log);
            if (config is null)
            {
                return;
            }

            var manager = new UpdateManager(new S3UpdateSource(config));
            var pending = manager.UpdatePendingRestart;
            if (pending is null)
            {
                log("[VelopackUpdater] Restart now requested but nothing is downloaded.");
                return;
            }

            log($"[VelopackUpdater] applying update {pending.Version} and restarting.");
            manager.ApplyUpdatesAndRestart(pending);
        }
        catch (Exception ex)
        {
            log($"[VelopackUpdater] apply-and-restart failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// True only when THIS running copy was installed by Setup.exe/vpk.
    /// A throwaway <see cref="IUpdateSource"/> is fine here — this
    /// property only ever reads local install metadata via
    /// UpdateManager.Locator; it never touches Source.
    /// </summary>
    private static bool IsInstalled()
    {
        return new UpdateManager(new NullUpdateSource()).IsInstalled;
    }

    private sealed class NullUpdateSource : IUpdateSource
    {
        public Task<VelopackAssetFeed> GetReleaseFeed(
            IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
            => throw new InvalidOperationException("NullUpdateSource should never be called — IsInstalled only reads local install metadata.");

        public Task DownloadReleaseEntry(
            IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken = default)
            => throw new InvalidOperationException("NullUpdateSource should never be called — IsInstalled only reads local install metadata.");
    }
}
