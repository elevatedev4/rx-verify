using System.Collections.Generic;
using System.Linq;

namespace RxVerifyOverlay.Integrated;

/// <summary>
/// Pure geometry for the integrated boxes layer's visual polish (owner
/// feedback, round 2 item 2 — "a little too busy looking and hard to
/// read"): expands each field's box outward from the raw UIA rect
/// (padding, so the border doesn't hug the text) and, for boxes stacked
/// directly on top of each other, snaps the facing edges together so
/// there's one shared boundary line instead of a sliver of background
/// between them. No WPF/UIA dependency — plain DipRect in, DipRect out —
/// so this is covered by fast xUnit tests, same pattern as
/// DpiRectConverter/IntegratedVisibilityGate. IntegratedBoxesWindow (the
/// only production caller) applies both AFTER DpiRectConverter, so the
/// thresholds below are in DIPs, matching what the owner actually sees
/// regardless of monitor scaling.
/// </summary>
public static class BoxLayoutAdjuster
{
    /// <summary>How far each box's border expands outward from the raw field rect, on every side.</summary>
    public const double PaddingDip = 4;

    /// <summary>
    /// Two boxes are considered "vertically adjacent" (and get their
    /// facing edges snapped together) when they horizontally overlap and
    /// the gap between them, AFTER padding, is at or below this. Also
    /// catches boxes that overlap slightly (a negative "gap") once padded —
    /// snapping still resolves those to a single shared boundary rather
    /// than leaving them overlapping.
    /// </summary>
    public const double FlushGapThresholdDip = 14;

    /// <summary>Expands every rect outward by <paramref name="padding"/> DIPs on each side — order-independent, one rect at a time.</summary>
    public static IReadOnlyList<DipRect> ApplyPadding(IReadOnlyList<DipRect> rects, double padding = PaddingDip)
    {
        return rects.Select(rect => ApplyPadding(rect, padding)).ToList();
    }

    /// <summary>Single-rect overload of ApplyPadding (list overload above) — expands outward by <paramref name="padding"/> on each side.</summary>
    public static DipRect ApplyPadding(DipRect rect, double padding = PaddingDip)
    {
        return new DipRect(
            rect.X - padding,
            rect.Y - padding,
            rect.Width + (2 * padding),
            rect.Height + (2 * padding));
    }

    /// <summary>
    /// For every pair of rects that horizontally overlap AND whose
    /// vertical gap is at or below <paramref name="gapThreshold"/> (which
    /// also catches a small post-padding overlap — a negative gap), moves
    /// BOTH rects' facing edges to the midpoint of that gap: the upper
    /// rect's bottom edge and the lower rect's top edge end up exactly
    /// equal, one shared boundary line, never overlapping. Rects that
    /// don't horizontally overlap, or whose gap exceeds the threshold,
    /// are returned unchanged. Order-independent and chain-safe: a run of
    /// 3+ stacked rects ends up with every adjacent pair flush, regardless
    /// of which pair is resolved first, because each rect's UNCHANGED
    /// edge (its far/outer side) is always read fresh from the
    /// most-recently-adjusted state before computing the next midpoint.
    /// </summary>
    public static IReadOnlyList<DipRect> SnapFlushAdjacentEdges(IReadOnlyList<DipRect> rects, double gapThreshold = FlushGapThresholdDip)
    {
        var result = rects.ToArray();
        var n = result.Length;
        if (n < 2) return result;

        for (var i = 0; i < n; i++)
        {
            for (var j = i + 1; j < n; j++)
            {
                var (topIndex, bottomIndex) = result[i].Y <= result[j].Y ? (i, j) : (j, i);
                var top = result[topIndex];
                var bottom = result[bottomIndex];

                var horizontallyOverlaps = top.X < bottom.X + bottom.Width && bottom.X < top.X + top.Width;
                if (!horizontallyOverlaps) continue;

                var gap = bottom.Y - (top.Y + top.Height);
                if (gap > gapThreshold) continue;

                var midpoint = (top.Y + top.Height + bottom.Y) / 2.0;
                var bottomOuterEdge = bottom.Y + bottom.Height;

                result[topIndex] = top with { Height = midpoint - top.Y };
                result[bottomIndex] = bottom with { Y = midpoint, Height = bottomOuterEdge - midpoint };
            }
        }

        return result;
    }
}
