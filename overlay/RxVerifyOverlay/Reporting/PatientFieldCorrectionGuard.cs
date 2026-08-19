using System;
using System.Text;

namespace RxVerifyOverlay.Reporting;

/// <summary>
/// 2026-08-19 (Will verbatim: "On reporting an error on patient address,
/// it won't let me type anything in the box... Fix that"). Patient-field
/// corrections are now TYPED and TRANSMITTED (see RxReportBuilder.Build's
/// own doc for the policy this replaces — an earlier round unconditionally
/// withheld every patient-field correction). This is the automated safety
/// net that policy change needs: a pure check for whether the pharmacist's
/// typed free text actually CONTAINS the patient's real captured value (or
/// a meaningful fragment of it) — the one shape of typed text that would
/// itself BE the PHI leak this whole feature exists to prevent, as opposed
/// to a safe description like "address looks truncated" or "zip code
/// wrong".
///
/// HEURISTIC (deliberately simple and biased toward FALSE POSITIVES over
/// false negatives — see class doc's own EOF note): normalize both the
/// typed text and the patient's captured Source/Entered value (lowercase,
/// letters+digits only — strips spaces/punctuation so trivial reformatting
/// can't dodge the check), then look for ANY contiguous run of at least
/// <see cref="MinContiguousMatchLength"/> characters from the patient
/// value appearing anywhere in the typed text. This is order-independent
/// at the WORD level (each word's own internal run still matches even if
/// the pharmacist reorders whole words — "123 Main Street" typed as "Main
/// Street 123" still contains "mains"/"stree"/etc.) without needing real
/// fuzzy matching. A short captured value (under the match-length floor,
/// e.g. a 2-3 character piece) can never trip this check by construction
/// — the inline on-screen warning (Integrated/ReportErrorWindow.xaml
/// PatientFieldNoteText) is the PRIMARY defense; this is a secondary,
/// automated backstop, not a foolproof filter.
///
/// RxReportBuilder.Build is the ONLY caller that matters for safety (same
/// "single enforcement point regardless of what the UI does" posture as
/// the rest of that class) — Integrated/ReportErrorWindow.xaml.cs also
/// calls this live, on every keystroke, purely to show/hide its own
/// on-screen warning; that call is UI feedback only and is never trusted
/// as the actual safety decision.
/// </summary>
public static class PatientFieldCorrectionGuard
{
    /// <summary>Minimum contiguous character run (after normalization) that counts as "the patient's actual value leaked into this text" — see class doc.</summary>
    public const int MinContiguousMatchLength = 5;

    /// <summary>
    /// True if <paramref name="correction"/> contains a normalized
    /// contiguous run of at least <see cref="MinContiguousMatchLength"/>
    /// characters that also appears in <paramref name="sourceValue"/> OR
    /// <paramref name="enteredValue"/> — either one tripping is enough
    /// (both are the SAME patient's real captured data, just from
    /// different sides of the source/entered comparison).
    /// </summary>
    public static bool ContainsPatientValueFragment(string? correction, string? sourceValue, string? enteredValue)
    {
        var normalizedCorrection = Normalize(correction);
        if (normalizedCorrection.Length < MinContiguousMatchLength) return false;

        return ContainsAnyFragmentOf(normalizedCorrection, Normalize(sourceValue)) ||
               ContainsAnyFragmentOf(normalizedCorrection, Normalize(enteredValue));
    }

    private static bool ContainsAnyFragmentOf(string normalizedCorrection, string normalizedPatientValue)
    {
        if (normalizedPatientValue.Length < MinContiguousMatchLength) return false;

        var lastStart = normalizedPatientValue.Length - MinContiguousMatchLength;
        for (var i = 0; i <= lastStart; i++)
        {
            var fragment = normalizedPatientValue.Substring(i, MinContiguousMatchLength);
            if (normalizedCorrection.Contains(fragment, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>Lowercase, letters+digits only — strips spaces/punctuation/dashes so "123-Main St." vs "123 main st" (or any other trivial reformatting) still compares equal. Never null (empty string for null/blank input).</summary>
    private static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";

        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c)) builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }
}
