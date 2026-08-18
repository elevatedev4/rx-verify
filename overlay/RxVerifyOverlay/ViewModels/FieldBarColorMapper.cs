using System;
using System.Collections.Generic;
using RxVerifyOverlay.Models;

namespace RxVerifyOverlay.ViewModels;

/// <summary>
/// Display color for one field's bar in MainWindow.xaml's compact table
/// (the per-field row Border background + leading glyph foreground) — a
/// finer-grained rendering than the engine's own 3-value VerdictStatus.
/// </summary>
public enum FieldBarColor
{
    Green,
    Yellow,
    Gray,
    Red
}

/// <summary>
/// Verdict -&gt; field-bar-color mapping for MainWindow.xaml's row template
/// (VerdictRowViewModel.BarColor). Per Will's live-test feedback (2026-08-17,
/// verbatim: "Make the field bar yellow if it was not able to be read, red
/// if it's wrong", corrected to "Actually make it gray if it wasn't able to
/// be read"): Green and Red pass straight through from VerdictStatus
/// unchanged (a genuine match stays green; the engine's own mismatches —
/// e.g. date_mismatch, quantity_mismatch, drug_mismatch, sig_mismatch,
/// surname_mismatch, npi_mismatch, daw_required — already carry
/// VerdictStatus.Red, so no reasonCode branching is needed there). Only
/// VerdictStatus.Yellow is split further, by ReasonCode, into two visually
/// distinct buckets:
///
///   GRAY — the field could not actually be READ/compared at all: either
///   side was empty (every comparator's shared "not_provided" reasonCode)
///   or the raw text present couldn't even be parsed into a comparable
///   value ("unparseable_date", "unparseable_quantity" — quantity/refills
///   text that isn't a number). There is no real signal here, so it must
///   not share yellow's "read both sides, use your judgment" color.
///
///   YELLOW (stays) — both sides WERE read into real, displayable values,
///   and the engine is deliberately flagging the comparison for a human
///   glance rather than calling it green or red. This is everything else:
///     - fuzzy/partial identity matches: suffix_dropped, middle_name_present,
///       surname_partial, given_name_partial, nickname_match (names);
///       sig_ambiguous (sig — structure parsed but indeterminate, or
///       leftover text on only one side); unknown_drug, pack_size,
///       strength_unverified, generic_substitution (drug — resolved
///       concepts that need confirming, not unreadable ones)
///     - deliberately-capped-severity differences: address_differs,
///       unit_differs (address mismatches are informational only per the
///       engine's own "address alone does not block dispensing" policy —
///       normalize/address.ts never returns Red for an address field at
///       all), phone_differs, phone_ocr_suspect (same "never escalate,
///       still show the discrepancy" policy for prescriber phone)
///     - reconciled-but-worth-a-look: quantity_adjusted (differs numerically
///       but the sig math explains it)
///     - PENDING_DRUG_LOOKUP_REASON_CODE ("pending_lookup") — not a verdict
///       at all, a transient "still computing" placeholder for the drug row
///       while the background NDC lookup runs (see
///       VerdictRowViewModel.IsPending, which already swaps this row's
///       glyph for a spinner); it will be replaced by a real status within
///       the same refresh, so it must never render as unreadable-gray.
///
/// This mapper only feeds MainWindow.xaml's per-row bar — the integrated
/// boxes layer (Integrated/BoxColorMapper.cs) is a DELIBERATE binary
/// green/red collapse per an earlier, separate spec from Will and is
/// unaffected by this change.
/// </summary>
public static class FieldBarColorMapper
{
    /// <summary>
    /// ReasonCodes meaning "nothing reliable was actually read" across every
    /// comparator (src/normalize/name.ts, src/normalize/address.ts,
    /// src/normalize/date.ts, src/quantity/index.ts, src/sig/index.ts,
    /// src/drug/index.ts, src/daw/index.ts all emit "not_provided" the same
    /// way) plus the two "read text but couldn't parse it into a value"
    /// codes (src/normalize/date.ts, src/quantity/index.ts).
    /// </summary>
    private static readonly HashSet<string> UnreadableReasonCodes = new(StringComparer.Ordinal)
    {
        "not_provided",
        "unparseable_date",
        "unparseable_quantity"
    };

    public static FieldBarColor Classify(VerdictStatus status, string? reasonCode)
    {
        switch (status)
        {
            case VerdictStatus.Green:
                return FieldBarColor.Green;
            case VerdictStatus.Red:
                return FieldBarColor.Red;
            default:
                // Yellow (or any future/unmapped status defaults to yellow's
                // treatment, same fallback posture as Glyph/StatusText
                // elsewhere in this file).
                if (reasonCode != null && UnreadableReasonCodes.Contains(reasonCode))
                {
                    return FieldBarColor.Gray;
                }
                return FieldBarColor.Yellow;
        }
    }
}
