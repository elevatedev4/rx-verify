using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RxVerifyOverlay.Diagnostics;

/// <summary>
/// Builds the PHI-safe "logTail" attached to a submitted error report (see
/// Reporting/RxReportPayload.cs LogTail, threaded through from
/// Integrated/ReportErrorWindow.xaml.cs's Submit handler) out of
/// Ocr/OcrLogger.cs's per-day diagnostic log file. Pure function of its
/// input lines — no file I/O here (OcrLogger.TryReadAllLines does that) —
/// so the line-selection rule is directly unit-testable; see
/// RxVerifyOverlay.Tests/LogTailBuilderTests.cs.
///
/// HARD PHI BOUNDARY, per Will's ask ("the HIPAA-free logs obviously"):
/// OcrLogger's file is NOT itself HIPAA-free — it also contains the FULL
/// raw OCR text of every distinct e-script read (patient name/DOB/address/
/// drug/sig, see OcrLogger's own "BOUNDED PHI LOG" doc) and structured
/// word dumps of the same. RxLogFormatter.BuildLogBlob's redactPatient
/// path was considered and rejected for this: it only scrubs the 3
/// patient-identity fields out of a STRUCTURED snapshot of the CURRENT
/// Rx (built from already-parsed field values it has on hand) — it has no
/// way to retroactively scrub arbitrary raw-OCR lines already written to
/// disk for a DIFFERENT, earlier Rx read this session, whose patient
/// tokens aren't in memory anymore. So this takes the other, safer option
/// the brief called for: a strict ALLOWLIST of individual lines that are
/// safe by construction, not a redaction pass over unsafe ones.
///
/// INCLUSION RULE — a line is included only if BOTH:
///   1. It starts with the exact "[yyyy-MM-dd HH:mm:ss.fff] " timestamp
///      bracket OcrLogger.LogTiming/LogRead/LogError all prefix their own
///      lines with. Raw OCR text bodies and the JSON word-dump line carry
///      NO such prefix (they're appended as plain sb.AppendLine(rawText) /
///      AppendLine(json) calls, see OcrLogger.LogRead) — so this alone
///      already excludes them.
///   2. AFTER stripping that prefix, the remainder starts with one of
///      exactly two known-safe tags:
///        - "Timing: " — RxLogFormatter.FormatTimingLine's own output,
///          documented and tested as "pure millisecond counts and
///          on/off/hit flags... needs none [of the redaction machinery]".
///        - "[RIGHTCLICK-DIAG]" — the right-click/report-dialog diagnostic
///          trail (MainWindow.xaml.cs, Integrated/IntegratedBoxesWindow.
///          xaml.cs). Every call site was audited: they interpolate only
///          FieldKey (one of Models/EngineModels.cs FieldOrder.Fields,
///          e.g. "drug"/"quantity" — an identifier, never a value),
///          booleans, ints, and static text — EXCEPT one line that
///          interpolates a full exception ("EXCEPTION constructing/
///          showing dialog: {ex}"); that one is excluded explicitly below
///          even though nothing on this call path ever puts field VALUES
///          into an exception, per "when in doubt, exclude the line".
///
/// Everything else default-DENIES, including two OcrLogger.LogTiming call
/// sites that superficially look like plain diagnostics but were
/// deliberately left off the allowlist: OrderAssist/
/// OrderAssistCoordinator.cs's "OrderAssist: no target window matched..."
/// line echoes arbitrary VISIBLE PIONEERRX WINDOW TITLES verbatim — that
/// class's own doc already flags this as a PHI caveat (an Edit-Rx window's
/// title bar can carry a patient name) — and its "OrderAssist[...]: column
/// resolution failed..." sibling echoes OCR-read table header labels,
/// which aren't provably PHI-free the way a fixed FieldKey is. Both stay
/// excluded rather than special-cased safe.
/// </summary>
public static class LogTailBuilder
{
    /// <summary>Default cap on the number of SAFE lines kept (most recent), independent of the byte cap below.</summary>
    public const int DefaultMaxLines = 80;

    /// <summary>Default cap on the built tail's UTF-8 byte size — trims from the FRONT (oldest first) so the most recent lines always survive.</summary>
    public const int DefaultMaxBytes = 16 * 1024;

    private static readonly Regex TimestampPrefix = new(
        @"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\] ",
        RegexOptions.Compiled);

    /// <summary>
    /// Filters <paramref name="allLines"/> (the day file's full line list,
    /// oldest first — see OcrLogger.TryReadAllLines) down to the safe
    /// allowlist, keeps at most the last <paramref name="maxLines"/> of
    /// those, then trims further from the front if still over
    /// <paramref name="maxBytes"/>. Returns "" (never null) for no safe
    /// lines / no input, so callers can treat an empty logTail the same as
    /// a null one.
    /// </summary>
    public static string BuildSafeTail(IEnumerable<string> allLines, int maxLines = DefaultMaxLines, int maxBytes = DefaultMaxBytes)
    {
        var safeLines = allLines.Where(IsSafeLine).ToList();
        if (safeLines.Count == 0) return "";

        var tail = safeLines.Count <= maxLines
            ? safeLines
            : safeLines.GetRange(safeLines.Count - maxLines, maxLines);

        while (tail.Count > 0 && Encoding.UTF8.GetByteCount(string.Join("\n", tail)) > maxBytes)
        {
            tail.RemoveAt(0);
        }

        return string.Join("\n", tail);
    }

    /// <summary>True for a line this report is allowed to leave the workstation with — see class doc's INCLUSION RULE.</summary>
    public static bool IsSafeLine(string line)
    {
        if (string.IsNullOrEmpty(line)) return false;

        var match = TimestampPrefix.Match(line);
        if (!match.Success) return false;

        var rest = line.Substring(match.Length);

        if (rest.StartsWith("Timing: ", StringComparison.Ordinal)) return true;

        if (rest.StartsWith("[RIGHTCLICK-DIAG]", StringComparison.Ordinal))
        {
            return rest.IndexOf("EXCEPTION", StringComparison.Ordinal) < 0;
        }

        return false;
    }
}
