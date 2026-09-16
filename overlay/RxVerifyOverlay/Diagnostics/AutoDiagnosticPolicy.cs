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
    /// <summary>Rx number (or, for an unidentified Rx, the fallback context key — see AutoDiagnosticPolicy.GetContextKey) -&gt; UTC instant of the last auto-report sent for it. A rolling 24h window (not a calendar-day boundary) — simpler, and avoids a report at 11:59pm/12:01am counting as two different "days" for the same Rx.</summary>
    public Dictionary<string, DateTime> LastReportedUtcByRx { get; set; } = new();

    /// <summary>UTC instants of every auto-report sent in roughly the last hour on this PC — pruned (entries older than 1h dropped) on every ShouldReport call, so this never grows unbounded even across a long-running shift.</summary>
    public List<DateTime> RecentReportTimestampsUtc { get; set; } = new();

    /// <summary>
    /// The in-progress "refills box has been uncoloured N scans in a row"
    /// streak for whichever context (see AutoDiagnosticPolicy.GetContextKey)
    /// most recently produced an uncoloured result — see ShouldReport's
    /// debounce gate doc. Null before the first uncoloured scan ever seen,
    /// after a coloured scan, or (backward compatibility) when loading a
    /// state file written before this field existed — either way, "no
    /// field" and "field present with ConsecutiveMisses 0" both mean
    /// exactly the same thing: no streak in progress.
    /// </summary>
    public AutoDiagnosticStreakState? Streak { get; set; }
}

/// <summary>
/// One field's worth of ShouldReport's debounce bookkeeping — see
/// AutoDiagnosticRateLimitState.Streak's doc. Only ever tracks the SINGLE
/// most recently active context, same as the real workflow (a pharmacist
/// looks at one Rx/window at a time): switching context overwrites this
/// rather than keeping a per-context dictionary.
/// </summary>
public sealed class AutoDiagnosticStreakState
{
    /// <summary>rxNumber when present, otherwise the window-title+screen-mode fallback — see AutoDiagnosticPolicy.GetContextKey.</summary>
    public string ContextKey { get; set; } = "";

    /// <summary>How many consecutive ShouldReport calls for this SAME ContextKey have seen an uncoloured refills box, with no coloured result or context switch in between.</summary>
    public int ConsecutiveMisses { get; set; }

    /// <summary>UTC instant the streak started (the first of the consecutive uncoloured scans) — ShouldReport's persistence gate is nowUtc minus this.</summary>
    public DateTime FirstMissUtc { get; set; }
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

    /// <summary>Debounce gate (2026-09-16 field report — a transient "claim processing" popup burned a real Rx's daily budget): the refills box must be uncoloured on at least this many CONSECUTIVE engine results for the same context before a report can fire. See ShouldReport's doc.</summary>
    public const int MinConsecutiveMisses = 3;

    /// <summary>Debounce gate: the streak of consecutive misses (see MinConsecutiveMisses) must also span at least this many wall-clock seconds — a burst of 3 scans within a fraction of a second (e.g. a fast poll loop) is not, by itself, evidence the screen has actually settled.</summary>
    public const double MinPersistenceSeconds = 5;

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
    /// when ShouldReport is false — pruning of expired entries and streak
    /// bookkeeping still need to be saved so a restart doesn't lose the
    /// in-progress debounce, and so the state file doesn't grow forever).
    ///
    /// `contextKey` (see GetContextKey) identifies "the same screen" for
    /// both the debounce streak (gate 3) and, when `rxNumber` is empty,
    /// the daily cap (gate 4) — always pass the SAME value a caller would
    /// get from GetContextKey(rxNumber, windowTitle, screenMode) for this
    /// scan.
    ///
    /// Busy-screen scans (`isBusyScreen` true — see the overlay's
    /// transient-dialog phrase list) are handled FIRST and are pure
    /// no-information ticks: no report, and the streak/budget are left
    /// completely untouched (not reset, not incremented, not consumed) —
    /// only the routine hourly-bookkeeping prune still runs, same as
    /// every other early-return branch below.
    ///
    /// Gate 1 — must actually be uncoloured (see IsUncoloured); a
    /// Green/Red refills box is working correctly and is never reported.
    /// A coloured result, OR a scan for a DIFFERENT contextKey than the
    /// in-progress streak, resets the streak (to zero, or to a fresh
    /// 1-miss streak for the new context if this scan is itself
    /// uncoloured).
    ///
    /// Gate 2 — must look like a prescription that should have had
    /// refills at all: either `hasFillWords` (see the overlay's
    /// AutoDiagnosticPolicy.IsFillWord — a normalised OCR word shaped
    /// like "refill"/"fills"/"total fills", NOT a bare "Filled:"/"Fill:"
    /// label) or `isApproval` (the document classifies as a refill
    /// approval/renewal response). Neither means this simply isn't a
    /// refills-bearing document (e.g. a genuinely refill-less new Rx) —
    /// not a bug, nothing to report.
    ///
    /// Gate 3 — debounce: the streak for this contextKey must have
    /// reached MinConsecutiveMisses consecutive uncoloured scans AND
    /// span at least MinPersistenceSeconds of wall-clock time (2026-09-16
    /// field report: a single transient-popup scan must never alone
    /// trigger a report).
    ///
    /// Gate 4 — per-Rx (or, when rxNumber is empty, per-fallback-context)
    /// daily cap: the cap key must not have been reported in the last 24h
    /// (rolling, see AutoDiagnosticRateLimitState's doc). An empty
    /// rxNumber never SKIPS this cap (2026-09-16: previously it did) — it
    /// keys the cap on contextKey instead, so an unidentified Rx still
    /// gets a real per-screen daily limit rather than an unlimited one.
    ///
    /// Gate 5 — per-PC hourly cap: at most MaxReportsPerHour reports
    /// total (across every Rx) in the last rolling hour.
    ///
    /// `StateChanged` (2026-09-16 review fix) is true only when
    /// UpdatedState actually differs from `rateLimitState` (pruning
    /// removed something, the streak moved, or a report was recorded) —
    /// the caller should skip AutoDiagnosticStateStore.Save entirely when
    /// it's false, since the common steady-state case (refills box
    /// coloured, no streak in progress) leaves nothing to persist and
    /// otherwise forces a disk write on every single scan.
    /// </summary>
    public static (bool ShouldReport, AutoDiagnosticRateLimitState UpdatedState, bool StateChanged) ShouldReport(
        RefillsBoxRenderState refillsState,
        bool hasFillWords,
        bool isApproval,
        bool isBusyScreen,
        AutoDiagnosticRateLimitState? rateLimitState,
        DateTime nowUtc,
        string? rxNumber,
        string contextKey)
    {
        var state = Clone(rateLimitState);

        (bool, AutoDiagnosticRateLimitState, bool) Result(bool shouldReportNow) =>
            (shouldReportNow, state, !StatesEqual(rateLimitState, state));

        // Prune expired bookkeeping FIRST, unconditionally — the caller
        // persists UpdatedState (when StateChanged) regardless of the
        // boolean outcome, so a long run of "nothing to report" ticks
        // still keeps the file from growing forever.
        state.RecentReportTimestampsUtc = state.RecentReportTimestampsUtc
            .Where(t => nowUtc - t < TimeSpan.FromHours(1))
            .ToList();

        // Busy-screen scan: no information either way — leave the streak
        // and every cap completely untouched (see class doc above).
        if (isBusyScreen) return Result(false);

        var isUncoloured = IsUncoloured(refillsState);

        if (!isUncoloured)
        {
            state.Streak = null;
        }
        else if (state.Streak is null || !string.Equals(state.Streak.ContextKey, contextKey, StringComparison.Ordinal))
        {
            state.Streak = new AutoDiagnosticStreakState { ContextKey = contextKey, ConsecutiveMisses = 1, FirstMissUtc = nowUtc };
        }
        else
        {
            state.Streak.ConsecutiveMisses++;
        }

        if (!isUncoloured) return Result(false);
        if (!hasFillWords && !isApproval) return Result(false);

        var persistedSeconds = (nowUtc - state.Streak!.FirstMissUtc).TotalSeconds;
        if (state.Streak.ConsecutiveMisses < MinConsecutiveMisses || persistedSeconds < MinPersistenceSeconds)
        {
            return Result(false);
        }

        var dailyCapKey = string.IsNullOrEmpty(rxNumber) ? contextKey : rxNumber;
        if (state.LastReportedUtcByRx.TryGetValue(dailyCapKey, out var lastForRx) &&
            nowUtc - lastForRx < TimeSpan.FromDays(1))
        {
            return Result(false);
        }

        if (state.RecentReportTimestampsUtc.Count >= MaxReportsPerHour) return Result(false);

        state.RecentReportTimestampsUtc.Add(nowUtc);
        state.LastReportedUtcByRx[dailyCapKey] = nowUtc;
        return Result(true);
    }

    /// <summary>
    /// Structural equality for the StateChanged dirty-check above — a
    /// null `a` (no persisted/cached state yet) compares as a fresh empty
    /// state, so "nothing persisted, nothing to persist" correctly comes
    /// back equal.
    /// </summary>
    private static bool StatesEqual(AutoDiagnosticRateLimitState? a, AutoDiagnosticRateLimitState b)
    {
        var left = a ?? new AutoDiagnosticRateLimitState();

        if (left.RecentReportTimestampsUtc.Count != b.RecentReportTimestampsUtc.Count) return false;
        for (var i = 0; i < left.RecentReportTimestampsUtc.Count; i++)
        {
            if (left.RecentReportTimestampsUtc[i] != b.RecentReportTimestampsUtc[i]) return false;
        }

        if (left.LastReportedUtcByRx.Count != b.LastReportedUtcByRx.Count) return false;
        foreach (var (key, value) in left.LastReportedUtcByRx)
        {
            if (!b.LastReportedUtcByRx.TryGetValue(key, out var otherValue) || otherValue != value) return false;
        }

        if ((left.Streak is null) != (b.Streak is null)) return false;
        if (left.Streak is not null && b.Streak is not null)
        {
            if (!string.Equals(left.Streak.ContextKey, b.Streak.ContextKey, StringComparison.Ordinal)) return false;
            if (left.Streak.ConsecutiveMisses != b.Streak.ConsecutiveMisses) return false;
            if (left.Streak.FirstMissUtc != b.Streak.FirstMissUtc) return false;
        }

        return true;
    }

    /// <summary>
    /// The identity ShouldReport's debounce streak and (when rxNumber is
    /// empty) daily cap group scans by: `rxNumber` when present, otherwise
    /// the window title plus screen mode — the same "best identity we
    /// actually have" fallback already used elsewhere in this feature
    /// (see ClassifyDocument's screenMode reuse). Two calls with a null
    /// windowTitle still collide on the SAME key ("|screenMode") rather
    /// than on two different null-derived keys, which is fine: a null
    /// title only happens when the window read itself failed, and
    /// treating every such tick as "the same unknown screen" is no worse
    /// than treating them as unrelated.
    /// </summary>
    public static string GetContextKey(string? rxNumber, string? windowTitle, RxScreenMode screenMode) =>
        !string.IsNullOrEmpty(rxNumber) ? rxNumber : $"{windowTitle}|{screenMode}";

    /// <summary>
    /// Gate 2 tightening (2026-09-16 field report: a PreCheck screen's own
    /// "Filled:"/"Fill:" field LABELS were tripping the old bare
    /// /refill|fills?/i regex — that regex matches "fill" as a SUBSTRING,
    /// and "Filled"/"Fill" both contain it). True only when this OCR
    /// word's normalised form (letters only, lowercased, with the OCR
    /// digit-for-letter confusions 1-&gt;l, 0-&gt;o, 5-&gt;s undone — same
    /// garble class already handled for "Total Fills" elsewhere in this
    /// codebase) contains "refill", or "fills" (PLURAL — "filled"/"fill"
    /// do NOT contain this), or the "Total Fills" garble shape
    /// "totalfills" (already implied by the "fills" check for any garble
    /// that keeps the trailing s; kept as an explicit second check to
    /// match the brief's algorithm literally and to stay correct if the
    /// "fills" check above is ever narrowed).
    /// </summary>
    public static bool IsFillWord(string? wordText)
    {
        if (string.IsNullOrEmpty(wordText)) return false;
        var normalized = NormalizeFillWordCandidate(wordText);
        return normalized.Contains("refill", StringComparison.Ordinal)
            || normalized.Contains("fills", StringComparison.Ordinal)
            || normalized.Contains("totalfills", StringComparison.Ordinal);
    }

    private static string NormalizeFillWordCandidate(string text)
    {
        var chars = new List<char>(text.Length);
        foreach (var c in text)
        {
            if (char.IsLetter(c))
            {
                chars.Add(char.ToLowerInvariant(c));
            }
            else if (c == '1') chars.Add('l');
            else if (c == '0') chars.Add('o');
            else if (c == '5') chars.Add('s');
            // every other non-letter character (remaining digits,
            // punctuation, whitespace) is dropped, not kept — this is a
            // shape check, not an exact-text check.
        }
        return new string(chars.ToArray());
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
            RecentReportTimestampsUtc = new List<DateTime>(source.RecentReportTimestampsUtc),
            Streak = source.Streak is null
                ? null
                : new AutoDiagnosticStreakState
                {
                    ContextKey = source.Streak.ContextKey,
                    ConsecutiveMisses = source.Streak.ConsecutiveMisses,
                    FirstMissUtc = source.Streak.FirstMissUtc
                }
        };
    }
}
