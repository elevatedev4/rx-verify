using System.Collections.Generic;
using System.IO;
using RxVerifyOverlay.Update;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for the pure parts of UpdateLauncher (Update/UpdateLauncher.cs)
/// — which update-and-run.ps1 the "Update" button runs and the powershell.exe
/// arguments it gets (branch feat/private-repo-ssh: the button runs THIS PC's
/// own script instead of a raw.githubusercontent.com one-liner, which dies
/// when the GitHub repo goes private). LaunchUpdateAndExit itself
/// (Process.Start + app shutdown) is deliberately not exercised here — same
/// "pure logic tested, OS-level side effects aren't" split as every other
/// *Launcher/*Submitter class in this app. Paths below are synthetic.
/// </summary>
public class UpdateLauncherTests
{
    private static string P(params string[] parts) => Path.Combine(parts);

    private static System.Func<string, bool> Exists(params string[] existing)
    {
        var set = new HashSet<string>(existing);
        return path => set.Contains(path);
    }

    [Fact]
    public void RunsTheScriptInTheCheckoutTheAppLivesIn()
    {
        var root = P(Path.GetTempPath(), "synthetic-rx", "repo");
        var start = P(root, "overlay", "RxVerifyOverlay", "bin", "Debug", "net8.0-windows");
        var script = P(root, "update-and-run.ps1");

        var resolved = UpdateLauncher.ResolveUpdateScriptPath(start, P(Path.GetTempPath(), "synthetic-user"), Exists(script));

        Assert.Equal(script, resolved);
    }

    [Fact]
    public void FallsBackToTheCanonicalUserProfileCheckout()
    {
        var profile = P(Path.GetTempPath(), "synthetic-user");
        var script = P(profile, "claude", "rx-verify", "update-and-run.ps1");
        var elsewhere = P(Path.GetTempPath(), "somewhere-else", "bin");

        var resolved = UpdateLauncher.ResolveUpdateScriptPath(elsewhere, profile, Exists(script));

        Assert.Equal(script, resolved);
    }

    [Fact]
    public void ReturnsNullWhenNoScriptExistsAnywhere()
    {
        var resolved = UpdateLauncher.ResolveUpdateScriptPath(
            P(Path.GetTempPath(), "synthetic-rx", "bin"), P(Path.GetTempPath(), "synthetic-user"), Exists());

        Assert.Null(resolved);
    }

    [Fact]
    public void PrefersTheCheckoutTheAppLivesInOverTheCanonicalOne()
    {
        var profile = P(Path.GetTempPath(), "synthetic-user");
        var canonical = P(profile, "claude", "rx-verify", "update-and-run.ps1");
        var root = P(Path.GetTempPath(), "synthetic-rx", "repo");
        var own = P(root, "update-and-run.ps1");

        var resolved = UpdateLauncher.ResolveUpdateScriptPath(P(root, "bin"), profile, Exists(canonical, own));

        Assert.Equal(own, resolved);
    }

    [Fact]
    public void BuildsPowershellArgumentsThatRunTheScriptFileWithNoReportKey()
    {
        var args = UpdateLauncher.BuildArguments(@"C:\Users\Synthetic User\claude\rx-verify\update-and-run.ps1");

        Assert.Equal(
            new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", @"C:\Users\Synthetic User\claude\rx-verify\update-and-run.ps1" },
            args);
        Assert.DoesNotContain("-ReportKey", args);
        Assert.DoesNotContain("-Command", args);
    }
}
