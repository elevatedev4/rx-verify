using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RxVerifyOverlay.Diagnostics;

/// <summary>
/// Formats one RxLogSnapshot into the single copy-pasteable text blob
/// behind the "Copy logs (no HIPAA)" button (MainWindow.xaml/.cs
/// OnCopyLogsNoHipaaClick) — per Will's ask: everything needed to debug
/// the CURRENT Rx (raw OCR text + word geometry, parsed/mapped fields,
/// match verdicts, warnings/errors) in one clipboard copy, nothing
/// accumulated across scripts. A pure function of its input (no
/// file/clipboard/UI access here) so it's directly unit-testable without
/// standing up a whole OverlayViewModel — see
/// RxVerifyOverlay.Tests/RxLogFormatterTests.cs.
///
/// <see cref="BuildLogBlob(RxLogSnapshot, bool)"/>'s <c>redactPatient</c>
/// flag backs that same "Copy logs (no HIPAA)" button (OverlayViewModel.
/// BuildCurrentLogBlob(redactPatient: true)): it strips every patient
/// identifier (name, DOB, address, and their appearance in the Rx-window
/// title and raw OCR text/word list) while keeping prescriber/drug/sig/
/// quantity/refills/dates and the OCR geometry, so a real prescription's
/// log can be pasted for debugging without exposing PHI. Redaction is
/// deliberately over-inclusive: a patient token that also happens to
/// appear in prescriber context (e.g. a shared surname) gets scrubbed
/// everywhere, and a final full-blob pass re-applies the scrub as a
/// safety net. 2026-08-13 (RXVERIFY-TROUBLESHOOT): the plain,
/// PHI-including "Copy logs" button (redactPatient: false) was removed
/// from the UI — this formatter itself is unchanged and still supports
/// redactPatient: false for completeness/tests, it's just no longer
/// reachable from any button.
/// </summary>
public static class RxLogFormatter
{
    /// <summary>
    /// Was private; made public (2026-08-13, verdict-tooltips-reports
    /// branch) so Reporting/RxReportBuilder.cs can redact a patient
    /// field's Source/Entered value the SAME way this file already does,
    /// instead of re-declaring its own "[redacted]" literal — see that
    /// class's "NO patient fields in the payload" doc.
    /// </summary>
    public const string RedactedValue = "[redacted]";
    private const string RedactedTitleSuffix = "[patient redacted]";

    private static readonly HashSet<string> PatientFieldKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "patientName", "patientDOB", "patientAddress"
    };

    /// <summary>
    /// True for the 3 patient-identity fields (patientName/patientDOB/
    /// patientAddress) — the same set BuildLogBlob's redactPatient:true
    /// scrubs. Exposed publicly (2026-08-13, verdict-tooltips-reports
    /// branch) so any OTHER feature that must never leak a patient
    /// field's raw value off this workstation — see
    /// Reporting/RxReportBuilder.cs and Integrated/VerdictFieldInfo.cs —
    /// reuses this one canonical list instead of maintaining a second copy
    /// that could drift from it.
    /// </summary>
    public static bool IsPatientField(string fieldKey) => PatientFieldKeys.Contains(fieldKey);

    /// <summary>
    /// Latency fix (Will's field report — verdicts noticeably slower
    /// than the OCR pipeline alone suggested): one compact line spelling
    /// out where a refresh's time actually went, e.g.
    /// "Timing: detect-&gt;render 612ms (attach 3 [hit] + uia 62 [find 4 +
    /// read 58] + capture 56 [region 3 + hidewait 0 + blit 53] + ocr 105
    /// + engine 180 + render 8) - phase2 +240ms · exclusion on".
    ///
    /// The "attach N [hit|resolve]" and "uia N [find X + read Y]"
    /// breakdowns are the uia-read-latency branch's diagnosis follow-up:
    /// attach and uia together used to run 2.5-3.8s per refresh
    /// (FieldReader.ReadEntered re-walking the UIA tree from scratch for
    /// all ~14 entered fields on every single call, and TryAttach
    /// constructing a brand-new UIA3Automation + re-enumerating every
    /// top-level window every call) with no visibility into which part
    /// was actually slow. "[hit]" means PioneerRxWindow.TryAttach reused
    /// the already-resolved window (see AttachCacheDecision) instead of
    /// re-resolving from scratch; "[resolve]" means it didn't. "find" is
    /// cumulative time doing fresh FindFirstDescendant walks across all
    /// entered fields (near-zero once every field's element is cached
    /// for this window — see Uia/EnteredFieldElementCache.cs); "read" is
    /// cumulative time re-reading each field's CURRENT value, which
    /// isn't eliminated by caching (values are never cached, only
    /// element references — see FieldReader.cs's safety doc) and so is
    /// uia's floor even on an all-cache-hit refresh.
    ///
    /// The bracketed region/hidewait/blit breakdown of "capture" is an
    /// earlier latency-fix diagnosis (capture-latency branch): see
    /// Ocr/EscriptImageCapture.cs / Uia/OcrFieldReader.cs / MainWindow.
    /// xaml.cs for the region-cache and SetWindowDisplayAffinity fixes
    /// that target it.
    ///
    /// The trailing "· exclusion on|off" (post-review diagnostic-
    /// visibility fix, <paramref name="captureExclusionActive"/>, omitted
    /// entirely when null) says whether SetWindowDisplayAffinity(
    /// WDA_EXCLUDEFROMCAPTURE) is the reason hide-wait is ~0, or whether
    /// the hide/show fallback ran instead — see
    /// IOverlayVisibilityController.IsExcludedFromCapture.
    ///
    /// Shared by Ocr/OcrLogger.cs's per-day log file and the "Copy logs"
    /// blob (BuildLogBlob below) so the two surfaces can never drift on
    /// format. No patient/prescriber/drug content — pure millisecond
    /// counts and on/off/hit flags — so unlike the rest of this file's
    /// redaction machinery, this needs none.
    /// </summary>
    public static string FormatTimingLine(RefreshTiming timing, bool? captureExclusionActive = null)
    {
        var attachTag = timing.AttachCacheHit is { } attachCacheHit ? $" [{(attachCacheHit ? "hit" : "resolve")}]" : "";

        var line = $"Timing: detect->render {timing.Phase1TotalMs}ms " +
                   $"(attach {timing.AttachMs}{attachTag} + " +
                   $"uia {timing.UiaMs} [find {timing.UiaFindMs} + read {timing.UiaReadMs}] + " +
                   $"capture {timing.CaptureMs} [region {timing.CaptureRegionResolveMs} + hidewait {timing.CaptureHideWaitMs} + blit {timing.CaptureBlitMs}] + " +
                   $"ocr {timing.OcrMs} + engine {timing.EngineMs} + render {timing.RenderMs})";

        if (timing.Phase2Ms is { } phase2Ms)
        {
            line += $" - phase2 +{phase2Ms}ms";
        }

        if (captureExclusionActive is { } exclusionActive)
        {
            line += $" · exclusion {(exclusionActive ? "on" : "off")}";
        }

        return line;
    }

    public static string BuildLogBlob(RxLogSnapshot s) => BuildLogBlob(s, redactPatient: false);

    public static string BuildLogBlob(RxLogSnapshot s, bool redactPatient)
    {
        PatientScrubContext? scrub = redactPatient ? BuildPatientScrubContext(s) : null;

        var sb = new StringBuilder();

        sb.AppendLine("=== Rx Verify — copied log ===");
        sb.AppendLine($"Captured: {s.CapturedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"App version: {s.AppVersion}    Commit: {s.CommitSha}");
        // RXVERIFY-TROUBLESHOOT 2026-08-13: distinct from Commit above --
        // that describes the C# overlay checkout, this describes the
        // separate Node engine subprocess's dist/cli.js (see
        // RxLogSnapshot.EngineBuildSha's doc for why those two can
        // drift). Omitted entirely (not printed as "unknown unknown")
        // when the handshake never happened at all.
        if (!string.IsNullOrEmpty(s.EngineBuildSha) || !string.IsNullOrEmpty(s.EngineBuildBuiltAt))
        {
            sb.AppendLine($"Engine build: {s.EngineBuildSha ?? "unknown"} {s.EngineBuildBuiltAt ?? "unknown"}");
        }
        sb.AppendLine($"Method: {s.Method}");
        if (!string.IsNullOrEmpty(s.RxWindowTitle))
        {
            var titleLine = scrub is null
                ? s.RxWindowTitle
                : string.IsNullOrEmpty(scrub.TitleKeep)
                    ? RedactedTitleSuffix
                    : $"{scrub.TitleKeep} - {RedactedTitleSuffix}";
            sb.AppendLine($"Rx window: {titleLine}");
        }
        sb.AppendLine($"Status: {s.StatusMessage}");
        if (s.Timing is not null)
        {
            sb.AppendLine(FormatTimingLine(s.Timing, s.CaptureExclusionActive));
        }
        sb.AppendLine();

        sb.AppendLine("--- Verdicts ---");
        sb.AppendLine($"Summary: {s.GreenCount} green / {s.YellowCount} yellow / {s.RedCount} red");
        foreach (var category in s.Categories)
        {
            sb.AppendLine($"[{category.Name} — {category.StatusText}]");
            if (category.Rows.Count == 0)
            {
                sb.AppendLine("  (no data)");
                continue;
            }

            foreach (var row in category.Rows)
            {
                sb.AppendLine($"  {row.DisplayName} ({row.FieldKey}): {row.Status}");
                var isPatientField = scrub is not null && PatientFieldKeys.Contains(row.FieldKey);
                var sourceValue = isPatientField ? RedactedValue : row.SourceValue;
                var enteredValue = isPatientField ? RedactedValue : row.EnteredValue;
                sb.AppendLine($"    source=\"{sourceValue}\"  entered=\"{enteredValue}\"");
                if (!string.IsNullOrEmpty(row.ReasonCode) || !string.IsNullOrEmpty(row.Explanation))
                {
                    sb.AppendLine($"    reason=[{row.ReasonCode}] {row.Explanation}");
                }
            }
        }
        sb.AppendLine();

        if (s.Notes.Count > 0)
        {
            sb.AppendLine("--- E-script notes ---");
            foreach (var note in s.Notes)
            {
                sb.AppendLine($"  {note}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("--- OCR ---");
        sb.AppendLine(string.IsNullOrEmpty(s.OcrStatusText) ? "(no OCR read yet)" : s.OcrStatusText);
        if (!string.IsNullOrEmpty(s.RawOcrText))
        {
            sb.AppendLine("Raw OCR text:");
            sb.AppendLine(scrub is null ? s.RawOcrText : ScrubText(s.RawOcrText, scrub));
        }
        if (s.OcrWords is { Count: > 0 })
        {
            sb.AppendLine($"OCR words ({s.OcrWords.Count}), text @ (x, y, w, h):");
            foreach (var word in s.OcrWords)
            {
                var text = scrub is null ? word.Text : ScrubText(word.Text, scrub);
                sb.AppendLine($"  \"{text}\" @ ({word.X:0}, {word.Y:0}, {word.W:0}, {word.H:0})");
            }
        }

        var result = sb.ToString();
        if (scrub is null) return result;

        // Belt-and-suspenders final pass: re-apply the patient scrub to the
        // ENTIRE assembled blob (not just the sections built from raw OCR
        // above), so nothing patient-identifying can survive regardless of
        // which section it leaked in from. OCR word coordinate tuples
        // ("@ (x, y, w, h)") are protected from this pass first — they're
        // geometry, not text, and must survive even when a coordinate
        // number happens to numerically match a patient digit token (e.g.
        // an X of 506 coinciding with a phone exchange).
        // Also protects verdict category/row header and reason lines from
        // this pass: a field's DisplayName/Status/ReasonCode is explicitly
        // NOT PHI (the per-row redaction above already blanks only
        // source/entered), but a label word like "DOB" or "Phone" can
        // collide with a patient token absorbed from the Rx-window
        // title's own "DOB: ..." / "Phone: ..." labels.
        // Also protects the latency-fix "Timing: ..." line (see
        // FormatTimingLine above) — it's pure millisecond counts, never
        // PHI, but a 3+ digit ms value could coincidentally match a
        // patient digit token (e.g. a street number) and get blanked out
        // for no reason, which would defeat the whole point of the line.
        var protectedPattern = new Regex(
            @"@ \([^)]*\)" +
            @"|^\[.+? — .+?\]$" +
            @"|^  .+? \([A-Za-z0-9]+\): \w+$" +
            @"|^    reason=\[[^\]]*\].*$" +
            @"|^Timing: .*$",
            RegexOptions.Multiline);

        var protectedSpans = new List<string>();
        var withPlaceholders = protectedPattern.Replace(result, m =>
        {
            protectedSpans.Add(m.Value);
            return $"\0PROTECTEDSPAN{protectedSpans.Count - 1}\0";
        });

        var scrubbed = ScrubText(withPlaceholders, scrub);

        for (var i = 0; i < protectedSpans.Count; i++)
        {
            scrubbed = scrubbed.Replace($"\0PROTECTEDSPAN{i}\0", protectedSpans[i]);
        }

        return scrubbed;
    }

    /// <summary>Everything needed to scrub patient info out of arbitrary text — built once per BuildLogBlob call from whichever fields carry PHI.</summary>
    private sealed class PatientScrubContext
    {
        public required string TitleKeep { get; init; }
        public required HashSet<string> Tokens { get; init; }
        public required HashSet<string> DigitRuns { get; init; }
        public required IReadOnlyList<string> LiteralPhrases { get; init; }
    }

    private static PatientScrubContext BuildPatientScrubContext(RxLogSnapshot s)
    {
        var allRows = s.Categories.SelectMany(c => c.Rows).ToList();

        var drugEntered = allRows
            .FirstOrDefault(r => string.Equals(r.FieldKey, "drug", StringComparison.OrdinalIgnoreCase))
            ?.EnteredValue;

        var (titleKeep, titlePatientPortion) = SplitTitleForRedaction(s.RxWindowTitle ?? "", drugEntered);

        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var digitRuns = new HashSet<string>(StringComparer.Ordinal);
        var literalPhrases = new List<string>();

        void AbsorbForTokensAndDigitRuns(string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            foreach (var tok in Tokenize(value)) tokens.Add(tok);
            AbsorbDigitRuns(value, digitRuns);
        }

        foreach (var row in allRows.Where(r => PatientFieldKeys.Contains(r.FieldKey)))
        {
            AbsorbForTokensAndDigitRuns(row.SourceValue);
            AbsorbForTokensAndDigitRuns(row.EnteredValue);

            // The DOB is a compact numeric phrase ("10/03/1988") whose
            // individual day/month components (e.g. "03") are too generic
            // to safely add as standalone redaction tokens — they'd
            // collide with unrelated dates elsewhere in the log (e.g. a
            // dateWritten of "03/03/2026" sharing the month "03"), which
            // would violate "dates are KEPT". Scrubbing token-by-token
            // would also risk leaving a fragment behind (e.g. only the
            // year redacted: "10/03/[redacted]"). So the DOB is instead
            // scrubbed as a whole literal phrase — every format it was
            // captured in (source AND entered) is matched and replaced
            // verbatim, guaranteeing no fragment survives.
            if (string.Equals(row.FieldKey, "patientDOB", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(row.SourceValue)) literalPhrases.Add(row.SourceValue);
                if (!string.IsNullOrWhiteSpace(row.EnteredValue)) literalPhrases.Add(row.EnteredValue);
            }
        }

        AbsorbForTokensAndDigitRuns(titlePatientPortion);

        return new PatientScrubContext
        {
            TitleKeep = titleKeep,
            Tokens = tokens,
            DigitRuns = digitRuns,
            LiteralPhrases = literalPhrases
        };
    }

    /// <summary>
    /// Splits the Rx window title into a safe-to-keep prefix (Rx number +
    /// drug) and the patient-identifying remainder. The drug's ENTERED
    /// value is used as the split point because the title always places
    /// the patient portion immediately after the drug. If the drug text
    /// can't be located in the title (format changed, drug missing,
    /// etc.), falls back to keeping only the leading "Edit Rx - {number}"
    /// segment and treating everything else as patient content — erring
    /// toward removing more.
    /// </summary>
    private static (string TitleKeep, string PatientPortion) SplitTitleForRedaction(string title, string? drugEntered)
    {
        if (string.IsNullOrEmpty(title)) return ("", "");

        if (!string.IsNullOrEmpty(drugEntered))
        {
            var idx = title.IndexOf(drugEntered, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var endIdx = idx + drugEntered.Length;
                return (title.Substring(0, endIdx), title.Substring(endIdx));
            }
        }

        var segments = title.Split(" - ");
        if (segments.Length >= 2)
        {
            var keep = string.Join(" - ", segments.Take(2));
            return (keep, title.Length > keep.Length ? title.Substring(keep.Length) : "");
        }

        return ("", title);
    }

    /// <summary>
    /// Collects digit runs (length &gt;= 3) out of <paramref name="value"/>,
    /// and adds each run (and concatenations of 2-3 adjacent runs) of
    /// length &gt;= 5 to <paramref name="digitRuns"/>. This catches merged
    /// alphanumeric OCR tokens like "KS660471615" (state + zip glued
    /// together with no separator) via a CONTAINS check in <see cref="ScrubText"/>,
    /// even when the merged token itself never appears verbatim in any
    /// single patient field value.
    /// </summary>
    private static void AbsorbDigitRuns(string value, HashSet<string> digitRuns)
    {
        var runs = Regex.Matches(value, @"\d+")
            .Select(m => m.Value)
            .Where(r => r.Length >= 3)
            .ToList();

        for (var i = 0; i < runs.Count; i++)
        {
            if (runs[i].Length >= 5)
            {
                digitRuns.Add(runs[i]);

                // A run of 6+ digits is commonly a zip+4 (or similar)
                // glued to something else with no separator (the sample's
                // "KS660471615" = state + zip 66047 + ext 1615). Also
                // register its leading 5 digits so a standalone 5-digit
                // zip appearing elsewhere (e.g. echoed in a DIFFERENT
                // field/row, such as a prescriber address that happens to
                // share the patient's zip code) is caught too — over-
                // redaction of a shared zip is preferred over a miss.
                if (runs[i].Length > 5) digitRuns.Add(runs[i].Substring(0, 5));
            }

            if (i + 1 < runs.Count)
            {
                var combo2 = runs[i] + runs[i + 1];
                if (combo2.Length >= 5) digitRuns.Add(combo2);
            }

            if (i + 2 < runs.Count)
            {
                var combo3 = runs[i] + runs[i + 1] + runs[i + 2];
                if (combo3.Length >= 5) digitRuns.Add(combo3);
            }
        }
    }

    /// <summary>
    /// Tokenizes on whitespace AND punctuation (anything that isn't
    /// ASCII letters/digits), lowercases, and drops short tokens — except
    /// digit tokens are kept down to length 3 (not 1-2): a bare "3" or
    /// "03" (e.g. a DOB day/month split out by punctuation) is too
    /// generic to use as a standalone redaction token without colliding
    /// with unrelated dates/quantities elsewhere in the log (see the DOB
    /// literal-phrase handling above, which covers that case fully
    /// instead). Digit runs of length 3+ (area codes, street numbers,
    /// zips) are specific enough to be safe.
    /// </summary>
    private static IEnumerable<string> Tokenize(string? value)
    {
        if (string.IsNullOrEmpty(value)) yield break;

        foreach (Match m in Regex.Matches(value, "[A-Za-z0-9]+"))
        {
            var tok = m.Value.ToLowerInvariant();
            var isDigits = tok.All(char.IsDigit);
            var minLength = isDigits ? 3 : 2;
            if (tok.Length < minLength) continue;
            yield return tok;
        }
    }

    private static string ScrubText(string text, PatientScrubContext scrub)
    {
        if (string.IsNullOrEmpty(text)) return text;

        foreach (var phrase in scrub.LiteralPhrases)
        {
            if (string.IsNullOrWhiteSpace(phrase)) continue;
            text = Regex.Replace(text, Regex.Escape(phrase), RedactedValue, RegexOptions.IgnoreCase);
        }

        return Regex.Replace(text, "[A-Za-z0-9]+", m =>
        {
            var raw = m.Value;
            var norm = raw.ToLowerInvariant();
            if (scrub.Tokens.Contains(norm)) return RedactedValue;

            var digits = new string(raw.Where(char.IsDigit).ToArray());
            if (digits.Length >= 5 && scrub.DigitRuns.Any(digits.Contains))
            {
                return RedactedValue;
            }

            return raw;
        });
    }
}
