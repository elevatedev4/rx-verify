using System.Collections.Generic;
using System.IO;
using RxVerifyOverlay.Update;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for RepoLocator (Update/RepoLocator.cs) — finds the rx-verify
/// checkout the update check and Update button operate on. A fake file-exists
/// probe stands in for the file system; paths are synthetic.
/// </summary>
public class RepoLocatorTests
{
    private static string P(params string[] parts) => Path.Combine(parts);

    private static System.Func<string, bool> Exists(params string[] existing)
    {
        var set = new HashSet<string>(existing);
        return path => set.Contains(path);
    }

    [Fact]
    public void FindsTheNearestAncestorHoldingTheLauncherScript()
    {
        var root = P(Path.GetTempPath(), "synthetic-rx", "repo");
        var start = P(root, "overlay", "RxVerifyOverlay", "bin", "Debug", "net8.0-windows");

        var found = RepoLocator.FindRepoRoot(start, Exists(P(root, "update-and-run.ps1")));

        Assert.Equal(root, found);
    }

    [Fact]
    public void ReturnsNullForBlankStartDirOrNoMarker()
    {
        Assert.Null(RepoLocator.FindRepoRoot(null, Exists()));
        Assert.Null(RepoLocator.FindRepoRoot("  ", Exists()));
        Assert.Null(RepoLocator.FindRepoRoot(P(Path.GetTempPath(), "synthetic-rx", "bin"), Exists()));
    }

    [Fact]
    public void ResolveFallsBackToCanonicalOnlyWhenItHoldsTheScript()
    {
        var profile = P(Path.GetTempPath(), "synthetic-user");
        var canonical = RepoLocator.CanonicalRepoRoot(profile);

        Assert.Equal(P(profile, "claude", "rx-verify"), canonical);
        Assert.Equal(canonical, RepoLocator.ResolveRepoRoot(P(Path.GetTempPath(), "nowhere"), profile, Exists(P(canonical, "update-and-run.ps1"))));
        Assert.Null(RepoLocator.ResolveRepoRoot(P(Path.GetTempPath(), "nowhere"), profile, Exists()));
        Assert.Null(RepoLocator.ResolveRepoRoot(P(Path.GetTempPath(), "nowhere"), null, Exists()));
    }
}
