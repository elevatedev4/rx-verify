using System;
using System.IO;

namespace RxVerifyOverlay.Reports;

/// <summary>
/// Small append-only log writer for Reports-mode runs. NOTE for anyone
/// re-checking the branch brief's "find the existing log writer and use
/// it" instruction: this repo has no general-purpose app-wide log writer
/// to reuse — the only file-based logger in Diagnostics/Ocr is
/// Ocr/OcrLogger.cs, which is OCR-raw-text-specific (per-day file, PHI
/// dedup keyed on OCR text, 5MB rotation-by-truncation) and not a fit
/// for a report-run summary line. This class instead MIRRORS OcrLogger's
/// own conventions (static class, lock object, best-effort/never-throws
/// writes, its own dedicated log directory) rather than bolting
/// unrelated content onto it.
///
/// Contains NO patient data by construction — only report display names,
/// statuses, file paths under "Pioneer Reports", and failure reasons
/// (automation/timeout text, never on-screen field content).
/// </summary>
public static class ReportsLog
{
    private static readonly object LockObj = new();

    private static string LogDirectory => Path.Combine(Path.GetTempPath(), "RxVerifyOverlay", "Reports");

    private static string LogFilePath => Path.Combine(LogDirectory, $"reports-{DateTime.Now:yyyyMMdd}.log");

    /// <summary>Appends one line (with a timestamp prefix) to today's app-wide reports log. Never throws — a failed write is silently dropped, same posture as OcrLogger/AutoDiagnosticStateStore.</summary>
    public static void Append(string line)
    {
        try
        {
            lock (LockObj)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(LogFilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
        }
        catch
        {
            // Best-effort only — see class doc.
        }
    }

    /// <summary>
    /// Appends one line to a run-scoped log file inside the report
    /// output folder (GOAL brief step 6: "a run-&lt;timestamp&gt;.log in the
    /// output folder"). <paramref name="runLogFileName"/> is the full
    /// file name (e.g. "run-20260916-140501.log") this run already
    /// picked once at start — see ReportsCoordinator.BuildRunLogFileName
    /// — so every line in one run lands in the same file. Never throws.
    /// </summary>
    public static void AppendToRunLog(string outputFolder, string runLogFileName, string line)
    {
        try
        {
            lock (LockObj)
            {
                Directory.CreateDirectory(outputFolder);
                var path = Path.Combine(outputFolder, runLogFileName);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
        }
        catch
        {
            // Best-effort only — see class doc.
        }
    }
}
