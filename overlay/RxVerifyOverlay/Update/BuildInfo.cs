using System.Linq;
using System.Reflection;

namespace RxVerifyOverlay.Update;

/// <summary>
/// W-T92 round 3 (Will, 2026-09-22 8:05pm, after two rounds that DID
/// change the date-entry source: "Still doesn't work. Literally the
/// action hasn't changed at all."). Whatever caused that gap between a
/// real code change and what he actually saw on his workstation - a
/// stale exe surviving a pull, a taskbar shortcut he never re-created, or
/// simply never relaunching the app between test rounds - the fix here
/// is to make that gap impossible to miss: every build embeds its own
/// git sha + build time (see RxVerifyOverlay.csproj's
/// RxVerifyBuildSha/RxVerifyBuildTime AssemblyMetadata, set by
/// update-and-run.ps1's `dotnet build -p:RxVerifyBuildSha=... -p:RxVerifyBuildTime=...`
/// on every single run), and this class reads that back at runtime so it
/// can be logged at startup, logged in every Reports run log, and shown
/// in the Reports window's own title bar - Will can now tell, from his
/// chair, whether the build in front of him is actually the one that was
/// just pushed.
///
/// Reads via reflection (AssemblyMetadataAttribute) rather than a
/// generated .cs file - no custom MSBuild target needed, and it degrades
/// gracefully ("local"/best-effort) for a plain `dotnet build` that never
/// passed the properties (e.g. a dev machine), never throwing.
/// </summary>
public static class BuildInfo
{
    public static string Sha => GetMetadata("RxVerifyBuildSha") ?? "unknown";

    public static string BuildTimeUtc => GetMetadata("RxVerifyBuildTime") ?? "unknown";

    /// <summary>"Rx Verify build &lt;sha&gt; &lt;time&gt;" - the exact line format the GOAL brief asks for, reused for the startup log, the Reports run log, and (trimmed) the Reports window title.</summary>
    public static string Summary => $"Rx Verify build {Sha} {BuildTimeUtc} UTC";

    private static string? GetMetadata(string key)
    {
        try
        {
            var value = Assembly.GetExecutingAssembly()
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == key)
                ?.Value;

            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            // Best-effort only - a build-info line failing to resolve must
            // never be the reason the app itself won't start.
            return null;
        }
    }
}
