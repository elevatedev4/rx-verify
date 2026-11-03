namespace RxVerifyOverlay.OrderAssist.Windows;

/// <summary>What the host should do after an observation.</summary>
public enum AutoOrderModeAction
{
    None,
    SwitchToOrder,
    SwitchToVerify,
}

/// <summary>
/// Pure, edge-triggered decision behind automatic Order/Verify mode
/// switching (Will: "The app should change to order mode automatically when
/// on the Create Recommended Order screen and Recommended order screens,
/// then change back to Verify when it's not."). No Win32/WPF dependency;
/// OrderScreenMonitor supplies the observations and MainWindow applies the
/// action.
///
/// EDGES, NOT LEVELS. The rule acts only when an order screen APPEARS or
/// DISAPPEARS, never merely because the current mode differs from what the
/// screen "should" use. So a manual dropdown pick is never fought:
///   - on an order screen, picking Verify sticks until that screen closes;
///   - off any order screen, picking Order sticks until the next order
///     screen has come and gone (it then returns to Verify, as asked).
///
/// DEBOUNCE. Appearing is acted on at the first sample (fast). Disappearing
/// must be seen for <see cref="AbsentSamplesBeforeRevert"/> consecutive fresh
/// samples (~0.8 s at the monitor's 400 ms cache) so a sibling dialog
/// replacing the Create Recommended Orders window cannot flap Order ->
/// Verify -> Order.
///
/// REPORTS. While <c>suspended</c> (the Reports window is open) the rule
/// still tracks presence but never returns an action, so an order-titled
/// window opened by a report run cannot change the mode under it.
///
/// An unknown sample (<c>null</c>, detection failed) changes nothing.
/// </summary>
public sealed class AutoOrderModeRule
{
    /// <summary>Consecutive "no order screen" samples required before reverting to Verify.</summary>
    public const int AbsentSamplesBeforeRevert = 2;

    private bool _screenPresent;
    private int _absentStreak;

    /// <summary>True while the rule believes an order screen is up (after the last confirmed edge).</summary>
    public bool ScreenPresent => _screenPresent;

    public AutoOrderModeAction Observe(bool? screenPresent, bool orderModeActive, bool suspended)
    {
        if (screenPresent is null) return AutoOrderModeAction.None;

        if (screenPresent.Value)
        {
            _absentStreak = 0;
            if (_screenPresent) return AutoOrderModeAction.None; // already up: no edge

            _screenPresent = true; // rising edge
            if (suspended) return AutoOrderModeAction.None;
            return orderModeActive ? AutoOrderModeAction.None : AutoOrderModeAction.SwitchToOrder;
        }

        if (!_screenPresent) return AutoOrderModeAction.None; // still none: no edge

        _absentStreak++;
        if (_absentStreak < AbsentSamplesBeforeRevert) return AutoOrderModeAction.None;

        _screenPresent = false; // falling edge confirmed
        _absentStreak = 0;
        if (suspended) return AutoOrderModeAction.None;
        return orderModeActive ? AutoOrderModeAction.SwitchToVerify : AutoOrderModeAction.None;
    }
}
