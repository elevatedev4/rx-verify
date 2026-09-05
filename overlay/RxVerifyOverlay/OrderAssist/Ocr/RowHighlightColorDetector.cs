using System;
using System.Collections.Generic;
using System.Linq;

namespace RxVerifyOverlay.OrderAssist.Ocr;

/// <summary>
/// ROUND 3 (repeat complaint W-T85 bug 2 STILL happening on Catalog
/// Substitution's blue first row, PLUS two new reports: Create Recommended
/// Orders still doesn't pick up a qty edited to 0, and "sometimes rows may
/// be highlighted in yellow [in McKesson itself] ... this one skipped the
/// yellow item"). Replaces round 2's SelectionRowColorDetector, which only
/// ever matched ONE specific, unverified, blue-dominant RGB range.
///
/// ROUND 3/4/5 HISTORY (kept for context — the actual accept/reject GATE
/// this history describes was removed in round 6; see the ROUND 6 section
/// below for why and what replaced it):
///
/// Rounds 3-5 all took the same shape: a row whose background deviates
/// from Pioneer's normal dark-text-on-light-background convention has to
/// be detected before it can be binarized for OCR, so each round tried to
/// draw a better boundary around "genuinely colored row fill" using CHROMA
/// (max channel - min channel, hue-independent) and a luminance band, plus
/// a minimum fraction of the scanline that had to match the fill color.
/// Round 3 introduced the hue-agnostic chroma/luminance test (replacing
/// round 2's single hardcoded blue). Round 4 widened the luminance ceiling
/// after measuring a real Pioneer pale-yellow flag row that sat just
/// outside it. Round 5 lowered the chroma floor (60 -&gt; 30) and the fill
/// fraction (0.6 -&gt; 0.5) after Will reported PALE yellow/blue rows —
/// not just saturated ones — were still being skipped.
///
/// ROUND 6 (Will, verbatim: "The blue and yellow lines need to be read
/// too, just as if they are white regular lines. Currently they are being
/// skipped."): three consecutive rounds of threshold tuning and the
/// symptom is UNCHANGED. That is not evidence the thresholds need a
/// fourth adjustment — it is evidence the whole DETECT-THEN-BINARIZE
/// approach is the wrong shape for this problem. Every constant this class
/// has ever shipped (chroma floor, luminance floor/ceiling, fill fraction)
/// was, by this class's own repeated admission, an ESTIMATE never measured
/// against a real Pioneer screen (no way to sample real pixels from this
/// Mac — see every prior round's own doc above). Guessing a fourth set of
/// numbers has the same failure mode as the first three: whatever Will's
/// actual screen renders can sit on the wrong side of any boundary drawn
/// blind, and each miss reads to him as "still skipped" regardless of how
/// many times the boundary moved.
///
/// FIX: delete the gate. RowHighlightNormalizer no longer asks "is this
/// scanline a highlight?" at all — it binarizes EVERY scanline in the
/// capture, unconditionally, using that scanline's own dominant color as
/// the background to map to white (everything else becomes black). A
/// plain white row with black text already binarizes to something visually
/// equivalent to today's untouched path (dominant = white, text = far from
/// white -&gt; black), so ordinary rows see no behavior change worth
/// worrying about. A blue selection row or a yellow flagged row now goes
/// through the EXACT SAME per-scanline pass with no separate decision
/// gating it, so there is no threshold left to mistune, measure wrong, or
/// re-tune a fifth time. See RowHighlightNormalizer's own class doc for
/// the per-scanline algorithm and its failure-mode analysis.
///
/// This class still owns the raw color primitives (dominant-color
/// estimation, "is this pixel close to that color") that the unconditional
/// binarization is built from — those never depended on the gate and stay
/// exactly as accurate as before. It also keeps one small, PURELY
/// diagnostic classification (<see cref="IsNotablyColored"/>) so
/// OrderAssistCoordinator can log how many scanlines each tick had a
/// genuinely colored (non-near-white) dominant background — i.e., proof
/// the fix is actually seeing/binarizing blue and yellow rows on Will's
/// real screen, not a decision that changes what gets binarized.
/// </summary>
public static class RowHighlightColorDetector
{
    /// <summary>
    /// Per-channel tolerance for "close enough to this scanline's own
    /// dominant/background color" — used by RowHighlightNormalizer's
    /// per-pixel binarization decision on EVERY scanline (round 6: no
    /// longer scoped to only scanlines that first passed a detection
    /// gate — there is no gate).
    ///
    /// Ungated, this tolerance is now the ONLY thing standing between a
    /// clean binarization and two edge cases worth naming explicitly:
    ///   - ANTI-ALIASED EDGES: a pixel half-blended between the row's fill
    ///     and adjacent text/border color needs to land on one side or the
    ///     other; 40 (unchanged from round 3) is wide enough to pull a
    ///     lightly-blended edge pixel in with its dominant background
    ///     without also swallowing genuine dark text (typically 100+
    ///     channel-distance from any light fill, and 150+ from a
    ///     mid-saturation highlight fill).
    ///   - A DENSE/TEXT-DOMINANT SCANLINE: if text pixels actually
    ///     outnumber background pixels on a given scanline (e.g. a row of
    ///     tightly-kerned bold digits with little whitespace between
    ///     them), EstimateDominantColor's median could return the TEXT
    ///     color as "dominant" instead of the background — see that
    ///     method's own doc for why this is rare but not impossible, and
    ///     RowHighlightNormalizer's class doc for what happens to the
    ///     binarized output in that case (polarity flips, but the output
    ///     is still clean binary black/white — never worse than the
    ///     pre-round-6 alternative of not binarizing that row at all).
    /// </summary>
    public const int BackgroundColorTolerance = 40;

    /// <summary>
    /// ROUND 6 diagnostic-only floor — has NO effect on binarization
    /// (which now runs unconditionally on every scanline regardless of
    /// this classification). Used solely by RowHighlightNormalizer to
    /// count/band which scanlines had a dominant color worth calling
    /// "colored" for OrderAssistCoordinator's per-tick log line, so a
    /// field report proves highlighted rows are actually being seen and
    /// binarized (not silently passed through as if plain white) without
    /// needing another live-diagnosis round. Same numeric value round 5's
    /// MinChromaForDiagnosticCandidate used for the same "skip ordinary
    /// anti-aliased text-edge noise (typically &lt;15), catch anything with
    /// deliberate, visible tint" purpose — the value carries over because
    /// its job is unchanged, only its consumer (a log-only classification,
    /// not a gate input) is new.
    /// </summary>
    public const int DiagnosticColoredRowChromaFloor = 15;

    /// <summary>ITU-R BT.601 luma -- the standard, simple RGB-to-perceived-brightness weighting; good enough here since this only needs a rough "how bright" number for diagnostic logging, not calibrated photometric accuracy.</summary>
    private static double Luminance(byte r, byte g, byte b) => 0.299 * r + 0.587 * g + 0.114 * b;

    /// <summary>Chroma (max(R,G,B) - min(R,G,B)) and luminance for one color -- the two raw measurements the diagnostic "was this scanline colored" classification and its logging are built from. Exposed publicly so RowHighlightNormalizer's diagnostic band-tracking can report the exact measured values without duplicating this math.</summary>
    public static (int Chroma, double Luminance) MeasureColor(byte r, byte g, byte b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        return (max - min, Luminance(r, g, b));
    }

    /// <summary>
    /// DIAGNOSTIC-ONLY classification (round 6) — true when a color is
    /// visibly tinted (a blue/yellow/etc. row fill) rather than plain
    /// near-white/near-black/neutral-gray. This is NEVER consulted before
    /// binarizing a scanline (every scanline binarizes regardless, see
    /// RowHighlightNormalizer) — it exists purely so that class can count
    /// and log how many of this tick's scanlines were genuinely colored,
    /// for OrderAssistCoordinator's field-report logging.
    /// </summary>
    public static bool IsNotablyColored(int chroma) => chroma >= DiagnosticColoredRowChromaFloor;

    /// <summary>
    /// Per-channel MEDIAN across every pixel in the scanline -- robust to a
    /// minority of very different (text) pixels, since a genuine row FILL
    /// (or, on an ordinary row, the plain page background) by definition
    /// covers the majority of the row's width. White (255,255,255) for an
    /// empty pixel list -- callers never call this on an empty list in
    /// practice but a defined, harmless default is safer than throwing.
    ///
    /// ROUND 6 NOTE (dense/text-dominant scanline failure mode): if a
    /// scanline's TEXT pixels actually outnumber its background pixels
    /// (dense bold digits with little whitespace, for example), the
    /// median can return the text color as "dominant" instead of the true
    /// background. This was already true before round 6 (EstimateDominantColor
    /// itself is unchanged) — the difference is that round 6 no longer has
    /// a fraction-based gate that happened to reject some of those rows
    /// outright; now every row binarizes using whatever this method
    /// returns. The result in that edge case is a polarity-flipped but
    /// still-clean binary image (background -&gt; black, text -&gt; white)
    /// rather than a skipped row — see RowHighlightNormalizer's class doc
    /// for why that is an acceptable, self-limiting failure mode rather
    /// than a regression.
    /// </summary>
    public static (byte R, byte G, byte B) EstimateDominantColor(IReadOnlyList<(byte R, byte G, byte B)> pixels)
    {
        if (pixels.Count == 0) return (255, 255, 255);

        var mid = pixels.Count / 2;
        var r = pixels.Select(p => (int)p.R).OrderBy(v => v).ElementAt(mid);
        var g = pixels.Select(p => (int)p.G).OrderBy(v => v).ElementAt(mid);
        var b = pixels.Select(p => (int)p.B).OrderBy(v => v).ElementAt(mid);
        return ((byte)r, (byte)g, (byte)b);
    }

    /// <summary>True if every channel of the given color is within <paramref name="tolerance"/> of <paramref name="target"/> -- the per-pixel test RowHighlightNormalizer's unconditional binarization uses on every scanline.</summary>
    public static bool IsCloseToColor(byte r, byte g, byte b, (byte R, byte G, byte B) target, int tolerance = BackgroundColorTolerance) =>
        Math.Abs(r - target.R) <= tolerance &&
        Math.Abs(g - target.G) <= tolerance &&
        Math.Abs(b - target.B) <= tolerance;
}
