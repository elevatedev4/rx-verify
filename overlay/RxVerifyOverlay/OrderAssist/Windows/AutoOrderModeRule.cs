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
/// EDGES, NOT LEVELS. The rule acts only when the order screen becomes the
/// active screen or stops being it, never merely because the current mode
/// differs from what the screen "should" use. So a manual dropdown pick is
/// never fought:
///   - on an order screen, picking Verify sticks until that screen is left;
///   - off any order screen, picking Order sticks until the next order
///     screen has come and gone (it then returns to Verify, as asked).
///
/// DEBOUNCE. Appearing is acted on at the first sample (fast). Leaving must
/// be seen for <see cref="AbsentSamplesBeforeRevert"/> consecutive fresh
/// samples (~0.8 s at the monitor's 400 ms cache) so a sibling dialog
/// replacing the Create Recommended Orders window cannot flap the mode.
///
/// REPORTS. While <c>suspended</c> (the Reports window is open) samples are
/// IGNORED -- no action and no state change -- so an order-titled window
/// opened by a report run cannot change the mode under it, and an edge that
/// happens while suspended is not lost: the first sample after resuming is
/// compared with the pre-suspension state. Example: auto-switched to Order,
/// Reports opens, the order screen closes, Reports closes -> the first
/// resumed samples are absent, the falling edge confirms, and the mode
/// reverts to Verify instead of sticking in Order with no screen.
/// <see cref="AutoSwitched"/> records whether the current Order mode was
/// entered by this rule rather than by a manual pick.
///
/// An unknown sample (<c>null</c>: detection failed, or the foreground window
/// is not PioneerRx -- see OrderScreenForegroundRule) changes nothing.
/// </summary>
public sealed class AutoOrderModeRule
{
    /// <summary>Consecutive "not on an order screen" samples required before reverting to Verify.</summary>
    public const int AbsentSamplesBeforeRevert = 2;

    private bool _screenPresent;
    private int _absentStreak;
    private bool _autoSwitched;

    /// <summary>True while the rule believes an order screen is up (after the last confirmed edge).</summary>
    public bool ScreenPresent => _screenPresent;

    /// <summary>True while the current Order mode was entered by this rule (not by a manual pick).</summary>
    public bool AutoSwitched => _autoSwitched;

    public AutoOrderModeAction Observe(bool? screenPresent, bool orderModeActive, bool suspended)
    {
        if (screenPresent is null || suspended) return AutoOrderModeAction.None;

        // Verify now (the user picked it, or we reverted): nothing of ours is in effect.
        if (!orderModeActive) _autoSwitched = false;

        if (screenPresent.Value)
        {
            _absentStreak = 0;
            if (_screenPresent) return AutoOrderModeAction.None; // already up: no edge

            _screenPresent = true; // rising edge
            if (orderModeActive) return AutoOrderModeAction.None;
            _autoSwitched = true;
            return AutoOrderModeAction.SwitchToOrder;
        }

        if (!_screenPresent) return AutoOrderModeAction.None; // still none: no edge

        _absentStreak++;
        if (_absentStreak < AbsentSamplesBeforeRevert) return AutoOrderModeAction.None;

        _screenPresent = false; // falling edge confirmed
        _absentStreak = 0;
        _autoSwitched = false;
        return orderModeActive ? AutoOrderModeAction.SwitchToVerify : AutoOrderModeAction.None;
    }
}
