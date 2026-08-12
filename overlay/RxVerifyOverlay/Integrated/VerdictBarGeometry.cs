using System;
using System.Collections.Generic;
using System.Linq;

namespace RxVerifyOverlay.Integrated;

/// <summary>
/// Pure geometry behind the left-edge-bar verdict rendering. Originated in
/// round 6 as a GREEN-only change (owner: "make the green boxes just be a
/// thicker left side only bar on the side of each field that is good ...
/// too distracting to have everything encircled. Leave red boxes the way
/// they are") and generalized in round 7 (owner: red verdicts should
/// render the SAME way — see IntegratedBoxesWindow.SetBoxes, which now
/// calls this once per color instead of routing red through a separate
/// full-border path). Nothing here is green- or red-specific: it takes
/// the SAME fully-adjusted rects IntegratedBoxesWindow already computes
/// for every field regardless of color (BoxLayoutAdjuster's pad -&gt;
/// flush -&gt; left-align pipeline runs identically for both) and derives
/// a thin vertical bar at each rect's LEFT edge. A WPF Border's own
/// Background fill starts AT the element's outer edge — so a bar at
/// X = rect.X, extending rightward by BarWidthDip, sits in exactly the
/// same place the old encircling border's left edge did, just wider.
///
/// GAP (owner report, round 8: the bars sat flush against the field box
/// and were "creeping right up on the text"): each bar now sits OUTSIDE
/// the field's left edge with BarGapDip DIP of breathing room, i.e. the
/// bar's RIGHT edge lands at (rect.X - BarGapDip), not at rect.X itself —
/// see DeriveBarRect. Every rect in a column shares the same original
/// rect.X, so this shift is applied identically to every bar in that
/// column; the (X, Width) merge key MergeVerticallyTouchingBars compares
/// on is computed AFTER the shift, so column alignment and merging are
/// both unaffected by it. Deliberately never clamped at the screen edge —
/// PioneerRx's fields never sit at DIP x=0, so a bar's shifted X going
/// slightly negative in screen-relative terms is expected, not a bug.
///
/// MERGING (the actual "no seam" guarantee, not just a math coincidence):
/// BoxLayoutAdjuster.SnapFlushAdjacentEdges already makes stacked rects'
/// touching Y-boundaries EXACTLY equal at the double level, so two bars
/// derived from touching rects are already seam-free in DIP-space math —
/// but WPF can still independently pixel-snap two SEPARATE elements to
/// different device pixels at non-1:1 DPI scaling, which would show as a
/// hairline gap even when the underlying DIPs match exactly.
/// DeriveMergedBarRects removes that risk at the root by combining any
/// run of bars that share the same X/Width and whose Y-ranges are
/// exactly (or effectively) touching into ONE taller bar — with only one
/// element, there's no boundary left to mis-render. Each color's rects are
/// merged independently (a green bar and a red bar are never merged into
/// each other even if they'd otherwise touch — see SetBoxes).
/// </summary>
public static class VerdictBarGeometry
{
    /// <summary>
    /// How wide a verdict bar is — the owner asked for "thicker" than the
    /// old 3px encircling border; 5 DIP (within the suggested 4-6 range)
    /// reads as a deliberate accent stripe without being wide enough to
    /// start covering meaningful width of a narrow field. An estimate,
    /// like every other geometry constant in this class — retune against
    /// a live workstation. Shared by both green and red bars — round 7's
    /// whole point is one render style for both.
    /// </summary>
    public const double BarWidthDip = 5;

    /// <summary>
    /// How much breathing room sits between a bar's RIGHT edge and the
    /// field rect's LEFT edge — owner report (round 8): the bars sat
    /// flush against the field box and were "creeping right up on the
    /// text". 3 DIP reads as a deliberate small gap without visually
    /// detaching the bar from the field it's marking. See DeriveBarRect
    /// and the class doc's GAP section.
    /// </summary>
    public const double BarGapDip = 3;

    /// <summary>Floating-point tolerance for "same column" (X/Width match) and "touching" (Y-ranges abut) comparisons — guards against harmless double-precision noise, never a real near-miss (genuinely different columns are already many DIPs apart, per BoxLayoutAdjuster.LeftEdgeAlignmentToleranceDip).</summary>
    private const double Epsilon = 0.01;

    /// <summary>
    /// Derives one full-height bar from a single fully-adjusted rect,
    /// positioned just OUTSIDE the rect's left edge with
    /// <paramref name="gap"/> DIP of breathing room — the bar's right
    /// edge lands at (rect.X - gap), i.e. bar.X = rect.X - barWidth - gap.
    /// Y and Height are copied through UNCHANGED (no arithmetic on them at
    /// all) specifically so a run of already-flush-snapped rects yields
    /// bars whose Y-ranges are STILL exactly flush, with zero additional
    /// floating-point error introduced by this step. Never clamped at the
    /// screen edge — see the class doc's GAP section for why that's safe.
    /// </summary>
    public static DipRect DeriveBarRect(DipRect rect, double barWidth = BarWidthDip, double gap = BarGapDip)
    {
        return new DipRect(rect.X - barWidth - gap, rect.Y, barWidth, rect.Height);
    }

    /// <summary>
    /// Derives a bar per rect (DeriveBarRect) and then merges any run of
    /// bars that share the same X/Width and whose Y-ranges exactly abut
    /// (or overlap) into single taller bars — see the class doc for why
    /// merging, not just deriving, is what actually guarantees no
    /// rendering seam. Order of the input list doesn't matter; output
    /// order/count is unspecified beyond "one bar per merged column run" —
    /// callers that don't need a positional correspondence to the input
    /// (IntegratedBoxesWindow renders plain color bars with no per-field
    /// content) don't need one.
    /// </summary>
    public static IReadOnlyList<DipRect> DeriveMergedBarRects(IReadOnlyList<DipRect> rects, double barWidth = BarWidthDip, double gap = BarGapDip)
    {
        var bars = rects.Select(rect => DeriveBarRect(rect, barWidth, gap)).ToList();
        return MergeVerticallyTouchingBars(bars);
    }

    /// <summary>
    /// Merges any bars that share the same X/Width (within Epsilon) and
    /// whose Y-ranges are touching or overlapping into one taller bar —
    /// repeats until no more merges are possible, so a long chain merges
    /// down to a single bar regardless of input order.
    /// </summary>
    public static IReadOnlyList<DipRect> MergeVerticallyTouchingBars(IReadOnlyList<DipRect> bars)
    {
        var remaining = bars.ToList();
        var mergedAny = true;

        while (mergedAny)
        {
            mergedAny = false;

            for (var i = 0; i < remaining.Count && !mergedAny; i++)
            {
                for (var j = i + 1; j < remaining.Count; j++)
                {
                    var a = remaining[i];
                    var b = remaining[j];

                    var sameColumn = Math.Abs(a.X - b.X) <= Epsilon && Math.Abs(a.Width - b.Width) <= Epsilon;
                    if (!sameColumn) continue;

                    var (top, bottom) = a.Y <= b.Y ? (a, b) : (b, a);
                    var touchingOrOverlapping = bottom.Y <= top.Y + top.Height + Epsilon;
                    if (!touchingOrOverlapping) continue;

                    var mergedBottom = Math.Max(top.Y + top.Height, bottom.Y + bottom.Height);
                    var merged = top with { Height = mergedBottom - top.Y };

                    remaining.RemoveAt(j);
                    remaining.RemoveAt(i);
                    remaining.Add(merged);

                    mergedAny = true;
                    break;
                }
            }
        }

        return remaining;
    }
}
