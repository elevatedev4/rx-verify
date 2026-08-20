namespace RxVerifyOverlay.OrderAssist;

/// <summary>
/// ROUND 3 REDESIGN (Will verbatim: "Make sure the highlighted items go
/// away as soon as the screen is closed, or if the order changes. Need to
/// have a faster update time. It's ok to clear it quickly and add a
/// 'Processing' by the sorted by rebate notice if we're waiting on
/// analysis.") — replaces round 2's HOLD-then-swap policy (which kept
/// showing the OLD, possibly-stale highlight on screen while a NEW result
/// was debouncing) with CLEAR-then-confirm: the instant this tick's result
/// differs from what's displayed, the old result is dropped immediately
/// (see Decision.Processing) and OrderAssistCoordinator shows a small
/// "Processing" indicator instead — never a stale highlight sitting there
/// unexplained. The new candidate is still required to repeat on
/// <see cref="RequiredConsecutiveTicksToAdoptChange"/> consecutive ticks
/// before actually being adopted (Decision.Display/Clear) — same
/// single-tick-OCR-misread flicker guard round 2's policy existed for (W-T85
/// bug 3: "the items are flashing a bunch instead of staying solid") — the
/// only thing that changed is what's shown WHILE that confirmation is
/// pending: nothing (+ "Processing"), never the previous tick's answer.
///
/// The empty ("nothing to flag") and non-empty ("here's a highlight") cases
/// are now handled by the exact same debounce path — round 2 kept them
/// separate (MaxConsecutiveEmptyTicksBeforeClearing vs.
/// RequiredConsecutiveTicksToAdoptChange), which is no longer needed now
/// that neither direction holds stale content while pending.
///
/// One exception, preserved from round 2: the very FIRST non-empty result
/// ever computed (nothing currently displayed at all) shows immediately,
/// no debounce -- there's no previous answer it could flicker against.
///
/// Comparison is by SIGNATURE, not raw geometry — see HighlightSignature.
/// Pixel coordinates jitter tick to tick even for the exact same logical
/// row (OCR isn't bit-for-bit deterministic), so comparing DipRect values
/// directly would defeat the whole point; a signature captures WHICH
/// row(s)/decision this is, which is what "genuinely different" should
/// mean here.
/// </summary>
public static class HighlightStabilityPolicy
{
    public const int RequiredConsecutiveTicksToAdoptChange = 2;

    public enum Decision
    {
        /// <summary>Stop displaying anything — this tick's (confirmed) result is genuinely empty.</summary>
        Clear,

        /// <summary>Draw this tick's freshly computed result, replacing whatever was displayed (or the first-ever result).</summary>
        Display,

        /// <summary>
        /// This tick's result differs from what's displayed but hasn't
        /// repeated enough times yet to adopt — clear whatever was shown
        /// immediately (Will: "ok to clear it quickly") and let the caller
        /// show a "Processing" indicator instead of a stale answer.
        /// </summary>
        Processing
    }

    /// <summary>One Decide call's full result — the Decision plus the caller's next pendingSignature/pendingStreak to pass into the NEXT tick's call (see the two params of the same name below).</summary>
    public readonly record struct Outcome(Decision Decision, string PendingSignature, int PendingStreak);

    /// <param name="newSignature">HighlightSignature.For* of this tick's freshly computed result — "" means empty/nothing to flag.</param>
    /// <param name="displayedSignature">The signature of whatever is CURRENTLY adopted/displayed — "" means nothing is currently shown. Only updated by the caller on a Display/Clear outcome (see Outcome's own doc) — a Processing outcome leaves it exactly as it was, since nothing new has been adopted yet.</param>
    /// <param name="pendingSignature">The signature most recently proposed as a REPLACEMENT for displayedSignature (from the caller's own bookkeeping, i.e. this call's own previous Outcome.PendingSignature) — "" if nothing is currently pending.</param>
    /// <param name="pendingStreak">How many CONSECUTIVE prior ticks already proposed pendingSignature — 0 if nothing is pending.</param>
    public static Outcome Decide(
        string newSignature,
        string displayedSignature,
        string pendingSignature,
        int pendingStreak)
    {
        if (newSignature == displayedSignature)
        {
            // Literally unchanged (including "still nothing, nothing") --
            // steady state, no pending candidate in flight.
            return new Outcome(newSignature.Length == 0 ? Decision.Clear : Decision.Display, "", 0);
        }

        if (displayedSignature.Length == 0 && newSignature.Length > 0)
        {
            // Nothing currently shown -- the very first result ever (or the
            // first since the last confirmed clear) shows immediately.
            // There's nothing on screen it could flicker against, so the
            // flicker guard below would only add latency for no safety
            // benefit here.
            return new Outcome(Decision.Display, "", 0);
        }

        var nextPendingSignature = newSignature;
        var nextPendingStreak = newSignature == pendingSignature ? pendingStreak + 1 : 1;

        if (nextPendingStreak >= RequiredConsecutiveTicksToAdoptChange)
        {
            return new Outcome(newSignature.Length == 0 ? Decision.Clear : Decision.Display, "", 0);
        }

        return new Outcome(Decision.Processing, nextPendingSignature, nextPendingStreak);
    }
}
