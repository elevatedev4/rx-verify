namespace RxVerifyOverlay.Integrated;

/// <summary>
/// ROUND 3 FIX: the signal FallbackSeparateWindowRule's isPioneerAttached
/// parameter actually needs — "does PioneerRx exist ANYWHERE on the
/// system" — is deliberately a DIFFERENT, broader question than
/// IntegratedVisibilityGate.ShouldShowControlBox's
/// isPioneerForegroundApp ("is PioneerRx the app the pharmacist is
/// CURRENTLY looking at").
///
/// THE BUG THIS CLOSES: round 2 wired hasForegroundPioneerWindow (the
/// control box's own signal) straight into the fallback rule too.
/// Conflating "not in front right now" with "doesn't exist" meant the
/// fallback separate window popped up: (a) at every launch, since
/// whatever process started the app (PowerShell, a shortcut, Explorer)
/// is the foreground window at that instant, even with PioneerRx already
/// open in the background; and (b) on every alt-tab away from PioneerRx
/// to check something else, even briefly — exactly the "invisible app"
/// UX the fallback was never meant to interrupt for. See
/// FallbackSeparateWindowRule's own doc for the ORIGINAL regression this
/// mechanism guards against (leaving Integrated mode from the
/// fallback-shown state) — that guard is untouched by this fix.
///
/// Pure combination of three independently-cheap-or-already-computed
/// signals (no WPF/UIA/Win32 dependency itself — IntegratedOverlayCoordinator
/// supplies the actual booleans), so it's covered by fast xUnit tests,
/// same pattern as every other decision class in this folder.
/// </summary>
public static class PioneerPresence
{
    /// <summary>
    /// True if PioneerRx exists ANYWHERE on the system right now, by any
    /// of three signals: a Pre-Check/Edit/New-Rx window is specifically
    /// attached (<paramref name="isRxScreenAttached"/>), PioneerRx is the
    /// current foreground application regardless of screen
    /// (<paramref name="hasForegroundPioneerWindow"/>), or a PioneerRx
    /// process is simply running somewhere, foreground or not
    /// (<paramref name="hasBackgroundPioneerProcess"/> — the new check,
    /// see IntegratedOverlayCoordinator.DoesPioneerRxProcessExist).
    /// </summary>
    public static bool Exists(bool isRxScreenAttached, bool hasForegroundPioneerWindow, bool hasBackgroundPioneerProcess)
    {
        return isRxScreenAttached || hasForegroundPioneerWindow || hasBackgroundPioneerProcess;
    }
}
