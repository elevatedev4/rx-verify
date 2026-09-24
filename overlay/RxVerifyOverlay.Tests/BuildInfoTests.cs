using RxVerifyOverlay.Update;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for Update/BuildInfo.cs (W-T92 round 3, GOAL brief step 1:
/// "log the git sha + build time at startup ... show it in the Reports
/// window title"). The xunit test host never passes
/// -p:RxVerifyBuildSha/-p:RxVerifyBuildTime the way update-and-run.ps1
/// does for a real launch, so these assert the CONTRACT (never throws,
/// never blank, always follows the "Rx Verify build &lt;sha&gt; &lt;time&gt; UTC"
/// shape) rather than a specific sha - see RxVerifyOverlay.csproj's
/// RxVerifyBuildSha/RxVerifyBuildTime Condition defaults for what a
/// property-less build (like this one) actually embeds ("local" / the
/// real build clock time).
/// </summary>
public class BuildInfoTests
{
    [Fact]
    public void ShaIsNeverNullOrEmpty()
    {
        Assert.False(string.IsNullOrEmpty(BuildInfo.Sha));
    }

    [Fact]
    public void BuildTimeUtcIsNeverNullOrEmpty()
    {
        Assert.False(string.IsNullOrEmpty(BuildInfo.BuildTimeUtc));
    }

    [Fact]
    public void SummaryFollowsTheDocumentedLogLineShape()
    {
        var summary = BuildInfo.Summary;

        Assert.StartsWith("Rx Verify build ", summary);
        Assert.EndsWith(" UTC", summary);
        Assert.Contains(BuildInfo.Sha, summary);
        Assert.Contains(BuildInfo.BuildTimeUtc, summary);
    }

    [Fact]
    public void WithNoBuildPropertiesPassed_ShaFallsBackToLocal()
    {
        // This test project's own build never passes
        // -p:RxVerifyBuildSha - confirms RxVerifyOverlay.csproj's
        // Condition default actually took effect rather than silently
        // producing an empty AssemblyMetadataAttribute value.
        Assert.Equal("local", BuildInfo.Sha);
    }
}
