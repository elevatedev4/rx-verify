using System.Collections.Generic;
using System.Threading.Tasks;
using RxVerifyOverlay.VelopackIntegration;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// VelopackUpdater.CheckAndApplyAsync's load-bearing invariant: it must
/// return immediately — never constructing an S3UpdateSource or touching
/// the network — when the running copy was not installed by Setup.exe/vpk,
/// so the existing bootstrap-fresh.ps1 + shortcut flow is completely
/// unaffected. <c>isInstalledOverride</c> is the test seam for this (a
/// real "not installed" check would also return false when run under the
/// test runner, but injecting it makes the assertion explicit and
/// independent of the test environment). Mirrors vaccine-assist's
/// VelopackUpdaterTests.cs.
/// </summary>
public class VelopackUpdaterTests
{
    [Fact]
    public async Task CheckAndApplyAsyncReturnsImmediatelyWhenNotInstalled()
    {
        var logs = new List<string>();

        await VelopackUpdater.CheckAndApplyAsync(logs.Add, isInstalledOverride: () => false);

        // No config load attempt, no "[VelopackUpdater] ..." message, no
        // exception — the gate short-circuits before any of that.
        Assert.Empty(logs);
    }

    [Fact]
    public async Task CheckAndApplyAsyncNeverThrowsEvenWhenInstalledWithNoConfig()
    {
        // isInstalledOverride: true, but no release-source.json exists next
        // to the test assembly — exercises the "installed but unconfigured"
        // path, which must still degrade to a logged no-op, never a thrown
        // exception (this is on the fire-and-forget startup call in
        // App.xaml.cs, which has nothing to catch it).
        var logs = new List<string>();

        await VelopackUpdater.CheckAndApplyAsync(logs.Add, isInstalledOverride: () => true);

        Assert.Single(logs);
        Assert.Contains("no config", logs[0]);
    }
}
