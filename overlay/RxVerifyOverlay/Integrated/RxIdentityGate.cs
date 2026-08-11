namespace RxVerifyOverlay.Integrated;

/// <summary>
/// ADDENDUM item 7 (priority — "stale green boxes over a NEW prescription
/// is a false-assurance hazard"): pure staleness check between the Rx
/// PioneerRx is CURRENTLY showing and the Rx the displayed verdicts were
/// actually computed for. The integrated boxes layer must hide itself the
/// instant these two identities disagree — a previous Rx's green/red
/// boxes must never be left floating over a NEW prescription's (different)
/// field positions for however long the next refresh takes.
///
/// Both identities are PioneerRxWindow.RxNumber values (parsed from the
/// window title — see that class), so they compare apples-to-apples:
/// <paramref name="currentRxIdentity"/> comes from a FRESH attach (the
/// window Pioneer is showing right now), <paramref name="verdictsRxIdentity"/>
/// from OverlayViewModel.CurrentVerdictsRxIdentity (captured at attach
/// time for whichever refresh most recently populated the displayed
/// rows). See IntegratedOverlayCoordinator.TickCore (checked every ~250ms
/// tick) and HideBoxesIfRxIdentityChanged (checked SYNCHRONOUSLY on
/// TitleChangeWatcher's near-instant title-change event, before the
/// resulting refresh even starts) for the two call sites.
/// </summary>
public static class RxIdentityGate
{
    /// <summary>
    /// True (STALE — hide the boxes) whenever the two identities differ.
    /// Both null (nothing attached, nothing ever verified) is NOT stale by
    /// this predicate alone — that state is already covered by the other
    /// visibility gates (isAttached/hasVerifiableContent), so this only
    /// needs to flag a genuine MISMATCH between two known identities, or a
    /// known current identity with no matching verdicts yet.
    /// </summary>
    public static bool IsStale(string? currentRxIdentity, string? verdictsRxIdentity)
    {
        if (currentRxIdentity is null && verdictsRxIdentity is null) return false;
        return currentRxIdentity != verdictsRxIdentity;
    }
}
