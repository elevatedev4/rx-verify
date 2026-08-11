using System;
using System.Collections.Generic;
using System.Linq;

namespace RxVerifyOverlay.Integrated;

/// <summary>
/// Pure geometry for the round-6 green-field rendering change (owner:
/// "make the green boxes just be a thicker left side only bar on the
/// side of each field that is good ... too distracting to have
/// everything encircled. Leave red boxes the way they are"). Takes the
/// SAME fully-adjusted rects IntegratedBoxesWindow already computes for
/// every field (BoxLayoutAdjuster's pad -&gt; flush -&gt; left-align
/// pipeline runs identically for green AND red; only the RENDER-time
/// branch differs) and derives a thin vertical bar at each rect's LEFT
/// edge. A WPF Border's own stroke band starts AT the element's outer
/// edge and extends INWARD, never outward — so a bar at X = rect.X,
/// extending rightward by BarWidthDip, sits in exactly the same place
/// the old encircling border's left edge did, just wider.
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
/// element, there's no boundary left to mis-render.
/// </summary>
public static class GreenBarGeometry
{
    /// <summary>
    /// How wide the green bar is — the owner asked for "thicker" than the
    /// old 3px encircling border; 5 DIP (within the suggested 4-6 range)
    /// reads as a deliberate accent stripe without being wide enough to
    /// start covering meaningful width of a narrow field. An estimate,
    /// like every other geometry constant in this class — retune against
    /// a live workstation.
    /// </summary>
    public const double BarWidthDip = 5;

    /// <summary>Floating-point tolerance for "same column" (X/Width match) and "touching" (Y-ranges abut) comparisons — guards against harmless double-precision noise, never a real near-miss (genuinely different columns are already many DIPs apart, per BoxLayoutAdjuster.LeftEdgeAlignmentToleranceDip).</summary>
    private const double Epsilon = 0.01;

    /// <summary>
    /// Derives one full-height left-edge bar from a single fully-adjusted
    /// rect — Y and Height are copied through UNCHANGED (no arithmetic on
    /// them at all) specifically so a run of already-flush-snapped rects
    /// yields bars whose Y-ranges are STILL exactly flush, with zero
    /// additional floating-point error introduced by this step.
    /// </summary>
    public static DipRect DeriveBarRect(DipRect rect, double barWidth = BarWidthDip)
    {
        return new DipRect(rect.X, rect.Y, barWidth, rect.Height);
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
    public static IReadOnlyList<DipRect> DeriveMergedBarRects(IReadOnlyList<DipRect> rects, double barWidth = BarWidthDip)
    {
        var bars = rects.Select(rect => DeriveBarRect(rect, barWidth)).ToList();
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
