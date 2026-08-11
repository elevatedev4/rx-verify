using RxVerifyOverlay.Models;

namespace RxVerifyOverlay.Integrated;

/// <summary>
/// OWNER FEEDBACK (round 4, item 5): don't draw a verdict box around the
/// DAW field at all unless DAW is either WRONG or actually "in play" for
/// this Rx — a green box around a field the pharmacist never needed to
/// think about (substitution is allowed, DAW wasn't checked) is just
/// visual noise; a box that's there because DAW genuinely mattered (the
/// source disallows substitution, or the pharmacist deliberately checked
/// DAW) is worth confirming, in either color.
///
/// DERIVATION: rx-verify's compareDaw (src/daw/index.ts) does NOT encode
/// "the pharmacist voluntarily checked DAW even though substitution was
/// allowed" as its own reasonCode — that combination still comes back
/// green/'substitution_allowed', identical to the "never touched DAW at
/// all" case. So this predicate can't key off Status+ReasonCode alone; it
/// reads the two EXACT display strings the engine already formats for
/// this row (src/engine/index.ts stringifyDaw/stringifySubstitutionIndicator),
/// already flowing through VerdictRowViewModel.EnteredValue/SourceValue.
/// If those exact strings ever change on the TS side, this predicate
/// needs updating in lockstep — flagged here deliberately since it's the
/// one seam where this couples to display text.
/// </summary>
public static class DawBoxRule
{
    /// <summary>Exact text from src/engine/index.ts stringifyDaw(true) — VerdictRowViewModel.EnteredValue for "daw" when the entered checkbox is checked.</summary>
    public const string EnteredDawCheckedText = "DAW checked";

    /// <summary>Exact text from src/engine/index.ts stringifySubstitutionIndicator(true) — VerdictRowViewModel.SourceValue for "daw" when the source e-script disallows substitution.</summary>
    public const string SourceSubstitutionNotAllowedText = "Substitution NOT allowed (DAW)";

    /// <summary>
    /// True when a box should be drawn around the DAW field:
    ///   (a) the verdict isn't Green (wrong, or needs a look — always shown), OR
    ///   (b) DAW is "in play" — the entered DAW checkbox is checked, or the
    ///       source e-script disallows substitution (then shown normally,
    ///       green included).
    /// False (no box at all) only when the verdict is Green AND neither of
    /// those "in play" conditions holds — i.e. substitution is allowed and
    /// the pharmacist never checked DAW, so the field was never a decision
    /// point for this Rx.
    /// </summary>
    public static bool ShouldDrawBox(VerdictStatus status, string? enteredValue, string? sourceValue)
    {
        if (status != VerdictStatus.Green) return true;

        var enteredDawChecked = enteredValue == EnteredDawCheckedText;
        var sourceDisallowsSubstitution = sourceValue == SourceSubstitutionNotAllowedText;

        return enteredDawChecked || sourceDisallowsSubstitution;
    }
}
