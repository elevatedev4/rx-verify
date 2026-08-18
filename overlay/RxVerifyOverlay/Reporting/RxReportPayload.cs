using System;
using RxVerifyOverlay.Diagnostics;
using RxVerifyOverlay.Integrated;

namespace RxVerifyOverlay.Reporting;

/// <summary>
/// Wire shape POSTed to HQ's dedicated /api/rxverify-reports endpoint (see
/// HQ-ENDPOINT-SPEC.md at the repo root) — one pharmacist-submitted
/// correction against a single field's verdict, plus enough context (app,
/// engine build, overlay commit) for whoever triages it on the HQ side to
/// know exactly which build produced the verdict being reported. CamelCase
/// on the wire via Reporting/RxReportSubmitter.cs's JsonSerializerOptions —
/// same convention as Engine/EngineClient.cs's wire types. Also the JSONL
/// record shape for Reporting/PendingReportsQueue.cs's store-and-forward
/// file (one of these, serialized, per line).
/// </summary>
public sealed class RxReportPayload
{
    public string App { get; set; } = "rx-verify";

    /// <summary>"&lt;sha&gt; &lt;builtAt&gt;" of the TypeScript engine subprocess (see Engine/EngineClient.cs EngineBuildSha/EngineBuildBuiltAt), or null if the engine's --serve handshake never happened / was malformed — same "omit rather than print unknown" posture as RxLogFormatter's "Engine build:" line.</summary>
    public string? EngineBuild { get; set; }

    /// <summary>The C# overlay's own git commit (AppDiagnostics.GetCommitSha()) — distinct from EngineBuild for the same reason RxLogFormatter's header keeps them on separate lines (the two can drift).</summary>
    public string? Commit { get; set; }

    /// <summary>One of Models/EngineModels.cs FieldOrder.Fields, e.g. "drug", "quantity".</summary>
    public string Field { get; set; } = "";

    /// <summary>The engine's SourceValue for this field — "[redacted]" (RxLogFormatter.RedactedValue) instead of the real value when Field is a patient-identity field. See RxReportBuilder.</summary>
    public string? Source { get; set; }

    /// <summary>The engine's EnteredValue for this field — same redaction rule as Source.</summary>
    public string? Entered { get; set; }

    /// <summary>Lowercase verdict color word ("green"/"yellow"/"red") — matches the engine's own JSON enum casing (Engine/EngineClient.cs JsonOptions), NOT the HQ-side report lifecycle status (new/accepted/fixed/rejected — see HQ-ENDPOINT-SPEC.md) which is a completely separate concept the server adds.</summary>
    public string Status { get; set; } = "";

    public string? ReasonCode { get; set; }

    public string? Explanation { get; set; }

    /// <summary>The pharmacist's free-text description of what's wrong / what it should have been. Never auto-redacted (see VerdictFieldInfo.IsPatientField's doc for why the whole affordance is hidden for patient fields instead of trying to scrub this).</summary>
    public string Correction { get; set; } = "";

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Diagnostic-only (2026-08-17 fix round, item 2 — Will's live
    /// false-yellow on a "Refill ApprovedWithChanges" renewal response,
    /// refills read "not provided"): which source-reading path produced
    /// the record being reported — "ocr" or "uia" (Models/OverlaySettings.cs
    /// VerificationMethod, lowercased). Lets the NEXT report self-diagnose
    /// which extraction path is actually in play, since the two are
    /// completely different code (Parsing/EscriptTreeParser.cs's UIA tree
    /// walk vs. src/ocr/parseEscriptOcr.ts on the TS side) with different
    /// failure modes. Null only if somehow unresolvable (should not
    /// happen in practice — see Integrated/ReportErrorWindow.xaml.cs).
    /// NOT part of HQ's current zod schema (rxverify-reports.ts) — see
    /// that file's z.object() without .strict()/.passthrough(): unknown
    /// keys are silently STRIPPED, not rejected, so this (and the two
    /// fields below) currently round-trip to HQ but never actually
    /// persist until that schema is extended server-side.
    /// </summary>
    public string? SourceInputMode { get; set; }

    /// <summary>
    /// Refills-specific diagnostic (only ever non-null when Field ==
    /// "refills" — see Integrated/VerdictFieldInfo.cs and Uia/FieldReader.cs
    /// RefillsTotalFillsLabelSeen doc): whether EscriptTreeParser.
    /// DetectTotalFillsLabel found a Total-fills-shaped label ANYWHERE on
    /// the source message, independent of whether ParseRefills could use
    /// it as Refills. Null for every other field, or when the source
    /// input mode wasn't "uia" (the diagnostic doesn't exist on the OCR
    /// path).
    /// </summary>
    public bool? RefillsTotalFillsLabelSeen { get; set; }

    /// <summary>Which FieldMap.TotalFillsKeyPrefixes entry matched, if RefillsTotalFillsLabelSeen — label text only (e.g. "Total fills: "), NEVER the refill count/value itself. Null when RefillsTotalFillsLabelSeen isn't true.</summary>
    public string? RefillsTotalFillsLabelPrefix { get; set; }

    /// <summary>
    /// PHI-safe tail of Ocr/OcrLogger.cs's per-day diagnostic log (2026-08-17
    /// fix round, item — Will verbatim: "Make sure the RxVerify error
    /// reports are sending the logs (from the right click) — the
    /// HIPAA-free logs obviously"), built by
    /// Diagnostics/LogTailBuilder.BuildSafeTail from
    /// Integrated/ReportErrorWindow.xaml.cs's Submit handler — see that
    /// class's doc for the exact line-allowlist rule (timing lines +
    /// right-click diagnostic trail only; every raw-OCR/patient line is
    /// excluded by construction, not by redaction). Capped ~16KB / last
    /// ~80 lines. Null when nothing safe was available to attach (log file
    /// missing/unreadable, or empty after filtering) — never a reason to
    /// block the report itself. Same "currently round-trips to HQ but
    /// doesn't persist until the server schema is extended" caveat as
    /// SourceInputMode/RefillsTotalFillsLabelSeen above (rxverify-reports.ts
    /// silently strips unknown keys).
    /// </summary>
    public string? LogTail { get; set; }
}

/// <summary>
/// Pure builder for RxReportPayload — the ONLY place a VerdictFieldInfo
/// plus a pharmacist's correction turns into the wire/JSONL payload, so
/// the "NO patient fields in the payload" design decision (HQ-ENDPOINT-SPEC.md)
/// can never be bypassed by some future call site that forgets to redact.
/// Belt-and-suspenders alongside the UI-level guard (Integrated/
/// IntegratedBoxesWindow hides "Report error…" entirely for a patient
/// field — see VerdictFieldInfo.IsPatientField): this still redacts
/// Source/Entered even if a future caller somehow reaches this with a
/// patient field's raw values. No I/O, no ViewModel/WPF dependency — see
/// RxVerifyOverlay.Tests/RxReportBuilderTests.cs.
/// </summary>
public static class RxReportBuilder
{
    /// <param name="sourceInputMode">
    /// "ocr" or "uia" — see RxReportPayload.SourceInputMode's doc. Optional
    /// (defaults to null) purely so existing single-purpose call sites/tests
    /// that don't care about this diagnostic don't need updating; the one
    /// real production call site (Integrated/ReportErrorWindow.xaml.cs)
    /// always passes it, derived from OverlaySettings.Method.
    /// </param>
    /// <param name="logTail">
    /// See RxReportPayload.LogTail's doc — already filtered to the PHI-safe
    /// allowlist (Diagnostics/LogTailBuilder.BuildSafeTail) by the caller;
    /// this builder does no I/O and applies no further scrubbing, it just
    /// carries the string through. Optional/null for the same
    /// don't-need-updating reason as sourceInputMode above.
    /// </param>
    public static RxReportPayload Build(VerdictFieldInfo field, string correction, string? engineBuild, string? commit, DateTime createdAtUtc, string? sourceInputMode = null, string? logTail = null)
    {
        var isPatientField = RxLogFormatter.IsPatientField(field.FieldKey);

        return new RxReportPayload
        {
            App = "rx-verify",
            EngineBuild = engineBuild,
            Commit = commit,
            Field = field.FieldKey,
            Source = isPatientField ? RxLogFormatter.RedactedValue : field.SourceValue,
            Entered = isPatientField ? RxLogFormatter.RedactedValue : field.EnteredValue,
            Status = field.Status.ToString().ToLowerInvariant(),
            ReasonCode = field.ReasonCode,
            Explanation = field.Explanation,
            Correction = correction ?? "",
            CreatedAt = createdAtUtc,
            SourceInputMode = sourceInputMode,
            // Already refills-scoped and label-only at the source (see
            // VerdictFieldInfo's doc) — passed straight through, not
            // re-gated by field.FieldKey here, so a future VerdictFieldInfo
            // producer that got the gating wrong fails visibly (wrong data
            // in the payload) rather than silently here too.
            RefillsTotalFillsLabelSeen = field.RefillsTotalFillsLabelSeen,
            RefillsTotalFillsLabelPrefix = field.RefillsTotalFillsLabelPrefix,
            LogTail = string.IsNullOrEmpty(logTail) ? null : logTail
        };
    }
}
