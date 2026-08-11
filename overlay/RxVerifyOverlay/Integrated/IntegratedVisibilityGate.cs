namespace RxVerifyOverlay.Integrated;

/// <summary>
/// Pure decision logic for whether the integrated boxes layer / control
/// box should be visible right now. See IntegratedOverlayCoordinator,
/// the only production caller — kept here as a standalone boolean-in/
/// boolean-out class (no WPF/UIA dependency) so it's covered by fast
/// xUnit tests, same pattern as Uia/AttachCacheDecision.cs and
/// Ocr/OcrSourceUsability.cs.
/// </summary>
public static class IntegratedVisibilityGate
{
    /// <summary>
    /// The boxes layer must be hidden whenever ANY of: PioneerRx isn't
    /// currently attached, PioneerRx isn't the OS foreground window
    /// (otherwise boxes would float over whatever app the pharmacist
    /// switched to), PioneerRx isn't maximized (integrated mode is
    /// MAXIMIZED-ONLY per the owner's spec), or there's nothing verified
    /// yet to draw boxes for (no category has data yet, or the current
    /// screen isn't a parseable escript — mirrors OverlayViewModel's
    /// existing non-escript blank-state signal, see
    /// IntegratedOverlayCoordinator for how hasVerifiableContent is
    /// computed from OverlayViewModel.Categories/HasNonEscriptMessage).
    /// </summary>
    public static bool ShouldShowBoxes(bool isAttached, bool isForeground, bool isMaximized, bool hasVerifiableContent)
    {
        return isAttached && isForeground && isMaximized && hasVerifiableContent;
    }

    /// <summary>
    /// The control box stays visible any time PioneerRx is attached and
    /// foreground, REGARDLESS of maximized state — when not maximized it
    /// switches to the "maximize to use integrated view" note with most
    /// controls disabled (see ControlBoxWindow.SetMaximizedGuardState)
    /// rather than disappearing entirely, since the pharmacist still
    /// needs the display-mode toggle to switch back to Separate.
    /// </summary>
    public static bool ShouldShowControlBox(bool isAttached, bool isForeground)
    {
        return isAttached && isForeground;
    }
}
