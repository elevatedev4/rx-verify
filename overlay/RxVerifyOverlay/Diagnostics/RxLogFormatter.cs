using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RxVerifyOverlay.Diagnostics;

/// <summary>
/// Formats one RxLogSnapshot into the single copy-pasteable text blob
/// behind the "Copy logs" button (MainWindow.xaml/.cs OnCopyLogsClick) —
/// per Will's ask: everything needed to debug the CURRENT Rx (raw OCR
/// text + word geometry, parsed/mapped fields, match verdicts, warnings/
/// errors) in one clipboard copy, nothing accumulated across scripts. A
/// pure function of its input (no file/clipboard/UI access here) so it's
/// directly unit-testable without standing up a whole OverlayViewModel —
/// see RxVerifyOverlay.Tests/RxLogFormatterTests.cs.
///
/// <see cref="BuildLogBlob(RxLogSnapshot, bool)"/>'s <c>redactPatient</c>
/// flag backs the "Copy logs (no HIPAA)" button (OverlayViewModel.
/// BuildCurrentLogBlob(redactPatient: true) / MainWindow.xaml.cs
/// OnCopyLogsNoHipaaClick): it strips every patient identifier (name,
/// DOB, address, and their appearance in the Rx-window title and raw OCR
/// text/word list) while keeping prescriber/drug/sig/quantity/refills/
/// dates and the OCR geometry, so a real prescription's log can be
/// pasted for debugging without exposing PHI. Redaction is deliberately
/// over-inclusive: a patient token that also happens to appear in
/// prescriber context (e.g. a shared surname) gets scrubbed everywhere,
/// and a final full-blob pass re-applies the scrub as a safety net.
/// </summary>
public static class RxLogFormatter
{
    private const string RedactedValue = "[redacted]";
    private const string RedactedTitleSuffix = "[patient redacted]";

    private static readonly HashSet<string> PatientFieldKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "patientName", "patientDOB", "patientAddress"
    };

    public static string BuildLogBlob(RxLogSnapshot s) => BuildLogBlob(s, redactPatient: false);

    public static string BuildLogBlob(RxLogSnapshot s, bool redactPatient)
    {
        PatientScrubContext? scrub = redactPatient ? BuildPatientScrubContext(s) : null;

        var sb = new StringBuilder();

        sb.AppendLine("=== Rx Verify — copied log ===");
        sb.AppendLine($"Captured: {s.CapturedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"App version: {s.AppVersion}    Commit: {s.CommitSha}");
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
        var protectedPattern = new Regex(
            @"@ \([^)]*\)" +
            @"|^\[.+? — .+?\]$" +
            @"|^  .+? \([A-Za-z0-9]+\): \w+$" +
            @"|^    reason=\[[^\]]*\].*$",
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
