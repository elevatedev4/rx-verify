using System;
using System.IO;

namespace RxVerifyOverlay.Update;

/// <summary>
/// Finds the rx-verify git checkout this app was built from, so the update
/// check (UpdateService: `git ls-remote origin refs/heads/main` run INSIDE
/// the checkout) and the Update button (UpdateLauncher: runs the checkout's
/// own update-and-run.ps1) both talk to the repo the way the repo itself is
/// configured - HTTPS or, once the GitHub repo is private, the read-only SSH
/// deploy key set up by scripts/GitSsh.ps1 (repo-local core.sshCommand).
/// Pure path logic with an injectable file-exists probe so it is unit tested
/// (see RxVerifyOverlay.Tests/RepoLocatorTests.cs).
/// </summary>
public static class RepoLocator
{
    /// <summary>The marker that identifies the repo root - the launcher script every PC runs.</summary>
    public const string LauncherScriptName = "update-and-run.ps1";

    private const int MaxLevels = 64;

    /// <summary>
    /// Walks up from <paramref name="startDir"/> (the app's build output,
    /// &lt;repoRoot&gt;/overlay/RxVerifyOverlay/bin/...) to the first folder
    /// that contains update-and-run.ps1. Null when none is found.
    /// </summary>
    public static string? FindRepoRoot(string? startDir, Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(startDir)) return null;

        try
        {
            DirectoryInfo? dir = new DirectoryInfo(startDir);
            for (var i = 0; dir is not null && i < MaxLevels; i++, dir = dir.Parent)
            {
                if (fileExists(Path.Combine(dir.FullName, LauncherScriptName))) return dir.FullName;
            }
        }
        catch
        {
            // Unreadable/odd path - treat as "not found", never throw (callers are fail-soft).
        }

        return null;
    }

    /// <summary>The canonical checkout location every install script uses: %USERPROFILE%\claude\rx-verify.</summary>
    public static string CanonicalRepoRoot(string userProfile) => Path.Combine(userProfile, "claude", "rx-verify");

    /// <summary>
    /// The repo root for the running app: the checkout the app lives in
    /// (walk-up from AppContext.BaseDirectory), else the canonical
    /// %USERPROFILE%\claude\rx-verify when that holds the launcher script.
    /// </summary>
    public static string? ResolveRepoRoot(string? startDir, string? userProfile, Func<string, bool> fileExists)
    {
        var found = FindRepoRoot(startDir, fileExists);
        if (found is not null) return found;

        if (string.IsNullOrWhiteSpace(userProfile)) return null;
        var canonical = CanonicalRepoRoot(userProfile);
        return fileExists(Path.Combine(canonical, LauncherScriptName)) ? canonical : null;
    }

    /// <summary>ResolveRepoRoot against the real file system and the running app's own location.</summary>
    public static string? ResolveRepoRoot() => ResolveRepoRoot(
        AppContext.BaseDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        File.Exists);
}
