namespace RxVerifyOverlay.OrderAssist;

/// <summary>
/// ROUND 2 (W-T85, Will verbatim: "the items are flashing a bunch instead
/// of staying solid") — pure hysteresis/debounce decision for what
/// OrderAssistCoordinator.TickAsync should actually DISPLAY this tick,
/// given what's currently on screen and what this tick's fresh OCR pass
/// just computed. Two independent, complementary jobs:
///
///  1. Don't blank an already-displayed highlight the instant a single
///     tick comes back empty (a transient OCR/column-resolution hiccup —
///     see OrderAssistCoordinator.LogColumnDiagnosticsIfNeeded, which
///     already distinguishes "genuinely nothing to flag" from "couldn't
///     resolve columns this tick" but previously treated both the same
///     way for display purposes: hold whatever's showing). Bounded by
///     MaxConsecutiveEmptyTicksBeforeClearing so a highlight that's
///     genuinely gone stale (the pharmacist actually fixed the row, or
///     navigated to a different screen entirely) doesn't linger forever.
///  2. Don't immediately swap to a DIFFERENT, non-empty result the first
///     time it's computed — require it to repeat on
///     RequiredConsecutiveTicksToAdoptChange consecutive ticks first. A
///     one-tick OCR misread that momentarily makes a DIFFERENT row look
///     like the right pick (e.g. bug 2's white-on-selection-blue row
///     misread) would otherwise flash the wrong highlight in and back out
///     a second later — exactly what "flashing" describes when it's not
///     just the on/off pulse (2) above addresses.
///
/// The GEOMETRIC pixel-level self-occlusion hide/show pulse (bug 3's OTHER
/// contributor) is a separate fix — see OrderAssist/Windows/
/// OrderAssistOverlayWindow.xaml.cs's SetWindowDisplayAffinity doc; this
/// class only ever governs WHICH result to draw, never how the drawing
/// itself avoids self-occlusion.
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
    public const int MaxConsecutiveEmptyTicksBeforeClearing = 3;

    public enum Decision
    {
        /// <summary>Leave whatever's currently displayed exactly as it is (draw nothing new this tick).</summary>
        KeepDisplayed,

        /// <summary>Draw this tick's freshly computed result, replacing whatever was displayed (or the first-ever result).</summary>
        Display,

        /// <summary>Stop displaying anything — the empty-tick streak finally exceeded the hold budget.</summary>
        Clear
    }

    /// <param name="newSignature">HighlightSignature.For* of this tick's freshly computed result — "" means empty/nothing to flag.</param>
    /// <param name="displayedSignature">The signature of whatever is CURRENTLY displayed — "" means nothing is currently shown.</param>
    /// <param name="consecutiveEmptyTicksSoFar">How many ticks IMMEDIATELY BEFORE this one were empty (0 if the previous tick was non-empty or this is the first tick) — caller resets this to 0 whenever a tick is non-empty.</param>
    /// <param name="pendingChangeStreak">How many CONSECUTIVE prior ticks already proposed the exact SAME new (non-empty, different-from-displayed) signature — caller resets this to 0 whenever the proposed signature changes or matches what's displayed.</param>
    public static Decision Decide(
        string newSignature,
        string displayedSignature,
        int consecutiveEmptyTicksSoFar,
        int pendingChangeStreak)
    {
        var isEmpty = string.IsNullOrEmpty(newSignature);
        var hasSomethingDisplayed = !string.IsNullOrEmpty(displayedSignature);

        if (isEmpty)
        {
            if (!hasSomethingDisplayed) return Decision.KeepDisplayed; // nothing shown, nothing to clear
            return consecutiveEmptyTicksSoFar + 1 >= MaxConsecutiveEmptyTicksBeforeClearing
                ? Decision.Clear
                : Decision.KeepDisplayed;
        }

        if (!hasSomethingDisplayed) return Decision.Display; // first real result ever -- show immediately, no debounce needed
        if (newSignature == displayedSignature) return Decision.KeepDisplayed; // literally unchanged

        // A genuinely different, non-empty candidate -- require it to
        // repeat before adopting it (see class doc, job 2).
        return pendingChangeStreak + 1 >= RequiredConsecutiveTicksToAdoptChange
            ? Decision.Display
            : Decision.KeepDisplayed;
    }
}
