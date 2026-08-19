using System.Collections.Generic;

namespace RxVerifyOverlay.OrderAssist.Ocr;

/// <summary>
/// ROUND 2 (W-T85 bug 2, Will verbatim: "The analysis is also skipping
/// whatever row is highlighted (usually the first row starts highlighted,
/// as a dark blue). Must make sure to include that."). Pure, byte-level
/// color classification behind SelectionRowNormalizer's bitmap
/// preprocessing — split out so it's directly unit-testable with
/// synthetic RGB triples, no Bitmap/System.Drawing dependency at all (this
/// file has none), same "pure logic tested, OS-level bitmap plumbing
/// isn't" split every other OCR-adjacent class in this app already uses.
///
/// ROOT CAUSE (traced through the code, not guessed): the whole
/// OrderAssist pipeline — TableRowGrouper, ColumnResolver,
/// CellValueBucketizer, SubstitutionRecommender, PackageClassifier — is
/// PURE TEXT/GEOMETRY. None of it reads pixel color at all; a row is
/// EXCLUDED from every downstream decision only when OCR itself produces
/// no usable words for it (CellValueBucketizer.BucketColumn's Text=""/
/// Bounds=null "blank cell" case, which every decision class already
/// treats as "no data for this row", never as zero/free). So a row
/// disappearing from every result is necessarily an OCR READ failure for
/// that row specifically, not a downstream logic bug — and the one thing
/// visually different about Pioneer's SELECTED row (per Will's screenshot)
/// is its color: white text on a dark blue fill, the OPPOSITE contrast
/// polarity from every other row (dark text on white/pale-green/
/// pale-yellow). Windows.Media.Ocr is documented and generally reliable on
/// EITHER polarity individually, but a small captured region mixing both
/// polarities in one pass is exactly the kind of case where a single
/// row's text can come back missing or garbled rather than misread
/// character-by-character — degrading silently to "no words for this row"
/// exactly like Will described.
///
/// ESTIMATE, NOT YET VERIFIED (same posture as OrderAssistCoordinator.
/// OrderModeBottomInsetDip and Ocr/WindowsMediaOcrEngine's own confidence
/// doc): the exact RGB bounds below are an informed guess at Pioneer's
/// selection-highlight color (a medium-dark, blue-dominant fill — visually
/// consistent with a common Windows "accent blue" selection tint, per
/// Will's screenshot) — NOT measured from real captured pixels, since
/// there is no way to sample them from this Mac. SelectionRowNormalizer
/// logs how many bands it found + their y-ranges every tick (local-only,
/// see that class's own doc) specifically so the NEXT log paste proves or
/// disproves this heuristic directly, rather than needing another round of
/// guessing.
/// </summary>
public static class SelectionRowColorDetector
{
    /// <summary>
    /// True for a pixel whose color looks like Pioneer's dark-blue row
    /// SELECTION fill (not its lighter blue HYPERLINK text color, which
    /// the screenshot shows on every row's drug-description cell — see
    /// class doc). Bounds are deliberately narrow enough that ordinary
    /// link-blue text (typically brighter/more saturated, closer to a
    /// "pure" blue) and the grid's pale green/yellow row tints both fall
    /// well outside them:
    ///   - Blue must be the CLEARLY dominant channel (a real navy/accent
    ///     blue, not a green-leaning teal or red-leaning purple).
    ///   - Blue itself stays in a MEDIUM range — high enough that this
    ///     isn't near-black text, low enough that this isn't a
    ///     near-white/pale background tint.
    ///   - Red stays low — rules out lighter, more red-shifted blues
    ///     (lavender/periwinkle tones) a selection fill isn't likely to
    ///     use.
    /// </summary>
    public static bool IsSelectionBackgroundColor(byte r, byte g, byte b) =>
        b is >= 100 and <= 230 &&
        b > r + 40 &&
        b > g + 25 &&
        r < 100;

    /// <summary>Default fraction of a scanline's pixels that must match IsSelectionBackgroundColor for the WHOLE scanline to count as a selection-fill band — see IsSelectionScanline's own doc for why this (not a per-pixel decision) is what distinguishes a real background fill from scattered hyperlink-text pixels.</summary>
    public const double DefaultMinSelectionFraction = 0.6;

    /// <summary>
    /// True when at least <paramref name="minFraction"/> of one horizontal
    /// pixel row is selection-colored — the key safety mechanism that
    /// keeps this from ever firing on ordinary blue HYPERLINK text
    /// (present on every row's drug description, per Will's screenshot):
    /// a single link's text occupies a modest fraction of a row's full
    /// width, surrounded by that row's own light background on both
    /// sides, so no single scanline through normal text ever comes close
    /// to majority-blue. A genuine selection fill spans the row's FULL
    /// width edge-to-edge (confirmed in the screenshot), which is exactly
    /// what crosses this threshold.
    /// </summary>
    public static bool IsSelectionScanline(IReadOnlyList<(byte R, byte G, byte B)> pixels, double minFraction = DefaultMinSelectionFraction)
    {
        if (pixels.Count == 0) return false;

        var matchCount = 0;
        foreach (var (r, g, b) in pixels)
        {
            if (IsSelectionBackgroundColor(r, g, b)) matchCount++;
        }

        return (double)matchCount / pixels.Count >= minFraction;
    }
}
