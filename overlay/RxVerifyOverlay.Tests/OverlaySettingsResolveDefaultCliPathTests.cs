using System;
using System.IO;
using RxVerifyOverlay.Models;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for OverlaySettings.ResolveDefaultCliPath, the walk-up-
/// from-build-output auto-detection added so a fresh workstation with an
/// empty/stale EngineCliPath doesn't hard-fail with "Engine CLI not
/// found" (see MainWindow.xaml.cs constructor). Pure filesystem checks
/// against temp directories -- no UIA, no engine call, no synthetic PHI
/// needed.
/// </summary>
public class OverlaySettingsResolveDefaultCliPathTests
{
    [Fact]
    public void FindsDistCliJsByWalkingUpFromNestedBuildOutputDir()
    {
        var root = Directory.CreateTempSubdirectory("rxverify-resolve-test-");
        try
        {
            var distDir = Directory.CreateDirectory(Path.Combine(root.FullName, "dist"));
            var cliPath = Path.Combine(distDir.FullName, "cli.js");
            File.WriteAllText(cliPath, "// fake cli.js");

            // Mirrors the real layout: <repoRoot>/overlay/RxVerifyOverlay/bin/<cfg>/<tfm>/
            var deepDir = Directory.CreateDirectory(
                Path.Combine(root.FullName, "overlay", "RxVerifyOverlay", "bin", "Debug", "net8.0-windows"));

            var resolved = OverlaySettings.ResolveDefaultCliPath(deepDir.FullName);

            Assert.Equal(Path.GetFullPath(cliPath), Path.GetFullPath(resolved));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void ReturnsEmptyStringWhenNoDistCliJsExistsAnywhereAbove()
    {
        var root = Directory.CreateTempSubdirectory("rxverify-resolve-test-negative-");
        try
        {
            var deepDir = Directory.CreateDirectory(
                Path.Combine(root.FullName, "overlay", "RxVerifyOverlay", "bin", "Debug", "net8.0-windows"));

            var resolved = OverlaySettings.ResolveDefaultCliPath(deepDir.FullName);

            Assert.Equal("", resolved);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
