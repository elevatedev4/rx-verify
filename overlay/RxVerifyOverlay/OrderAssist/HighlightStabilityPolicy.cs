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
/// unexplained.
///
/// ROUND 5 (Will verbatim: "Needs to be faster to read and display the
/// calculation, needs to be more responsive when the screen changes.") —
/// the round-3 debounce required the SAME
/// RequiredConsecutiveTicksToAdoptChange (2) count in BOTH directions:
/// adopting a brand-new/changed highlight AND confirming a clear. That
/// symmetry was never load-bearing for the SHOW direction — the W-T85 bug
/// 3 flicker report ("the items are flashing a bunch") this 2-tick count
/// was originally chosen to fix (see the tests below and their round-3
/// history) was about a single-tick OCR misread causing a shown result to
/// swap to a WRONG one and back, not about the FIRST sighting of a
/// genuinely new result being slow to appear. Splitting the threshold
/// asymmetrically — <see cref="RequiredConsecutiveTicksToShow"/> = 1 (a
/// new/changed non-empty result displays the very first tick it's seen,
/// same as the pre-existing "nothing displayed yet" fast path below) while
/// <see cref="RequiredConsecutiveTicksToClear"/> stays 2 (a result going
/// to empty still needs one confirming "Processing" tick before the
/// highlight actually disappears, protecting against a one-tick OCR
/// hiccup misreading "still there" as "gone") — buys the requested "faster
/// to display" behavior on the direction Will actually asked for while
/// keeping the original anti-flicker protection on clearing, the direction
/// that still needs it (see Decision.Clear/Decision.Processing's own doc).
/// This is a deliberate trade: a genuinely wrong single-tick OCR misread
/// on a NEW candidate can now display for one tick before a real follow-up
/// corrects it, in exchange for the "responsive when the screen changes"
/// complaint no longer costing a full second confirmation tick.
///
/// The empty ("nothing to flag") and non-empty ("here's a highlight") cases
/// still share the SAME pending/streak bookkeeping — only the THRESHOLD
/// each direction compares its streak against differs.
///
/// One exception, preserved from round 2/3: the very FIRST non-empty result
/// ever computed (nothing currently displayed at all) shows immediately,
/// no debounce -- there's no previous answer it could flicker against.
/// (This is now just a special case of RequiredConsecutiveTicksToShow = 1,
/// kept as its own branch below since "nothing pending yet" needs no
/// streak bookkeeping at all.)
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
    /// <summary>
    /// ROUND 5: how many CONSECUTIVE ticks a new/changed NON-EMPTY result
    /// must be proposed before it displays. 1 means "show it the first
    /// time it's seen" — see class doc for why this is no longer the same
    /// number as <see cref="RequiredConsecutiveTicksToClear"/>.
    /// </summary>
    public const int RequiredConsecutiveTicksToShow = 1;

    /// <summary>
    /// How many CONSECUTIVE ticks a result going to EMPTY must be proposed
    /// before the displayed highlight actually clears — kept at round 3's
    /// original 2 (unchanged) since this is the direction the W-T85
    /// flicker guard is still protecting; see class doc.
    /// </summary>
    public const int RequiredConsecutiveTicksToClear = 2;

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

        // ROUND 5: which threshold applies depends on which DIRECTION this
        // candidate is proposing — going to empty (Clear) still needs
        // RequiredConsecutiveTicksToClear confirming ticks; anything else
        // (a new/changed non-empty result, Display) only needs
        // RequiredConsecutiveTicksToShow — see class doc for why these are
        // no longer the same number.
        var isClearing = newSignature.Length == 0;
        var requiredTicks = isClearing ? RequiredConsecutiveTicksToClear : RequiredConsecutiveTicksToShow;

        if (nextPendingStreak >= requiredTicks)
        {
            return new Outcome(isClearing ? Decision.Clear : Decision.Display, "", 0);
        }

        return new Outcome(Decision.Processing, nextPendingSignature, nextPendingStreak);
    }
}
