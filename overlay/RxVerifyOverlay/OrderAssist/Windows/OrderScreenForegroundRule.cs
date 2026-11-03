namespace RxVerifyOverlay.OrderAssist.Windows;

/// <summary>Whose window is in the foreground, for OrderScreenForegroundRule.</summary>
public enum ForegroundOwner
{
    /// <summary>No foreground window / lookup failed.</summary>
    Unknown,

    /// <summary>One of this overlay's own windows (control box, boxes, order highlight).</summary>
    OverlayProcess,

    /// <summary>A PioneerRx process window.</summary>
    PioneerRx,

    /// <summary>Any other application.</summary>
    OtherApp,
}

/// <summary>
/// Pure part of OrderScreenMonitor: "is the pharmacist ON an order screen
/// right now" (Will: "when ON the Create Recommended Order screen"). A
/// "Recommended Order..." window left open behind PioneerRx's main window
/// must not hold the app in Order mode while he verifies, so presence is
/// judged from the FOREGROUND window, not from the mere existence of an
/// order-titled window anywhere on the desktop.
///   - PioneerRx foreground: present iff that window, or its root owner (a
///     title-less message box / child dialog owned by the order window),
///     has an order-screen title. False otherwise -- the main window being
///     foreground means "not on the order screen" (and feeds the rule's
///     absent debounce).
///   - Our own overlay in the foreground (the user just clicked the control
///     box) or any other application (alt-tabbed to a browser): null = no
///     information, no mode change.
/// Titles can carry patient data: compared here, never stored or logged.
/// </summary>
public static class OrderScreenForegroundRule
{
    public static bool? Evaluate(ForegroundOwner owner, string? foregroundTitle, string? rootOwnerTitle)
    {
        if (owner != ForegroundOwner.PioneerRx) return null;

        return OrderAssistWindowClassifier.IsOrderScreenTitle(foregroundTitle)
            || OrderAssistWindowClassifier.IsOrderScreenTitle(rootOwnerTitle);
    }
}
