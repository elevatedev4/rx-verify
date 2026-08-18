using System;
using System.Diagnostics;
using System.Windows;

namespace RxVerifyOverlay.Update;

/// <summary>
/// One-click "Update" (branch fix/rightclick-all-feedback-compact, task 4)
/// — launches the exact same pinned bootstrap one-liner from the HQ
/// pinned-snippet box (README.md / bootstrap-fresh.ps1's own header doc)
/// in a fresh PowerShell process, then exits this app so the relaunch
/// (update-and-run.ps1, chained from bootstrap-fresh.ps1's own step 7) can
/// rebuild/replace it cleanly. Split into a pure command-string builder
/// (BuildBootstrapCommand — unit tested, see
/// RxVerifyOverlay.Tests/UpdateLauncherTests.cs) and the actual
/// Process.Start/Shutdown side effects (LaunchBootstrapAndExit), same
/// "pure logic tested, transport plumbing isn't" split as Update/
/// UpdateChecker.cs and Engine/EngineClient.cs.
/// </summary>
public static class UpdateLauncher
{
    /// <summary>
    /// Fixed public bootstrap URL — see bootstrap-fresh.ps1's own header
    /// doc for the full one-liner this is copied from. Not a secret (the
    /// repo is public); only -ReportKey below is workstation-specific.
    /// </summary>
    private const string BootstrapScriptBlock =
        "[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12; & ([scriptblock]::Create((irm https://raw.githubusercontent.com/elevatedev4/rx-verify/main/bootstrap-fresh.ps1)))";

    /// <summary>
    /// Builds the full -Command payload passed to powershell.exe —
    /// BootstrapScriptBlock alone when <paramref name="reportKey"/> is
    /// null/blank (bootstrap-fresh.ps1's own -ReportKey parameter defaults
    /// to '', so omitting it entirely is valid — see that script's own
    /// header doc), or with `-ReportKey '&lt;key&gt;'` appended when set.
    ///
    /// Single-quoted, with any embedded single quote doubled ('' is
    /// PowerShell's own escape for a literal ' inside a single-quoted
    /// string) — NOT double-quoted: bootstrap-fresh.ps1's own doc
    /// specifically calls out that Windows PowerShell expands `$` inside a
    /// double-quoted string, which would silently truncate a key
    /// containing one with zero diagnostic signal. A report key is a
    /// low-privilege, report-intake-only bearer secret (see
    /// Models/OverlaySettings.cs RxVerifyReportKey's own doc) — worth
    /// getting this escaping right even though it isn't PHI.
    /// </summary>
    public static string BuildBootstrapCommand(string? reportKey)
    {
        if (string.IsNullOrWhiteSpace(reportKey)) return BootstrapScriptBlock;

        var escaped = reportKey.Replace("'", "''");
        return $"{BootstrapScriptBlock} -ReportKey '{escaped}'";
    }

    /// <summary>
    /// Launches powershell.exe running BuildBootstrapCommand's output as a
    /// brand new, independent process (UseShellExecute=false plus a plain
    /// ArgumentList — NOT a single hand-quoted Arguments string — so .NET's
    /// own Win32-correct per-argument quoting handles the single quotes/
    /// spaces inside the script block and report key without this class
    /// needing to re-derive CommandLineToArgvW's quoting rules itself),
    /// then calls Application.Current.Shutdown() so this process exits and
    /// releases whatever file/process locks the rebuild in
    /// update-and-run.ps1 needs (matching dist/cli.js, the compiled
    /// RxVerifyOverlay.exe itself). Does NOT catch its own failures — a
    /// failure to even START powershell.exe (e.g. missing/blocked binary)
    /// throws out to the caller (see MainWindow.xaml.cs OnUpdateClick,
    /// which catches and shows a message box, same pattern as
    /// OpenReportErrorDialog's own catch) rather than being swallowed
    /// here — Application.Current.Shutdown() must never run when the
    /// process we were trying to hand off to never actually started,
    /// which a blanket try/catch inside this method could otherwise mask.
    /// </summary>
    public static void LaunchBootstrapAndExit(string? reportKey)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = false
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(BuildBootstrapCommand(reportKey));

        Process.Start(psi);
        Application.Current.Shutdown();
    }
}
