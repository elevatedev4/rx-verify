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
/// ROOT CAUSE, ROUND 3: round 2 correctly diagnosed the MECHANISM (a row
/// whose background/text color deviates from Pioneer's normal dark-text-
/// on-light-background convention silently drops out of OCR — every
/// downstream class in this module is pure text/geometry, see
/// CellValueBucketizer's "blank cell -> Unknown, never flagged" doc) but
/// picked too NARROW a fix: one hardcoded blue hue family. That explains
/// all three still-open reports as ONE root cause, not three:
///   - Will's real Pioneer selection-blue apparently isn't the exact shade
///     round 2 guessed (never verified — no way to sample real pixels from
///     this Mac), so the narrow blue-only match still misses it.
///   - Clicking into a Create Recommended Orders cell to edit its
///     Order Quantity puts that GRID ROW into the same kind of
///     selection/focus highlight state as a selected row on the Catalog
///     Substitution window — same mechanism as the blue-first-row bug,
///     just never covered there before (SelectionRowNormalizer WAS already
///     wired into both target windows' capture path, but its narrow blue
///     detector still misses whatever shade Create Recommended Orders
///     actually uses for an in-edit row).
///   - A row McKesson itself has flagged/highlighted YELLOW is a
///     THIRD color the old detector never had any bounds for at all.
///
/// FIX: stop trying to name specific highlight hues. Detect ANY full-row
/// background that is genuinely COLORED (a real, saturated fill — not a
/// neutral near-white/near-black, and not one of the existing pale
/// green/yellow row-banding tints, which stay low-saturation on purpose)
/// via CHROMA (max channel - min channel), the standard hue-independent
/// "how colorful is this" measure. Once such a band is found, BINARIZE it
/// (majority/background-colored pixels -> pure white, everything else ->
/// pure black) instead of blindly inverting — this fixes BOTH contrast
/// polarities in one pass (white-on-dark selection blue AND dark-on-bright
/// highlight yellow) without ever needing to know which polarity a given
/// highlight color uses, let alone its exact hue. See RowHighlightNormalizer
/// for where this per-scanline decision is actually applied to a captured
/// bitmap.
///
/// STILL AN ESTIMATE: the exact chroma/luminance thresholds below are
/// tuned against synthetic RGB triples representing the color FAMILIES
/// described in Will's reports and round 1/2's own doc (a dark saturated
/// selection fill, a bright saturated highlight fill, and the existing
/// pale green/yellow row-banding tints that must stay untouched) — not
/// measured from a real Pioneer screenshot, which remains impossible from
/// this Mac. Unlike round 2, though, this fix no longer depends on
/// guessing a SPECIFIC hue at all, only on "is this row's background
/// meaningfully more colorful than plain white/pale-tint" — a much lower-
/// risk guess to get wrong, and one that degrades safely: a row this
/// still doesn't catch is exactly as broken as before (OCR read failure,
/// no highlight that tick), never a NEWLY introduced false positive on an
/// ordinary row, since ordinary body-text rows are >90% plain background
/// pixels by area and their per-row dominant/median color stays low-chroma
/// by construction.
///
/// ROUND 5 (Will, verbatim: "Order is still not reading the colored rows.
/// It's only reading the white ones. But the yellow and blue colored rows
/// are valid."): round 3/4's framing above was backwards on one point --
/// "pale green/yellow row-banding tints" are NOT always inert decoration to
/// exclude; Will's PALE yellow and PALE/light blue rows are genuine
/// highlights that must be detected same as a saturated one. The 60-chroma
/// floor rejected them outright (pale yellow ~50, light blue ~57, pale
/// header blue ~45 -- all under 60), so those rows never got binarized and
/// OCR silently skipped them exactly as reported. See
/// MinChromaForHighlight's own doc for the corrected floor (30) and why it
/// still keeps plain white/near-white/light-gray rows AND the one
/// remaining genuinely-neutral pale tint (pale green banding, chroma=25)
/// out.
/// </summary>
public static class RowHighlightColorDetector
{
    /// <summary>
    /// Minimum chroma (max(R,G,B) - min(R,G,B)) for a color to count as a
    /// genuine highlight FILL rather than plain white/near-white/light-gray.
    ///
    /// ROUND 5 (Will: "Order is still not reading the colored rows. It's
    /// only reading the white ones. But the yellow and blue colored rows
    /// are valid."): round 3/4's 60 floor was calibrated on the WRONG
    /// assumption that pale yellow/light-blue tints are inert row-banding
    /// to be excluded -- Will's report says the opposite: PALE colored rows
    /// (a light yellow flag, a light blue selection/header tint) are
    /// legitimate highlights that must be detected, not banding to ignore.
    /// Those exact pale tints measure: (250,245,200) pale yellow chroma=50,
    /// (173,216,230) light blue chroma=57, (200,220,245) pale header blue
    /// chroma=45 -- all BELOW the old 60 floor, so round 3/4 silently
    /// skipped every one of them, matching the bug report exactly (pale
    /// rows never binarized -> OCR only ever reads the white ones).
    ///
    /// Lowered to 30. Still well clear of plain white/near-white/light-gray
    /// rows, which stay at chroma=0 (every channel equal) plus a few points
    /// of anti-aliasing noise (typically &lt;15) -- and clear of the one
    /// pale-but-genuinely-neutral tint this detector must keep rejecting,
    /// (210,235,210) pale green banding at chroma=25. 30 sits above that
    /// with a 5-point margin and below the lowest now-required pale color
    /// (45) with a 15-point margin.
    /// </summary>
    public const int MinChromaForHighlight = 30;

    /// <summary>Luminance floor -- excludes near-black (ordinary dark text itself, never a row FILL).</summary>
    public const int MinLuminanceForHighlight = 40;

    /// <summary>
    /// Luminance ceiling -- excludes near-white (the ordinary unhighlighted
    /// page background).
    ///
    /// ROUND 4 (Will's THIRD repeat report on the same symptom — "the top
    /// blue line of catalog item substitution is still not being
    /// analyzed" — prompted re-measuring against a REAL screenshot instead
    /// of another synthetic guess): the owner's own Create Recommended
    /// Orders screenshot (order screen round 4 qty-0 report) contains a
    /// row Pioneer itself tints pale yellow -- measured pixel-for-pixel
    /// from that PNG at (255,255,179): chroma=76 (clears MinChromaForHighlight
    /// easily) but luminance=246.3, which the OLD ceiling of 245 rejected
    /// by a hair. Bumped to 250 to bring this real, measured Pioneer color
    /// inside bounds with a small margin, rather than sitting on the exact
    /// wrong side of a never-measured-before guess. (This specific row's
    /// OCR readability was likely fine either way -- black text on a pale
    /// fill has plenty of natural contrast without binarization -- but a
    /// boundary a REAL Pioneer color sits 1.3 outside of is worth
    /// correcting regardless; see RowHighlightColorDetectorTests' own
    /// round-4 test for the exact measured value.) Still leaves a wide
    /// margin below pure white (255) and comfortably above every "reject"
    /// test case's luminance.
    /// </summary>
    public const int MaxLuminanceForHighlight = 250;

    /// <summary>Per-channel tolerance for "close enough to the row's own dominant/background color" -- used both to decide the majority-fill fraction and, in RowHighlightNormalizer, to binarize each pixel.</summary>
    public const int BackgroundColorTolerance = 40;

    /// <summary>
    /// Default fraction of a scanline's pixels that must be background-colored
    /// for the WHOLE scanline to count as a highlighted band -- same "reject
    /// a thin stroke of colored text, only accept a genuine full-width fill"
    /// safety margin round 2's detector used.
    ///
    /// ROUND 5 (Will, still after the chroma-30 fix: "Order is still not
    /// reading the colored rows" on Catalog Substitution specifically):
    /// EscriptImageCapture.CaptureRegion captures the ENTIRE target window
    /// (OrderAssistCoordinator.TickAsync's target.Value.Bounds is the whole
    /// GetWindowRect, not a crop of just the grid), so a highlighted TABLE
    /// row's scanline also includes whatever window chrome/margin pixels
    /// sit outside the grid on that same Y — a scrollbar track, a filter
    /// column, or side padding, all of which stay a plain neutral color.
    /// Catalog Substitution's own chrome is documented elsewhere in this
    /// module (HeaderRowWindowSelector's ROOT CAUSE doc) as having MORE
    /// non-grid chrome than Create Recommended Orders (an extra filter/
    /// toolbar row) — a plausible, screen-specific reason a real
    /// highlighted row's colored-pixel fraction sits lower on THIS screen
    /// even though the same color already clears MinChromaForHighlight/the
    /// luminance band (verified against every color in Will's report --
    /// see RowHighlightColorDetectorTests' round-5 cases, all of which pass
    /// the color check at 80% fill; the 0.6 floor was never actually
    /// exercised against anything narrower). Lowered to 0.5: still a full
    /// majority of the scanline, so an ordinary plain row (background is
    /// ~95%+ of the row by construction -- see class doc) is nowhere close
    /// to tripping this, but leaves 10 more points of headroom for
    /// non-grid chrome/margin dilution on a real captured window. STILL AN
    /// ESTIMATE (same posture as every other bound in this class) -- not
    /// measured from a real Catalog Substitution capture, which remains
    /// impossible from this Mac; see LogSelectionBandsIfChanged's own
    /// round-5 kind-tagging addition for how Will's NEXT report proves or
    /// disproves this specific theory instead of requiring another guess.
    /// </summary>
    public const double DefaultMinHighlightFraction = 0.5;

    /// <summary>
    /// ROUND 5 diagnostic-only floor -- NOT part of the accept/reject
    /// decision. A scanline whose dominant chroma clears this (but not the
    /// real <see cref="MinChromaForHighlight"/> floor, or fails the
    /// luminance band / fill-fraction check) is a "near miss" worth logging
    /// so Will's next report carries the exact measured values instead of
    /// another guess -- see OrderAssistCoordinator.LogSelectionBandsIfChanged.
    /// Set low enough to skip ordinary anti-aliased white/black text edges
    /// (typically chroma &lt;15) but still catch anything with deliberate,
    /// visible tint.
    /// </summary>
    public const int MinChromaForDiagnosticCandidate = 15;

    /// <summary>ITU-R BT.601 luma -- the standard, simple RGB-to-perceived-brightness weighting; good enough here since this only needs to separate "roughly dark" from "roughly light", not calibrated photometric accuracy.</summary>
    private static double Luminance(byte r, byte g, byte b) => 0.299 * r + 0.587 * g + 0.114 * b;

    /// <summary>Chroma (max(R,G,B) - min(R,G,B)) and luminance for one color -- the two raw measurements every accept/reject/diagnostic decision in this class is built from. Exposed publicly (ROUND 5) so callers -- currently just the normalizer's rejected-candidate diagnostic logging -- can report the exact measured values without duplicating this math.</summary>
    public static (int Chroma, double Luminance) MeasureColor(byte r, byte g, byte b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        return (max - min, Luminance(r, g, b));
    }

    /// <summary>
    /// True for a color that looks like a genuine highlight/flag/selection
    /// FILL -- real, saturated color (chroma), not too dark to be a
    /// background (that's text), not too light to be distinguishable from
    /// the ordinary page background at all. Hue-agnostic on purpose (see
    /// class doc) -- this fires on blue, yellow, green, orange, whatever
    /// color Pioneer uses, as long as it's genuinely colored.
    /// </summary>
    public static bool IsHighlightedBackgroundColor(byte r, byte g, byte b)
    {
        var (chroma, luminance) = MeasureColor(r, g, b);

        return chroma >= MinChromaForHighlight &&
               luminance >= MinLuminanceForHighlight &&
               luminance <= MaxLuminanceForHighlight;
    }

    /// <summary>
    /// Per-channel MEDIAN across every pixel in the scanline -- robust to a
    /// minority of very different (text) pixels, since a genuine row FILL
    /// by definition covers the majority of the row's width. White
    /// (255,255,255) for an empty pixel list -- callers never call this on
    /// an empty list in practice (IsHighlightScanline short-circuits first)
    /// but a defined, harmless default is safer than throwing.
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

    /// <summary>True if every channel of the given color is within <paramref name="tolerance"/> of <paramref name="target"/> -- the per-pixel test both the majority-fraction check below and RowHighlightNormalizer's own binarization step use.</summary>
    public static bool IsCloseToColor(byte r, byte g, byte b, (byte R, byte G, byte B) target, int tolerance = BackgroundColorTolerance) =>
        Math.Abs(r - target.R) <= tolerance &&
        Math.Abs(g - target.G) <= tolerance &&
        Math.Abs(b - target.B) <= tolerance;

    /// <summary>
    /// True when this scanline's own dominant color is a genuine highlight
    /// fill (<see cref="IsHighlightedBackgroundColor"/>) AND at least
    /// <paramref name="minFraction"/> of its pixels are close to that
    /// dominant color -- the second check is what rejects a thin colored
    /// TEXT stroke (a hyperlink-blue drug name, say) surrounded by ordinary
    /// light background: the row's own MEDIAN/dominant color there is still
    /// the light background itself (text is a small minority of the row's
    /// width), so IsHighlightedBackgroundColor never even sees the text
    /// color as the "dominant" one to begin with.
    /// </summary>
    public static bool IsHighlightScanline(IReadOnlyList<(byte R, byte G, byte B)> pixels, double minFraction = DefaultMinHighlightFraction)
    {
        if (pixels.Count == 0) return false;

        var dominant = EstimateDominantColor(pixels);
        if (!IsHighlightedBackgroundColor(dominant.R, dominant.G, dominant.B)) return false;

        var matchCount = pixels.Count(p => IsCloseToColor(p.R, p.G, p.B, dominant));
        return (double)matchCount / pixels.Count >= minFraction;
    }
}
