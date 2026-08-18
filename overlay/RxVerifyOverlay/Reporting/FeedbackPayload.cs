using System;

namespace RxVerifyOverlay.Reporting;

/// <summary>
/// Wire shape POSTed to HQ's /api/rxverify-feedback endpoint (branch
/// fix/rightclick-all-feedback-compact, task 2) — a pharmacist's free-form
/// note about the app itself, NOT tied to any one field's verdict (that's
/// RxReportPayload's job, via /api/rxverify-reports — a deliberately
/// separate, parallel endpoint/payload/submitter pair rather than
/// overloading the report shape with an "isFeedback" flag). CamelCase on
/// the wire via FeedbackSubmitter's JsonSerializerOptions — same convention
/// as RxReportPayload/Engine/EngineClient.cs's wire types.
///
/// Deliberately carries NO field/verdict/log-tail data at all — Message is
/// free text the pharmacist chose to type, which is fine (deliberate user
/// speech, per the branch brief), but nothing captured from the screen or
/// OcrLogger's log file is ever auto-attached to it, unlike RxReportPayload
/// (see that class's LogTail).
/// </summary>
public sealed class FeedbackPayload
{
    public string App { get; set; } = "rx-verify";

    /// <summary>"&lt;sha&gt; &lt;builtAt&gt;" of the TypeScript engine subprocess — see RxReportPayload.EngineBuild's doc, same shape, same source (MainWindow.xaml.cs ResolveEngineBuildString).</summary>
    public string? EngineBuild { get; set; }

    /// <summary>The C# overlay's own git commit (AppDiagnostics.GetCommitSha()) — see RxReportPayload.Commit's doc.</summary>
    public string? Commit { get; set; }

    /// <summary>The pharmacist's free-text feedback — never redacted (there is no field/patient context here to redact from).</summary>
    public string Message { get; set; } = "";

    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Pure builder for FeedbackPayload — mirrors Reporting/RxReportPayload.cs's
/// RxReportBuilder split (no I/O, no WPF dependency, fully unit-testable —
/// see RxVerifyOverlay.Tests/FeedbackBuilderTests.cs) even though this
/// payload is much simpler and needs no redaction of its own.
/// </summary>
public static class FeedbackBuilder
{
    public static FeedbackPayload Build(string message, string? engineBuild, string? commit, DateTime createdAtUtc) =>
        new()
        {
            App = "rx-verify",
            EngineBuild = engineBuild,
            Commit = commit,
            Message = message ?? "",
            CreatedAt = createdAtUtc
        };
}
