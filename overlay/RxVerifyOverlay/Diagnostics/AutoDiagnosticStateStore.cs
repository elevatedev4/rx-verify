using System;
using System.IO;
using System.Text.Json;

namespace RxVerifyOverlay.Diagnostics;

/// <summary>
/// Load/save for AutoDiagnosticPolicy's AutoDiagnosticRateLimitState —
/// %AppData%\RxVerifyOverlay\auto-diagnostic-state.json, same file-location
/// convention (and same "default real path + overridable param for
/// testability" shape) as Reporting/PendingReportsQueue.cs's
/// pending-reports.jsonl and Models/OverlaySettings.cs's settings.json.
///
/// Best-effort, like every other piece of this app's local persistence:
/// Load never throws (a missing/corrupt file just means "no history yet",
/// same posture a first-run PC would have anyway) and Save never throws
/// (a failed write costs at most this one rate-limit update, never blocks
/// or crashes the caller — see AutoDiagnosticPolicy.ShouldReport's own
/// doc for why UpdatedState must be persisted even on a "don't report"
/// outcome, and why losing that persistence occasionally is an acceptable
/// v0 tradeoff for this low-volume, non-critical-path feature, same as
/// PendingReportsQueue's own documented tradeoffs).
/// </summary>
public static class AutoDiagnosticStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string DefaultStateFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RxVerifyOverlay", "auto-diagnostic-state.json");

    /// <summary>Never throws — returns a fresh, empty state on a missing file, corrupt JSON, or any I/O error.</summary>
    public static AutoDiagnosticRateLimitState Load(string? filePath = null)
    {
        var path = filePath ?? DefaultStateFilePath;
        try
        {
            if (!File.Exists(path)) return new AutoDiagnosticRateLimitState();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AutoDiagnosticRateLimitState>(json, JsonOptions) ?? new AutoDiagnosticRateLimitState();
        }
        catch
        {
            return new AutoDiagnosticRateLimitState();
        }
    }

    /// <summary>Never throws — a failed write is silently dropped (see class doc); returns whether the write actually succeeded so a caller that cares can log it.</summary>
    public static bool Save(AutoDiagnosticRateLimitState state, string? filePath = null)
    {
        var path = filePath ?? DefaultStateFilePath;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(state, JsonOptions);
            File.WriteAllText(path, json);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
