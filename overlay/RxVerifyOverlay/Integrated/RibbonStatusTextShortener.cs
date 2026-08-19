using System;

namespace RxVerifyOverlay.Integrated;

/// <summary>
/// 2026-08-19 (Will verbatim: "Just say 'waiting' instead of the long
/// string about waiting for a precheck. The current string is hiding all
/// the buttons."). Pure title -&gt; short-label mapping for
/// ControlBoxWindow.xaml.cs SetStatusMessage — the compact ribbon's
/// StatusTimeText shows a SHORTENED rendering of whatever full
/// OverlayViewModel.StatusMessage string it's given; MainWindow.xaml's own
/// StatusMessage binding is untouched by this class entirely and keeps
/// showing the full text (it lives in its own, much wider window).
///
/// ROOT CAUSE (traced, not guessed): ControlBoxWindow.xaml's status row is
/// a DockPanel with LastChildFill="False" inside a FIXED Width="280"
/// window. StatusTimeText is docked Left with no explicit Width/MaxWidth
/// — its TextTrimming="CharacterEllipsis" only ever engages when a
/// TextBlock's rendered width is actually CONSTRAINED, which this one
/// never was; it just grows to fit however long the string is. On a long
/// message ("Waiting for a PioneerRx Pre-Check/Edit/New Rx window..." is
/// 57 characters) that pushes the Dock=Right icon buttons (Refresh/
/// Feedback/Settings) out past the window's own fixed 280px edge —
/// exactly "hiding all the buttons". See ControlBoxWindow.xaml's own
/// StatusTimeText for the companion fix (a real MaxWidth, so
/// TextTrimming actually does something as a belt-and-suspenders safety
/// net for any FUTURE status string this class doesn't yet know about).
///
/// COVERAGE: every OverlayViewModel.StatusMessage call site was read
/// directly (ViewModels/OverlayViewModel.cs) and given an explicit prefix
/// mapping below for its known, fixed-text lead-in. The three call sites
/// that surface a raw, fully dynamic message (ocrResult.Error/
/// fastResult.Error/result.Error, and reader.SourceUnavailableReason when
/// non-null) have no fixed shape to prefix-match — those fall through to
/// the generic length-capped fallback instead.
/// </summary>
public static class RibbonStatusTextShortener
{
    /// <summary>Checked in order, first match wins — see class doc's COVERAGE section for where each of these strings actually comes from.</summary>
    private static readonly (string Prefix, string Short)[] KnownPrefixes =
    {
        ("Waiting for a PioneerRx", "Waiting"),
        ("Checked ", "Checked"),
        ("Rx is not an escript", "Not escript"),
        ("OCR didn't find enough text", "No OCR text"),
        ("No PioneerRx window found", "No window"),
        ("Open the Escript tab", "Open Escript"),
        ("UIA source read failed", "UIA error"),
        ("UIA read failed", "UIA error"),
        ("Drug lookup failed", "Lookup error"),
    };

    /// <summary>Character budget for anything NOT matched above (a raw dynamic error message) — generous enough to still read as words, short enough that "RxVerify" + this + the icon row never exceeds the ribbon's fixed 280px width. Tuned alongside ControlBoxWindow.xaml's StatusTimeText.MaxWidth, not independently.</summary>
    private const int FallbackMaxLength = 14;

    /// <summary>Never null; "" for null/empty input (nothing to show either way).</summary>
    public static string Shorten(string? fullStatusMessage)
    {
        if (string.IsNullOrEmpty(fullStatusMessage)) return "";

        foreach (var (prefix, shortLabel) in KnownPrefixes)
        {
            if (fullStatusMessage.StartsWith(prefix, StringComparison.Ordinal)) return shortLabel;
        }

        return fullStatusMessage.Length <= FallbackMaxLength
            ? fullStatusMessage
            : fullStatusMessage[..FallbackMaxLength].TrimEnd() + "…";
    }
}
