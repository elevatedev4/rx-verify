using System;
using System.IO;
using System.Reflection;

namespace RxVerifyOverlay.Diagnostics;

/// <summary>
/// Best-effort app version + commit sha for the "Copy logs" blob header
/// (RxLogFormatter/OverlayViewModel.BuildCurrentLogBlob) — so a pasted log
/// is traceable to an exact build without asking the pharmacist to dig up
/// a changelog. Never throws; falls back to "unknown" on any failure, same
/// best-effort philosophy as Ocr/OcrLogger.cs.
/// </summary>
public static class AppDiagnostics
{
    public static string GetAppVersion()
    {
        try
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version?.ToString() ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    /// <summary>
    /// Walks up from the running app's build output directory (same
    /// walk-the-parent-chain pattern as Models/OverlaySettings.cs
    /// ResolveDefaultCliPath) looking for a .git entry, then reads HEAD
    /// (following one level of "ref: ..." indirection for a normal branch
    /// checkout, and one level of worktree ".git file -> gitdir:"
    /// indirection) to get the current commit sha, truncated to 8 chars.
    /// Returns "unknown" if no .git is found or anything about it can't be
    /// read (e.g. a packed-refs-only ref this simple reader doesn't
    /// follow) — this is a diagnostic nicety, never required for the app
    /// to function.
    /// </summary>
    public static string GetCommitSha(string? startDir = null)
    {
        try
        {
            DirectoryInfo? dir = new DirectoryInfo(startDir ?? AppContext.BaseDirectory);
            const int maxLevels = 64;
            for (var i = 0; dir is not null && i < maxLevels; i++, dir = dir.Parent)
            {
                var gitPath = Path.Combine(dir.FullName, ".git");

                if (Directory.Exists(gitPath))
                {
                    return ReadHeadSha(gitPath) ?? "unknown";
                }

                if (File.Exists(gitPath))
                {
                    // Worktree checkout: ".git" is a file containing "gitdir: <path>".
                    var contents = File.ReadAllText(gitPath).Trim();
                    if (contents.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
                    {
                        var realGitDir = contents["gitdir:".Length..].Trim();
                        if (!Path.IsPathRooted(realGitDir))
                        {
                            realGitDir = Path.Combine(dir.FullName, realGitDir);
                        }
                        return ReadHeadSha(realGitDir) ?? "unknown";
                    }
                }
            }
        }
        catch
        {
            // best-effort only — see class doc.
        }

        return "unknown";
    }

    private static string? ReadHeadSha(string gitDir)
    {
        try
        {
            var headPath = Path.Combine(gitDir, "HEAD");
            if (!File.Exists(headPath)) return null;

            var head = File.ReadAllText(headPath).Trim();
            if (head.StartsWith("ref:", StringComparison.OrdinalIgnoreCase))
            {
                var refRelative = head["ref:".Length..].Trim().Replace('/', Path.DirectorySeparatorChar);

                // Worktree checkouts (see GetCommitSha's "gitdir:" branch —
                // this repo does all its feature work in worktrees) keep
                // their own private HEAD but share refs/objects with the
                // main repo via a "commondir" file (git's documented
                // gitrepository-layout) — refs/heads/* lives under THAT
                // directory, not this worktree's own gitDir. A normal
                // (non-worktree) checkout has no commondir file, so
                // commonDir just stays gitDir itself.
                var commonDir = gitDir;
                var commonDirFile = Path.Combine(gitDir, "commondir");
                if (File.Exists(commonDirFile))
                {
                    var commonDirRelative = File.ReadAllText(commonDirFile).Trim();
                    commonDir = Path.IsPathRooted(commonDirRelative)
                        ? commonDirRelative
                        : Path.GetFullPath(Path.Combine(gitDir, commonDirRelative));
                }

                var refPath = Path.Combine(commonDir, refRelative);
                if (!File.Exists(refPath)) return null; // packed-refs case not handled — v0 diagnostic nicety only
                head = File.ReadAllText(refPath).Trim();
            }

            return head.Length >= 8 ? head[..8] : head;
        }
        catch
        {
            return null;
        }
    }
}
