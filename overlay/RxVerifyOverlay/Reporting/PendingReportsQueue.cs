using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RxVerifyOverlay.Reporting;

/// <summary>
/// Store-and-forward for RxReportPayload when HQ's /api/rxverify-reports
/// endpoint is missing/unreachable — no RXVERIFY_REPORT_KEY configured
/// yet, no network, HQ deploy down, etc. (see Reporting/RxReportSubmitter.cs
/// and HQ-ENDPOINT-SPEC.md). Appends one JSON object per line (JSONL) to
/// %AppData%\RxVerifyOverlay\pending-reports.jsonl — same file-location
/// convention as Models/OverlaySettings.cs settings.json.
///
/// <paramref name="filePath"/> on every method defaults to the real
/// %AppData% path but is overridable, same "pure-ish + optional override
/// param" testability pattern as Models/OverlaySettings.cs
/// ResolveDefaultCliPath — see RxVerifyOverlay.Tests/PendingReportsQueueTests.cs
/// for the round-trip tests this enables without touching a real user
/// profile.
///
/// DequeueAll is destructive (reads, deletes, THEN returns what it read)
/// so a report already handed back to a caller for retry is never
/// double-read on a second call — the tradeoff (documented, not an
/// oversight) is that a report that fails mid-retry after DequeueAll
/// already deleted the file is dropped rather than retried forever; see
/// HQ-ENDPOINT-SPEC.md's note on why that's acceptable for this v0,
/// low-volume, non-critical-path feature. RxReportSubmitter.RetryPendingAsync
/// re-enqueues anything that fails ITS OWN submit attempt, so the only
/// truly lost case is the process dying in the narrow window between the
/// file delete and that re-enqueue.
/// </summary>
public static class PendingReportsQueue
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string DefaultQueueFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RxVerifyOverlay", "pending-reports.jsonl");

    /// <summary>
    /// Best-effort append — never throws, but returns false on a write
    /// failure (disk full, locked file, permissions) instead of silently
    /// swallowing it, so RxReportSubmitter.SubmitOrQueueAsync can tell
    /// "queued" apart from "lost" and Integrated/ReportErrorWindow.xaml.cs
    /// can pop an error for the pharmacist on the genuine-loss case
    /// (branch fix/report-submit-instant-close review round 2 — a queue
    /// write failure used to be indistinguishable from success).
    /// </summary>
    public static bool Enqueue(RxReportPayload payload, string? filePath = null)
    {
        var path = filePath ?? DefaultQueueFilePath;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var line = JsonSerializer.Serialize(payload, JsonOptions);
            File.AppendAllText(path, line + Environment.NewLine);
            return true;
        }
        catch
        {
            // Never throws — see class doc — but the caller still needs to
            // know this one report is now lost rather than queued.
            return false;
        }
    }

    /// <summary>Reads every queued report, deletes the file, and returns what it read (see class doc for the destructive-up-front tradeoff). Empty list (never null, never throws) when the file doesn't exist or can't be read.</summary>
    public static List<RxReportPayload> DequeueAll(string? filePath = null)
    {
        var path = filePath ?? DefaultQueueFilePath;
        var result = new List<RxReportPayload>();

        try
        {
            if (!File.Exists(path)) return result;

            var lines = File.ReadAllLines(path);
            File.Delete(path);

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var payload = JsonSerializer.Deserialize<RxReportPayload>(line, JsonOptions);
                    if (payload is not null) result.Add(payload);
                }
                catch
                {
                    // One corrupt line (partial write from a crash mid-append,
                    // etc.) must never lose every other queued report.
                }
            }
        }
        catch
        {
            // Best-effort only — see class doc.
        }

        return result;
    }
}
