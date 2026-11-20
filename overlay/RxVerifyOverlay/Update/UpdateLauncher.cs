using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace RxVerifyOverlay.Update;

/// <summary>
/// One-click "Update" (branch fix/rightclick-all-feedback-compact, task 4;
/// reworked on feat/private-repo-ssh) — runs THIS PC's own checkout of
/// update-and-run.ps1 in a fresh PowerShell process, then exits this app so
/// that script can rebuild/replace it cleanly.
///
/// It used to run a raw.githubusercontent.com bootstrap one-liner; that URL
/// stops working the moment the GitHub repo goes private (and would have
/// fetched a script from the very repo the PC is trying to reach). The local
/// script already knows how to reach the repo (HTTPS, or the read-only SSH
/// deploy key via scripts/GitSsh.ps1), reads RxVerifyReportKey out of
/// settings.json itself, and launches the last good build if GitHub can't
/// be reached — so no report key is put on a command line here any more.
///
/// Split into pure helpers (ResolveUpdateScriptPath, BuildArguments — unit
/// tested, see RxVerifyOverlay.Tests/UpdateLauncherTests.cs) and the actual
/// Process.Start/Shutdown side effects (LaunchUpdateAndExit), same "pure
/// logic tested, transport plumbing isn't" split as Update/UpdateChecker.cs.
/// </summary>
public static class UpdateLauncher
{
    /// <summary>
    /// The update script to run: the one in the checkout this app was built
    /// from, else %USERPROFILE%\claude\rx-verify\update-and-run.ps1 (the
    /// canonical location every install script uses). Null when neither
    /// exists. <paramref name="fileExists"/> is injected for tests.
    /// </summary>
    public static string? ResolveUpdateScriptPath(string? startDir, string? userProfile, Func<string, bool> fileExists)
    {
        var root = RepoLocator.ResolveRepoRoot(startDir, userProfile, fileExists);
        return root is null ? null : Path.Combine(root, RepoLocator.LauncherScriptName);
    }

    /// <summary>
    /// powershell.exe arguments that run the script. Passed through
    /// ProcessStartInfo.ArgumentList (not one hand-quoted string) so .NET's
    /// own per-argument quoting handles a script path containing spaces.
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(string scriptPath) =>
        new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath };

    /// <summary>
    /// Starts update-and-run.ps1 as a brand new, independent process in its
    /// own visible window (so progress/errors can be read), THEN calls
    /// Application.Current.Shutdown() so this process releases the file
    /// locks the script's rebuild needs. Does NOT catch its own failures —
    /// a missing script or a failure to even START powershell.exe throws out
    /// to the caller (MainWindow.xaml.cs TriggerUpdate shows a message box)
    /// BEFORE Shutdown, so the app is never closed when the update never
    /// started.
    /// </summary>
    public static void LaunchUpdateAndExit()
    {
        var scriptPath = ResolveUpdateScriptPath(
            AppContext.BaseDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            File.Exists);
        if (scriptPath is null)
        {
            throw new FileNotFoundException(
                $"{RepoLocator.LauncherScriptName} was not found next to this app or in %USERPROFILE%\\claude\\rx-verify.");
        }

        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = Path.GetDirectoryName(scriptPath)!
        };
        foreach (var arg in BuildArguments(scriptPath)) psi.ArgumentList.Add(arg);

        Process.Start(psi);
        Application.Current.Shutdown();
    }
}
