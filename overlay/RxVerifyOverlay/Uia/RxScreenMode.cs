using System;

namespace RxVerifyOverlay.Uia;

/// <summary>
/// Which of PioneerRx's 3 Rx-editing screens a window title identifies
/// (see FieldMap.TargetWindowTitlePrefixes: "Pre-Check Rx", "Edit Rx",
/// "New Rx" — the ONLY titles PioneerRxWindow.TryAttach ever attaches to
/// in the first place, so a real PioneerRxWindow instance's title should
/// always classify as one of the three named values). Added 2026-08-18
/// (Will, verbatim: "RxVerify verify mode should only do checks when in
/// Pre-Check mode (from title bar), not when in other modes, like Edit
/// Rx") — see Integrated/PreCheckModeGate.cs for how this drives whether
/// the verify boxes/hover/right-click layer runs at all.
/// </summary>
public enum RxScreenMode
{
    /// <summary>Title didn't classify as any known screen (null/empty/unreadable title, or text that doesn't match any of the 3 known patterns — see RxScreenModeClassifier.Classify's own doc for why this is treated as "keep checks running" rather than "suppress").</summary>
    Unknown,

    PreCheck,
    EditRx,
    NewRx
}

/// <summary>
/// Pure title -&gt; RxScreenMode classifier — see RxScreenMode's own doc for
/// why this exists. No UIA/Win32 dependency, so it's directly
/// unit-testable; see RxVerifyOverlay.Tests/RxScreenModeClassifierTests.cs.
/// </summary>
public static class RxScreenModeClassifier
{
    /// <summary>
    /// TOLERANT substring match, deliberately NOT the strict
    /// StartsWith(prefix)-against-FieldMap.TargetWindowTitlePrefixes
    /// pattern PioneerRxWindow.TryAttach itself uses to decide WHETHER to
    /// attach at all — a prior bug here (over-strict hardcoded title
    /// prefixes breaking on a real 2-Pioneer-instance workstation) is
    /// exactly the failure mode this branch was warned to avoid
    /// reintroducing. Once TryAttach has already found a window (whose
    /// title is confirmed to start with one of the 3 known prefixes),
    /// this only needs to tell PreCheck apart from the other two — a
    /// case-insensitive Contains on "pre-check"/"precheck" (both spellings,
    /// in case a different Pioneer install/version/locale renders the
    /// hyphen differently — the confirmed real title is "Pre-Check Rx -
    /// &lt;rx number&gt; - ...", see FieldMap.cs's own doc) is tolerant of
    /// trailing pharmacy-name/suffix text TryAttach's own StartsWith
    /// already didn't care about, and of whatever surrounds the screen
    /// name as long as the substring itself is present.
    ///
    /// Checked BEFORE "edit rx"/"new rx" — the three are mutually
    /// exclusive substrings in every confirmed real title, so order only
    /// matters defensively (a hypothetical title containing more than one
    /// of the three phrases resolves to PreCheck first, the actual mode
    /// this gate cares most about getting right).
    /// </summary>
    public static RxScreenMode Classify(string? windowTitle)
    {
        if (string.IsNullOrWhiteSpace(windowTitle)) return RxScreenMode.Unknown;

        if (Contains(windowTitle, "pre-check") || Contains(windowTitle, "precheck"))
        {
            return RxScreenMode.PreCheck;
        }

        if (Contains(windowTitle, "edit rx"))
        {
            return RxScreenMode.EditRx;
        }

        if (Contains(windowTitle, "new rx"))
        {
            return RxScreenMode.NewRx;
        }

        return RxScreenMode.Unknown;
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
}
