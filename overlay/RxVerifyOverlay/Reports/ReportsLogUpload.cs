using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using RxVerifyOverlay.Reporting;

namespace RxVerifyOverlay.Reports;

/// <summary>
/// W-Q13 (Will: yes): when a Reports run stops, the Reports window log goes to
/// HQ as ONE message. The message is built only from the lines the Reports
/// window log itself shows (the same text, nothing added from the screen, no
/// OCR text, no patient names) plus a one-line header of counts and the build.
///
/// Sent through the HQ path the app already has: FeedbackSubmitter ->
/// /api/rxverify-feedback with OverlaySettings.RxVerifyReportKey (the dedicated
/// low-privilege key). No new credential. FAIL SOFT: SendAsync never throws
/// and a failed or skipped send never affects the run.
///
/// PRIVACY: the window log shows full paths (they include the Windows account
/// name), exception messages and UIA control names. None of that leaves this PC:
/// <see cref="Sanitize"/> runs on every line inside <see cref="BuildMessage"/>
/// (not at the call sites), so no caller can send an unsanitized line.
///
/// Everything except SendAsync is pure (unit tested).
/// </summary>
public static class ReportsLogUpload
{
    /// <summary>HQ rejects a feedback message longer than this (FEEDBACK_MESSAGE_MAX_LENGTH in manager-hq).</summary>
    public const int MaxMessageChars = 4000;

    /// <summary>A normal (non-daily) run stops early when any report did not save (failed, still pending after Stop, or still running).</summary>
    public static bool ShouldSend(IReadOnlyList<ReportRunOutcome> outcomes)
    {
        if (outcomes.Count == 0) return false;
        foreach (var outcome in outcomes)
        {
            if (outcome.Status != ReportRunStatus.Saved) return true;
        }
        return false;
    }

    /// <summary>A daily run stopped when it was stopped, failed on a step, needs Search Drawers, or could not start; a run that saved every step does not send.</summary>
    public static bool ShouldSend(ReportsCoordinator.DailyRunSummary summary) =>
        summary.Stopped || summary.FailedStep is not null || summary.NeedsSearchDrawers || summary.FailureReason is not null || summary.Saved != summary.Total;

    /// <summary>The date-entry test is a diagnostic; send only when it did not complete.</summary>
    public static bool ShouldSend(ReportRunResult result) => result.Status != ReportRunStatus.Saved;

    private const string PathDetailNote = "(details not sent)";

    /// <summary>A path right after a quote, up to the closing quote (an apostrophe inside a folder name does not end it).</summary>
    private static readonly Regex QuotedPath = new(
        @"(?<=['""])(?:[A-Za-z]:[\\/]|\\\\[^\\/\s]+[\\/])[^\r\n]*?(?=['""](?:[\s.,;:)\]]|$))", RegexOptions.Compiled);

    /// <summary>Any other Windows path (drive or UNC); runs to the end of the line because folder names can hold spaces.</summary>
    private static readonly Regex AnyPath = new(
        @"(?<![A-Za-z0-9])(?:[A-Za-z]:[\\/]|\\\\[^\\/\s'""]+[\\/])[^\r\n]*", RegexOptions.Compiled);

    /// <summary>The UIA control description the driver logs: class='..' name='..' id='..'.</summary>
    private static readonly Regex ControlName = new(@"\bname='(.*?)'(?= id=')", RegexOptions.Compiled);

    /// <summary>Any remaining name='..' or title='..' value.</summary>
    private static readonly Regex NameOrTitle = new(@"\b(name|title)='([^']*)'", RegexOptions.Compiled);

    /// <summary>
    /// Makes one window-log line safe to leave this PC: every Windows path is cut
    /// down to its last folder or file name (so "C:\Users\SomeName\Downloads\2026-10_EOM reports"
    /// becomes "…\2026-10_EOM reports"), an output-folder exception message is
    /// dropped, and control names / titles are redacted unless they are a report
    /// name, column header or known screen name (the same allowlists the log
    /// already uses for window titles).
    /// </summary>
    public static string Sanitize(string line)
    {
        if (string.IsNullOrEmpty(line)) return line;

        const string folderFailure = "Couldn't create the output folder:";
        var at = line.IndexOf(folderFailure, StringComparison.OrdinalIgnoreCase);
        if (at >= 0) return line.Substring(0, at + folderFailure.Length) + " " + PathDetailNote;

        line = ControlName.Replace(line, m => "name='" + SafeName(m.Groups[1].Value) + "'");
        line = NameOrTitle.Replace(line, m => m.Groups[1].Value + "='" + SafeName(m.Groups[2].Value) + "'");
        line = QuotedPath.Replace(line, m => LastSegment(m.Value));
        line = AnyPath.Replace(line, m => LastSegment(m.Value));
        return line;
    }

    private static string SafeName(string value)
    {
        if (value.Length == 0 || value == UiaNameRedaction.RedactedName || value == "<untitled>") return value;
        if (ReportRowNameRedaction.IsKnownSafeValue(value)) return value;
        if (MainWindowTitleRedaction.RedactIfNeeded(value) == value) return value;
        return UiaNameRedaction.RedactedName;
    }

    private static string LastSegment(string path)
    {
        var trimmed = path.TrimEnd('\\', '/', ' ');
        var cut = trimmed.LastIndexOfAny(new[] { '\\', '/' });
        var last = cut >= 0 ? trimmed.Substring(cut + 1) : trimmed;
        return "…\\" + last;
    }

    /// <summary>One header line: which run, counts, build. Counts and fixed text only.</summary>
    public static string BuildHeader(string runKind, string? countsText, string buildSummary) =>
        string.IsNullOrWhiteSpace(countsText)
            ? $"Reports log - {runKind} stopped. {buildSummary}"
            : $"Reports log - {runKind} stopped ({countsText}). {buildSummary}";

    public static string DailyCounts(ReportsCoordinator.DailyRunSummary summary) => $"{summary.Saved} of {summary.Total} saved";

    public static string RunCounts(IReadOnlyList<ReportRunOutcome> outcomes)
    {
        var saved = 0;
        foreach (var outcome in outcomes)
        {
            if (outcome.Status == ReportRunStatus.Saved) saved++;
        }
        return $"{saved} of {outcomes.Count} saved";
    }

    /// <summary>
    /// Header, a newline, then the log lines. When the lines do not fit in
    /// <paramref name="maxChars"/> the OLDEST lines are dropped (the end of the
    /// log is where a run stopped), with a one-line note saying how many.
    /// </summary>
    public static string BuildMessage(string header, IReadOnlyList<string> rawLines, int maxChars = MaxMessageChars)
    {
        header = Sanitize(header);
        var lines = new List<string>(rawLines.Count);
        foreach (var raw in rawLines) lines.Add(Sanitize(raw));
        header = header.Length > maxChars ? header.Substring(0, maxChars) : header;
        var budget = maxChars - header.Length - 1; // newline after the header
        if (lines.Count == 0 || budget <= 0) return header;

        var total = 0;
        foreach (var line in lines) total += line.Length + 1;
        if (total <= budget) return header + "\n" + string.Join("\n", lines);

        // Keep the newest lines that fit, leaving room for the omitted-lines note.
        var kept = new List<string>();
        var used = 0;
        const int noteReserve = 48;
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var cost = lines[i].Length + 1;
            if (used + cost > budget - noteReserve)
            {
                if (kept.Count == 0 && budget - noteReserve > 0)
                {
                    // One line alone is too long: keep its end.
                    var room = budget - noteReserve - 1;
                    kept.Add(lines[i].Substring(lines[i].Length - room));
                }
                break;
            }
            kept.Add(lines[i]);
            used += cost;
        }
        kept.Reverse();
        var omitted = lines.Count - kept.Count;

        var sb = new StringBuilder(header);
        sb.Append('\n').Append($"({omitted} earlier line(s) not sent)");
        foreach (var line in kept) sb.Append('\n').Append(line);
        return sb.Length > maxChars ? sb.ToString(0, maxChars) : sb.ToString();
    }

    /// <summary>
    /// Posts the message through the existing feedback path. Never throws.
    /// Returns a plain line for the window log: sent, or why not.
    /// </summary>
    public static async Task<string> SendAsync(FeedbackSubmitter submitter, bool hasReportKey, string message, string? commit)
    {
        try
        {
            if (!hasReportKey) return "Log not sent to HQ - no report key is set on this PC.";
            var payload = FeedbackBuilder.Build(message, null, commit, DateTime.UtcNow);
            return await submitter.SubmitAsync(payload).ConfigureAwait(false)
                ? "Log sent to HQ."
                : "Log not sent to HQ - HQ could not be reached.";
        }
        catch
        {
            return "Log not sent to HQ - HQ could not be reached.";
        }
    }
}
