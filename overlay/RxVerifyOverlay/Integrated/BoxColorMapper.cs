using System;
using System.Collections.Generic;
using RxVerifyOverlay.Models;

namespace RxVerifyOverlay.Integrated;

/// <summary>
/// Verdict -&gt; box-color mapping for the integrated boxes layer — a
/// deliberately BINARY collapse of the separate window's 3-color
/// (green/yellow/red) rendering, per the owner's explicit spec: Yellow
/// collapses to the same red "check it" box as Red, since drawing 3
/// distinct outline colors over live PioneerRx fields is more visual
/// noise than the integrated view is meant to add — the point is a fast
/// glance ("matches" vs. "check it"), not a 3-way read. MainWindow.xaml
/// (the separate window) is UNCHANGED and keeps rendering all 3 colors;
/// this mapping only ever feeds IntegratedBoxesWindow.
///
/// FIELD REPORT (owner, 2026-08-25, prescriberPhone): a source that was
/// never read at all ("(not provided)" — reasonCode "not_provided",
/// same shared code every comparator uses when a side is empty, see
/// FieldBarColorMapper's own doc) collapsed through IsGreenBox to the
/// same red "check it" box as a genuine mismatch. Owner, verbatim: "The
/// color displayed on the box should be yellow, not red." A field that
/// was never even read isn't "wrong" — there is nothing to check it
/// against — so it must never render as the same alarming red box a real
/// discrepancy gets. Per this file's own binary-color constraint (no
/// third color exists in this window's rendering — see
/// IntegratedBoxesWindow.xaml.cs), the correct binary answer for
/// "nothing was actually read" is to draw NO box at all here, the same
/// "don't add visual noise for a non-decision" treatment DawBoxRule
/// already gives a field that was never in play. ShouldDrawBox is a new,
/// additive check (used alongside DawBoxRule, never replacing it) that
/// IntegratedOverlayCoordinator's box-list Where-filter applies to every
/// field, not just DAW.
/// </summary>
public static class BoxColorMapper
{
    /// <summary>
    /// Same set FieldBarColorMapper.UnreadableReasonCodes uses for the
    /// separate window's Gray bucket — every comparator
    /// (src/normalize/*.ts, src/quantity/index.ts, src/sig/index.ts,
    /// src/drug/index.ts, src/daw/index.ts) emits these reasonCodes only
    /// when nothing reliable was actually read on one or both sides.
    /// </summary>
    private static readonly HashSet<string> UnreadableReasonCodes = new(StringComparer.Ordinal)
    {
        "not_provided",
        "unparseable_date",
        "unparseable_quantity"
    };

    /// <summary>True (draw a GREEN box) only for VerdictStatus.Green; false (draw a RED box) for Yellow, Red, or any other status value.</summary>
    public static bool IsGreenBox(VerdictStatus status) => status == VerdictStatus.Green;

    /// <summary>
    /// False when nothing was actually read for this field (see
    /// UnreadableReasonCodes) — no box should be drawn at all, regardless
    /// of status. True otherwise (every other field draws normally,
    /// through IsGreenBox above, unchanged).
    /// </summary>
    public static bool ShouldDrawBox(string? reasonCode) =>
        reasonCode == null || !UnreadableReasonCodes.Contains(reasonCode);
}
