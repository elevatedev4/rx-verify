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
/// </summary>
public static class RowHighlightColorDetector
{
    /// <summary>
    /// Minimum chroma (max(R,G,B) - min(R,G,B)) for a color to count as a
    /// genuine highlight FILL rather than plain white/near-white or one of
    /// the existing pale green/yellow row-banding tints. Pale tints
    /// measured/estimated in round 2's own test fixtures top out around
    /// chroma 25-57 (e.g. (210,235,210) chroma=25, (250,245,200) chroma=50,
    /// (173,216,230) chroma=57); a genuine selection/flag fill (e.g.
    /// (20,90,170) chroma=150, or pure highlight yellow (255,255,0)
    /// chroma=255) sits well above that. 60 leaves clear margin on both
    /// sides.
    /// </summary>
    public const int MinChromaForHighlight = 60;

    /// <summary>Luminance floor -- excludes near-black (ordinary dark text itself, never a row FILL).</summary>
    public const int MinLuminanceForHighlight = 40;

    /// <summary>Luminance ceiling -- excludes near-white (the ordinary unhighlighted page background).</summary>
    public const int MaxLuminanceForHighlight = 245;

    /// <summary>Per-channel tolerance for "close enough to the row's own dominant/background color" -- used both to decide the majority-fill fraction and, in RowHighlightNormalizer, to binarize each pixel.</summary>
    public const int BackgroundColorTolerance = 40;

    /// <summary>Default fraction of a scanline's pixels that must be background-colored for the WHOLE scanline to count as a highlighted band -- same "reject a thin stroke of colored text, only accept a genuine full-width fill" safety margin round 2's detector used.</summary>
    public const double DefaultMinHighlightFraction = 0.6;

    /// <summary>ITU-R BT.601 luma -- the standard, simple RGB-to-perceived-brightness weighting; good enough here since this only needs to separate "roughly dark" from "roughly light", not calibrated photometric accuracy.</summary>
    private static double Luminance(byte r, byte g, byte b) => 0.299 * r + 0.587 * g + 0.114 * b;

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
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var chroma = max - min;
        var luminance = Luminance(r, g, b);

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
