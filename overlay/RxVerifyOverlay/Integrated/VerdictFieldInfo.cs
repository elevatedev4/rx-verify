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
    string ReasonCode)
{
    /// <summary>
    /// True for the 3 patient-identity fields (RxLogFormatter.IsPatientField)
    /// — IntegratedBoxesWindow hides the "Report error…" menu item entirely
    /// for these (see BuildHotspotContextMenu), rather than merely
    /// redacting the prefilled Source/Entered in the dialog: the free-text
    /// correction box a pharmacist would type into next is exactly the
    /// kind of PHI leak the owner's "NO patient fields in the payload"
    /// design decision (see HQ-ENDPOINT-SPEC.md) is trying to prevent, and
    /// there is no safe way to auto-redact free text someone is actively
    /// composing.
    /// </summary>
    public bool IsPatientField => RxLogFormatter.IsPatientField(FieldKey);
}
