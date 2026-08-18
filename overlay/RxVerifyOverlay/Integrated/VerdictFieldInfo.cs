using RxVerifyOverlay.Diagnostics;
using RxVerifyOverlay.Models;

namespace RxVerifyOverlay.Integrated;

/// <summary>
/// Everything a verdict bar's hover tooltip / "Report error…" context menu
/// needs to describe ONE field — a deliberately small DTO, not a direct
/// reference to ViewModels.VerdictRowViewModel: IntegratedBoxesWindow
/// (the only consumer) stays decoupled from RxVerifyOverlay.ViewModels the
/// same way it was decoupled from it before this feature existed;
/// IntegratedOverlayCoordinator (which already references ViewModels) is
/// the one place that maps a VerdictRowViewModel onto this shape — see
/// UpdateBoxes.
///
/// PHI CAUTION: SourceValue/EnteredValue/Explanation are carried VERBATIM
/// (never redacted) — the whole point of the hover tooltip is showing the
/// pharmacist exactly what the engine compared, and a tooltip only ever
/// renders on-screen for that one workstation. Reporting/RxReportBuilder.cs
/// is the ONLY place these get redacted, and only for the 3 patient fields
/// (see IsPatientField below) — because that payload leaves the
/// workstation, unlike this tooltip.
/// </summary>
public sealed record VerdictFieldInfo(
    string FieldKey,
    string DisplayName,
    VerdictStatus Status,
    string SourceValue,
    string EnteredValue,
    string Explanation,
    string ReasonCode,
    // Trailing OPTIONAL diagnostics (2026-08-17 fix round, item 2) — never
    // required at any existing call site (all default to "not applicable"),
    // only ever meaningfully populated for FieldKey == "refills" by
    // Integrated/IntegratedOverlayCoordinator.cs UpdateBoxes, from
    // Uia/FieldReader.cs's RefillsTotalFillsLabelSeen/Prefix (see that
    // class's doc). Threaded through to Reporting/RxReportBuilder.cs so
    // the NEXT refills error report self-diagnoses whether the C# UIA
    // extraction path (Parsing/EscriptTreeParser.cs ParseRefills) even
    // SAW a Total-fills-shaped label, independent of whether it could use
    // it — see EscriptTreeParser.DetectTotalFillsLabel's doc. Label text
    // only (the matched FieldMap.TotalFillsKeyPrefixes constant) — never
    // the refill count/value itself.
    bool? RefillsTotalFillsLabelSeen = null,
    string? RefillsTotalFillsLabelPrefix = null)
{
    /// <summary>
    /// True for the 3 patient-identity fields (RxLogFormatter.IsPatientField).
    /// 2026-08-18 ("right-click must work on EVERY field"): IntegratedBoxesWindow's
    /// poll-driven right-click detection (PollCursorForHover) still raises
    /// ReportErrorRequested for these fields — it used to suppress the
    /// click entirely, which was itself confusing ("right-click just
    /// doesn't work here"). Instead Integrated/ReportErrorWindow.xaml.cs
    /// shows a redacted "[hidden — patient field]" placeholder in place of
    /// the real Source/Entered, and Reporting/RxReportBuilder.cs drops
    /// whatever free text the pharmacist typed into Correction, replacing
    /// it with a fixed reason code — the free-text correction box a
    /// pharmacist would type into is exactly the kind of PHI leak the
    /// owner's "NO patient fields in the payload" design decision (see
    /// HQ-ENDPOINT-SPEC.md) is trying to prevent, so that text is withheld
    /// from the payload rather than the whole affordance being hidden.
    /// </summary>
    public bool IsPatientField => RxLogFormatter.IsPatientField(FieldKey);
}
