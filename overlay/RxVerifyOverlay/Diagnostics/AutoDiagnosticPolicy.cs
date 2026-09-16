using System;
using System.Collections.Generic;
using System.Linq;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.Uia;

namespace RxVerifyOverlay.Diagnostics;

/// <summary>
/// Which of the refills verdict's possible end-states actually renders a
/// COLOR in the integrated boxes layer (Integrated/BoxColorMapper.cs) — see
/// that class's own doc for the general (every field) version of this
/// same "some states draw no box at all" rule. Enumerated here, by name,
/// specifically for refills (owner report, 2026-09-15: "refills box shows
/// NO colour, neither red nor green"):
///
///   Green — VerdictStatus.Green (compareRefills 'exact_match'). Colored.
///   Red — VerdictStatus.Red (compareRefills 'refills_mismatch'). Colored.
///   NotProvided — reasonCode 'not_provided' (source or entered side
///     empty) — BoxColorMapper.ShouldDrawBox returns false for this
///     reasonCode, so NO box is drawn at all. Uncoloured.
///   UnparseableQuantity — reasonCode 'unparseable_quantity' (a raw value
///     was present on one side but didn't parse as a number — can be
///     either side, not just the OCR/source extraction this branch
///     otherwise hardens) — also excluded by ShouldDrawBox. Uncoloured.
///   NotOnScreen — the row never got an on-screen rectangle at all this
///     tick (VerdictRowViewModel.ScreenRect is null: the entered field
///     wasn't found/readable on screen this refresh) — excluded from the
///     boxes list entirely, upstream of BoxColorMapper, by
///     IntegratedOverlayCoordinator.UpdateBoxes' own
///     `r.ScreenRect.HasValue` filter. Uncoloured — this is the "hidden"
///     case: there's no box because there was nowhere to draw one, not
///     because the color logic hid it.
/// </summary>
public enum RefillsBoxRenderState
{
    Green,
    Red,
    NotProvided,
    UnparseableQuantity,
    NotOnScreen
}

/// <summary>
/// Persisted (JSON, see AutoDiagnosticStateStore) rate-limit bookkeeping
/// for the automatic refills diagnostic report — see AutoDiagnosticPolicy.
/// ShouldReport's doc for the two caps this backs: at most one report per
/// Rx number per (rolling) day, and at most 5 reports total per (rolling)
/// hour on this PC. Plain data, JSON-serializable, so it round-trips
/// through System.Text.Json exactly like every other persisted shape in
/// this app (OverlaySettings, PendingReportsQueue's JSONL rows).
/// </summary>
public sealed class AutoDiagnosticRateLimitState
{
    /// <summary>Rx number -&gt; UTC instant of the last auto-report sent for it. A rolling 24h window (not a calendar-day boundary) — simpler, and avoids a report at 11:59pm/12:01am counting as two different "days" for the same Rx.</summary>
    public Dictionary<string, DateTime> LastReportedUtcByRx { get; set; } = new();

    /// <summary>UTC instants of every auto-report sent in roughly the last hour on this PC — pruned (entries older than 1h dropped) on every ShouldReport call, so this never grows unbounded even across a long-running shift.</summary>
    public List<DateTime> RecentReportTimestampsUtc { get; set; } = new();
}

/// <summary>
/// Pure decision helper — no WPF, no I/O, no OverlayViewModel/engine
/// dependency — for whether the overlay should automatically file one HQ
/// diagnostic report for an uncoloured refills box. See
/// RxVerifyOverlay.Tests/AutoDiagnosticPolicyTests.cs for the full
/// decision-table coverage. Deliberately mirrors the "pure classifier +
/// pure rule, tested without Windows" split every other Integrated/*Rule.cs
/// file in this app already uses (DawBoxRule, PreCheckModeGate, etc).
/// </summary>
public static class AutoDiagnosticPolicy
{
    /// <summary>Per-PC cap — see AutoDiagnosticRateLimitState.RecentReportTimestampsUtc's doc.</summary>
    public const int MaxReportsPerHour = 5;

    private static readonly HashSet<string> UnreadableReasonCodes = new(StringComparer.Ordinal)
    {
        "not_provided",
        "unparseable_quantity"
    };

    /// <summary>
    /// Classifies the refills row's current tick into one of
    /// RefillsBoxRenderState's 5 states — see that enum's own doc for what
    /// each one means and why it either does or doesn't draw a color.
    /// `hasScreenRect` is VerdictRowViewModel.ScreenRect.HasValue for the
    /// refills row; checked FIRST because a row with no screen rect never
    /// reaches BoxColorMapper at all (IntegratedOverlayCoordinator.
    /// UpdateBoxes filters it out before that check ever runs).
    /// </summary>
    public static RefillsBoxRenderState ClassifyRefillsBoxRenderState(VerdictStatus status, string? reasonCode, bool hasScreenRect)
    {
        if (!hasScreenRect) return RefillsBoxRenderState.NotOnScreen;
        if (status == VerdictStatus.Green) return RefillsBoxRenderState.Green;
        if (reasonCode != null && UnreadableReasonCodes.Contains(reasonCode))
        {
            return reasonCode == "not_provided" ? RefillsBoxRenderState.NotProvided : RefillsBoxRenderState.UnparseableQuantity;
        }
        // Every other (status, reasonCode) combination — Red/refills_mismatch,
        // or any future/unmapped reasonCode alongside a non-Green status —
        // is a real color per BoxColorMapper.IsGreenBox/ShouldDrawBox (not
        // in UnreadableReasonCodes), so it renders Red.
        return RefillsBoxRenderState.Red;
    }

    /// <summary>True for the 3 "no box was actually colored" states — see RefillsBoxRenderState's doc. False for Green/Red.</summary>
    public static bool IsUncoloured(RefillsBoxRenderState state) =>
        state is RefillsBoxRenderState.NotProvided or RefillsBoxRenderState.UnparseableQuantity or RefillsBoxRenderState.NotOnScreen;

    /// <summary>
    /// Whether to automatically file ONE refills diagnostic report for
    /// this verification pass, and the rate-limit state to persist
    /// afterward either way (the caller must persist UpdatedState even
    /// when ShouldReport is false — pruning of expired entries still
    /// needs to be saved so the state file doesn't grow forever).
    ///
    /// Gate 1 — must actually be uncoloured (see IsUncoloured); a
    /// Green/Red refills box is working correctly and is never reported.
    ///
    /// Gate 2 — must look like a prescription that should have had
    /// refills at all: either `hasFillWords` (some OCR word on the page
    /// matched /refill|fills?/i) or `isApproval` (the document classifies
    /// as a refill approval/renewal response). Neither means this simply
    /// isn't a refills-bearing document (e.g. a genuinely refill-less new
    /// Rx) — not a bug, nothing to report.
    ///
    /// Gate 3 — per-Rx daily cap: `rxNumber` must not have been reported
    /// in the last 24h (rolling, see AutoDiagnosticRateLimitState's doc).
    /// A null/empty rxNumber (identity unavailable) skips this cap — never
    /// suppresses solely because identity is unknown — but still counts
    /// against gate 4.
    ///
    /// Gate 4 — per-PC hourly cap: at most MaxReportsPerHour reports
    /// total (across every Rx) in the last rolling hour.
    /// </summary>
    public static (bool ShouldReport, AutoDiagnosticRateLimitState UpdatedState) ShouldReport(
        RefillsBoxRenderState refillsState,
        bool hasFillWords,
        bool isApproval,
        AutoDiagnosticRateLimitState? rateLimitState,
        DateTime nowUtc,
        string? rxNumber)
    {
        var state = Clone(rateLimitState);

        // Prune expired bookkeeping FIRST, unconditionally — the caller
        // persists UpdatedState regardless of the boolean outcome, so a
        // long run of "nothing to report" ticks still keeps the file from
        // growing forever.
        state.RecentReportTimestampsUtc = state.RecentReportTimestampsUtc
            .Where(t => nowUtc - t < TimeSpan.FromHours(1))
            .ToList();

        if (!IsUncoloured(refillsState)) return (false, state);
        if (!hasFillWords && !isApproval) return (false, state);

        if (!string.IsNullOrEmpty(rxNumber) &&
            state.LastReportedUtcByRx.TryGetValue(rxNumber, out var lastForRx) &&
            nowUtc - lastForRx < TimeSpan.FromDays(1))
        {
            return (false, state);
        }

        if (state.RecentReportTimestampsUtc.Count >= MaxReportsPerHour) return (false, state);

        state.RecentReportTimestampsUtc.Add(nowUtc);
        if (!string.IsNullOrEmpty(rxNumber)) state.LastReportedUtcByRx[rxNumber] = nowUtc;
        return (true, state);
    }

    /// <summary>
    /// Best-effort document classification for the auto-report note (item
    /// 2 of the brief: "the document classification (new Rx / refill
    /// approval / unknown)"). No dedicated NCPDP message-type field exists
    /// anywhere in this pipeline to classify off of directly, so this
    /// reuses the two real signals already available at the call site
    /// rather than guessing from raw OCR wording:
    ///   - `isApproval` (the SAME flag ShouldReport's gate 2 already
    ///     computed — see its own doc) wins first: it's already evidence
    ///     the source e-script is a refill-response/renewal document.
    ///   - Otherwise, PioneerRx's own screen title classification
    ///     (RxScreenMode.NewRx — see Uia/RxScreenMode.cs) is reused as a
    ///     proxy: the pharmacist has a "New Rx" screen open for this Rx.
    ///   - Neither -&gt; "unknown", never guessed further (same NO-GUESS
    ///     posture as the TS engine's own OCR extraction — see
    ///     src/ocr/parseEscriptOcr.ts class doc).
    /// </summary>
    public static string ClassifyDocument(bool isApproval, RxScreenMode screenMode)
    {
        if (isApproval) return "refill approval";
        if (screenMode == RxScreenMode.NewRx) return "new Rx";
        return "unknown";
    }

    private static AutoDiagnosticRateLimitState Clone(AutoDiagnosticRateLimitState? source)
    {
        if (source is null) return new AutoDiagnosticRateLimitState();
        return new AutoDiagnosticRateLimitState
        {
            LastReportedUtcByRx = new Dictionary<string, DateTime>(source.LastReportedUtcByRx),
            RecentReportTimestampsUtc = new List<DateTime>(source.RecentReportTimestampsUtc)
        };
    }
}
