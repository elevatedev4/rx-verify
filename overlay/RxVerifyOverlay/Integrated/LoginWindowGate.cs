using System;
using System.Collections.Generic;

namespace RxVerifyOverlay.Integrated;

/// <summary>
/// Pure decision logic for "hide every overlay part while PioneerRx's
/// login screen is up" (Will, 2026-10-22: hide the overlay when a window
/// whose title contains "Logon to PioneerRx" is visible). No Win32/WPF
/// dependency, so it is covered by fast xUnit tests — same pattern as
/// IntegratedVisibilityGate. LoginWindowMonitor is the only production
/// caller and supplies the actual window titles.
///
/// FAIL-SAFE: the hide is never permanent and never sticky. It lasts
/// exactly as long as the latest detection says the login window is
/// visible; an unknown/failed detection (<c>null</c>) is treated as "not
/// visible", i.e. today's behavior (overlay shown).
///
/// PHI: titles are only compared here, never stored or logged.
/// </summary>
public static class LoginWindowGate
{
    /// <summary>Case-insensitive substring that identifies PioneerRx's login window.</summary>
    public const string LoginTitleMarker = "Logon to PioneerRx";

    /// <summary>True when <paramref name="title"/> contains <see cref="LoginTitleMarker"/>, ignoring case. Null/empty/whitespace titles never match.</summary>
    public static bool IsLoginTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        return title.Contains(LoginTitleMarker, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when any of the supplied (visible top-level window) titles is a login title. A null sequence or null entries never match.</summary>
    public static bool AnyLoginTitle(IEnumerable<string?>? titles)
    {
        if (titles is null) return false;
        foreach (var title in titles)
        {
            if (IsLoginTitle(title)) return true;
        }
        return false;
    }

    /// <summary>
    /// Processes whose windows never count as the PioneerRx login window
    /// (a browser tab or mail subject can contain the phrase). Matched
    /// case-insensitively, with or without ".exe". The PioneerRx process is
    /// deliberately NOT required: an allow-list could silently disable the
    /// feature if its process name differs.
    /// </summary>
    private static readonly string[] ExcludedProcessNames =
    {
        "chrome", "msedge", "firefox", "brave", "opera", "iexplore", "outlook",
    };

    /// <summary>True for browser/mail-client process names (see ExcludedProcessNames). Null/empty is NOT excluded.</summary>
    public static bool IsExcludedProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;

        var name = processName.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];

        foreach (var excluded in ExcludedProcessNames)
        {
            if (string.Equals(name, excluded, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// A window counts as "the login window is up" only when it is visible,
    /// NOT minimized and NOT cloaked, and its title matches. A lingering
    /// minimized/cloaked "Logon" window must not keep the overlay hidden.
    /// (A failed cloak query is passed in as isCloaked: false.)
    /// </summary>
    public static bool IsLoginWindowCandidate(bool isVisible, bool isMinimized, bool isCloaked, string? title)
    {
        return isVisible && !isMinimized && !isCloaked && IsLoginTitle(title);
    }

    /// <summary>
    /// The overlay is hidden only on a positive detection. <c>null</c>
    /// means detection failed/unknown and must fall back to showing.
    /// </summary>
    public static bool ShouldHideOverlay(bool? loginWindowVisible) => loginWindowVisible == true;
}

/// <summary>What changed on an observation: nothing, the overlay should now be hidden, or it should be restored.</summary>
public enum LoginGateTransition
{
    None,
    Hide,
    Restore,
}

/// <summary>
/// Tiny show/hide state machine over LoginWindowGate.ShouldHideOverlay,
/// so callers can log/act on edges only. login appears -> Hidden;
/// login gone -> restored; detection error (null) -> restored (shown).
/// </summary>
public sealed class LoginGateState
{
    /// <summary>True while the overlay should currently be hidden because the login window is up.</summary>
    public bool IsHidden { get; private set; }

    /// <summary>Feeds the latest detection (<c>null</c> = detection failed) and reports the edge, if any.</summary>
    public LoginGateTransition Observe(bool? loginWindowVisible)
    {
        var shouldHide = LoginWindowGate.ShouldHideOverlay(loginWindowVisible);
        if (shouldHide == IsHidden) return LoginGateTransition.None;

        IsHidden = shouldHide;
        return shouldHide ? LoginGateTransition.Hide : LoginGateTransition.Restore;
    }
}
