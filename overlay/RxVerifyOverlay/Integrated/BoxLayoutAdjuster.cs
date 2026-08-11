using System.Collections.Generic;
using System.Linq;

namespace RxVerifyOverlay.Integrated;

/// <summary>
/// Pure geometry for the integrated boxes layer's visual polish. Round 2
/// (owner feedback: "a little too busy looking and hard to read") added
/// outward expansion from the raw UIA rect plus flush-snapping of
/// vertically-stacked boxes. Round 4 (owner feedback: boxes must EXACTLY
/// surround the input area so Pioneer's own gray field border is hidden
/// underneath the colored one) shrank that expansion from "breathing
/// room" (4px) down to a thin OUTSET (2px) — just enough for the colored
/// stroke to sit on/over the native border, per UIA's BoundingRectangle
/// already being the control's OUTER edge including that border. No
/// WPF/UIA dependency — plain DipRect in, DipRect out — so this is
/// covered by fast xUnit tests, same pattern as DpiRectConverter/
/// IntegratedVisibilityGate. IntegratedBoxesWindow (the only production
/// caller) applies both AFTER DpiRectConverter, so the thresholds below
/// are in DIPs, matching what the owner actually sees regardless of
/// monitor scaling.
/// </summary>
public static class BoxLayoutAdjuster
{
    /// <summary>
    /// How far each box's border expands outward from the raw field rect,
    /// on every side — round 4: shrunk from 4 to 2 so the colored stroke
    /// overlaps Pioneer's own native border rather than sitting outside
    /// it with visible gray showing through. An estimate (like the
    /// original 4px), not measured against a live workstation — retune
    /// here if the overlap still isn't tight enough in practice.
    /// </summary>
    public const double PaddingDip = 2;

    /// <summary>
    /// Two boxes are considered "vertically adjacent" (and get their
    /// facing edges snapped together) when they horizontally overlap and
    /// the gap between them, AFTER padding, is at or below this. Also
    /// catches boxes that overlap slightly (a negative "gap") once padded —
    /// snapping still resolves those to a single shared boundary rather
    /// than leaving them overlapping. Round 4: halved from 14 to 7 in step
    /// with PaddingDip halving from 4 to 2 — each box now closes roughly
    /// half as much of a given raw gap via padding alone (4px total vs.
    /// 8px total per pair), so the threshold that decides "close enough to
    /// snap" needs to shrink proportionally too, or pairs that used to
    /// land just inside the old threshold would now land just outside it
    /// and stop snapping. Still an estimate pending a live workstation
    /// check, same as PaddingDip.
    /// </summary>
    public const double FlushGapThresholdDip = 7;

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

    /// <summary>
    /// How close two boxes' left (X) edges need to be to be considered
    /// candidates for the same visual column — owner's own suggestion
    /// ("~8 DIP"). Small enough that it only catches genuine near-misses
    /// (a few px of UIA-rect jitter between fields PioneerRx actually laid
    /// out in the same column), not different columns that happen to be
    /// roughly similar widths apart.
    /// </summary>
    public const double LeftEdgeAlignmentToleranceDip = 8;

    /// <summary>
    /// How far apart (vertically — overlap counts as 0-or-negative, always
    /// within tolerance) two near-left boxes can be and still count as
    /// "stacked in the same column" rather than "two unrelated fields that
    /// happen to share an X coordinate". OWNER'S OWN EXAMPLE ("some of
    /// them will be off by themselves") implies vertical proximity DOES
    /// matter — two fields on opposite ends of the panel with coincidentally
    /// matching left edges are not "the ones that should be lined up".
    /// This is deliberately more generous than FlushGapThresholdDip (7):
    /// that threshold decides "close enough to make edges literally
    /// touch"; this one decides "close enough to still read as the same
    /// vertical column", which comfortably needs to bridge normal
    /// category-to-category gaps (e.g. the last Patient field to the first
    /// Prescriber field) without also bridging genuinely unrelated,
    /// far-apart screen regions. An estimate, like every other geometry
    /// constant in this class — retune against a live workstation.
    /// </summary>
    public const double ColumnVerticalGapToleranceDip = 40;

    /// <summary>
    /// OWNER FEEDBACK (round 5 — "make the left sides of the rectangles
    /// match up so they all line up when looking down (for the ones that
    /// should be lined up). Some of them will be off by themselves"):
    /// groups boxes into visual columns — two boxes join the same column
    /// when their left edges are within <paramref name="leftTolerance"/>
    /// AND they're vertically compatible (overlapping, or a gap at or
    /// below <paramref name="verticalGapTolerance"/>) — and snaps every
    /// column's members to the column's MINIMUM left edge (never a
    /// median/average: moving a box's left edge to something LARGER than
    /// its own original X would shift the border rightward, clipping into
    /// the field's own text; moving to the group minimum only ever
    /// extends a box further left into blank space, which is purely
    /// cosmetic and can never clip real content). Grouping is TRANSITIVE —
    /// a box joins a column by being compatible with ANY box already in
    /// it, not just the first one — so a long run of many stacked fields
    /// still ends up as one column even though its first and last members
    /// might individually be too far apart to pair up directly. Boxes
    /// with no left-edge neighbor at all end up as a column of one and
    /// are returned completely unchanged ("off by themselves").
    ///
    /// RIGHT EDGES NEVER MOVE (owner didn't ask for that, explicitly):
    /// each aligned box's Width grows by exactly however far its left
    /// edge moved, so <c>X + Width</c> (the right edge) is mathematically
    /// identical to the box's value before this call — only the left
    /// edge position (and, as a pure consequence of keeping the right
    /// edge fixed, the width) changes.
    ///
    /// ORDERING (called AFTER SnapFlushAdjacentEdges, never before):
    /// running this AFTER the flush snap means (a) the flush snap's own
    /// horizontal-overlap test still runs against the padded-but-not-yet-
    /// left-shifted rects, so this pass can't retroactively create a
    /// false horizontal overlap that changes what the flush snap decided,
    /// and (b) a column the flush snap already made vertically flush
    /// (gap exactly 0) is trivially well within ANY vertical tolerance
    /// here, making column detection MORE reliable for exactly the
    /// tightly-stacked groups this feature targets. IntegratedBoxesWindow.
    /// SetBoxes is the only production caller and applies the three
    /// passes in this order: ApplyPadding -&gt; SnapFlushAdjacentEdges -&gt;
    /// AlignColumnLeftEdges.
    /// </summary>
    public static IReadOnlyList<DipRect> AlignColumnLeftEdges(IReadOnlyList<DipRect> rects, double leftTolerance = LeftEdgeAlignmentToleranceDip, double verticalGapTolerance = ColumnVerticalGapToleranceDip)
    {
        var result = rects.ToArray();
        var n = result.Length;
        if (n < 2) return result;

        var visited = new bool[n];

        for (var start = 0; start < n; start++)
        {
            if (visited[start]) continue;

            // Flood-fill: grow the column from `start` by repeatedly
            // adding any not-yet-grouped box that's compatible with ANY
            // box ALREADY in the column (transitive membership — see doc).
            var column = new List<int> { start };
            visited[start] = true;

            var addedNew = true;
            while (addedNew)
            {
                addedNew = false;
                for (var candidate = 0; candidate < n; candidate++)
                {
                    if (visited[candidate]) continue;

                    var joinsColumn = column.Any(member =>
                        Math.Abs(rects[candidate].X - rects[member].X) <= leftTolerance &&
                        VerticalGapBetween(rects[candidate], rects[member]) <= verticalGapTolerance);

                    if (!joinsColumn) continue;

                    column.Add(candidate);
                    visited[candidate] = true;
                    addedNew = true;
                }
            }

            if (column.Count < 2) continue; // "off by itself" — no alignment partner, leave untouched

            var minX = column.Min(idx => rects[idx].X);
            foreach (var idx in column)
            {
                var rect = result[idx];
                var deltaX = rect.X - minX; // >= 0: how far right of the column's minimum this box currently sits
                result[idx] = rect with { X = minX, Width = rect.Width + deltaX }; // right edge (X + Width) unchanged — see doc
            }
        }

        return result;
    }

    /// <summary>Signed vertical gap between two rects — zero or negative means they vertically overlap (or touch), which is always "within tolerance" for any non-negative tolerance value.</summary>
    private static double VerticalGapBetween(DipRect a, DipRect b)
    {
        return a.Y <= b.Y
            ? b.Y - (a.Y + a.Height)
            : a.Y - (b.Y + b.Height);
    }
}
