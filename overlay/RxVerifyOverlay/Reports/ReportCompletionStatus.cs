using System;
using System.Collections.Generic;

namespace RxVerifyOverlay.Reports;

/// <summary>
/// W-T92 round 7 addition (Will, 2026-09-30, verbatim: "when saving the
/// report, it needs to wait until it is run. The window will show 'Please
/// wait while the report is generated...' in the bottom left and then
/// change to 'The report has completed' when it is done. The app needs to
/// watch for this and not try to save the report until that has happened,
/// otherwise the report will save empty."). One poll tick's outcome for
/// PioneerReportDriver.WaitForReportCompletionStatus, the shared wait
/// RunFinancialReport runs right before its Export/Save step for every
/// enabled DateRange/AsOfDate report (see ReportCatalog.All — Payments
/// never reaches this popup at all, so it's excluded by construction, not
/// a special case).
/// </summary>
public enum ReportCompletionDecision
{
    /// <summary>Status text hasn't reached "completed" yet and the timeout hasn't elapsed — poll again.</summary>
    KeepWaiting,

    /// <summary>Status text reads "The report has completed" — safe to proceed to Export/Save.</summary>
    Proceed,

    /// <summary>Elapsed time reached the timeout without ever seeing "completed" — abort the save, never write an empty report.</summary>
    TimedOut
}

/// <summary>
/// Pure (status text, elapsed, timeout) -&gt; ReportCompletionDecision logic,
/// plus the two phrase-matching helpers PioneerReportDriver's own UIA
/// search and logging use — no FlaUI/UIA dependency, so the actual
/// decision is unit tested (RxVerifyOverlay.Tests/Reports/
/// ReportCompletionStatusTests.cs) without a live Pioneer window.
/// PioneerReportDriver.WaitForReportCompletionStatus is the impure poll
/// loop (every ~250ms, up to 180s) that reads Pioneer's own bottom-left
/// status text and feeds it through Decide.
/// </summary>
public static class ReportCompletionStatus
{
    /// <summary>The bottom-left status text Pioneer shows while a report is still rendering — Will's own phrase, matched case-insensitively and by Contains (never exact-equals), since the live control may carry a trailing ellipsis/percentage/other characters around it that this driver has no confirmed UIA dump of yet.</summary>
    public const string GeneratingPhrase = "please wait while the report is generated";

    /// <summary>The bottom-left status text once the report has fully rendered — the ONLY text that is safe to proceed to the save step on.</summary>
    public const string CompletedPhrase = "the report has completed";

    public static bool IsCompleted(string? statusText) =>
        !string.IsNullOrEmpty(statusText) && statusText.Contains(CompletedPhrase, StringComparison.OrdinalIgnoreCase);

    public static bool IsGenerating(string? statusText) =>
        !string.IsNullOrEmpty(statusText) && statusText.Contains(GeneratingPhrase, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Proceed fires ONLY on a status text that actually contains
    /// CompletedPhrase — a null/empty readback, one that still reads the
    /// generating phrase, or one that reads something this driver doesn't
    /// recognize at all is never itself treated as "done"; it's KeepWaiting
    /// until <paramref name="elapsed"/> reaches <paramref name="timeout"/>,
    /// at which point it's TimedOut instead. This is why a status element
    /// that can never be found at all (PioneerReportDriver logs that case
    /// and dumps diagnostics once, per its own doc) still safely waits out
    /// the full timeout rather than silently proceeding — the caller must
    /// see an actual "completed" reading before it ever saves.
    /// </summary>
    public static ReportCompletionDecision Decide(string? statusText, TimeSpan elapsed, TimeSpan timeout)
    {
        if (IsCompleted(statusText)) return ReportCompletionDecision.Proceed;
        if (elapsed >= timeout) return ReportCompletionDecision.TimedOut;
        return ReportCompletionDecision.KeepWaiting;
    }

    /// <summary>
    /// Reviewer round 7 non-blocking fix (PioneerReportDriver.
    /// FindStatusPhraseInWindow): a single poll of a live window can turn
    /// up more than one Text/StatusBar descendant whose Name matches one
    /// of the two phrases — a UIA tree offers no guarantee about which
    /// order those come back in. A Completed reading must always win over
    /// a Generating one regardless of enumeration order (a report that has
    /// actually finished is never re-treated as "still generating" just
    /// because some other element's stale text happened to be walked
    /// first), so this scans <paramref name="candidateTexts"/> for the
    /// first CompletedPhrase match before ever considering a Generating
    /// one. Null if none of the candidates match either phrase — the
    /// caller (WaitForReportCompletionStatus, via Decide) treats that the
    /// same as "not found yet", never as "done".
    /// </summary>
    public static string? SelectStatusText(IEnumerable<string?> candidateTexts)
    {
        string? generatingFallback = null;

        foreach (var candidate in candidateTexts)
        {
            if (IsCompleted(candidate)) return candidate;
            if (generatingFallback is null && IsGenerating(candidate)) generatingFallback = candidate;
        }

        return generatingFallback;
    }
}
