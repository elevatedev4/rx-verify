using System;
using RxVerifyOverlay.Integrated;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for HoverStateMachine (Integrated/HoverStateMachine.cs) —
/// the pure dwell/right-click detection behind IntegratedBoxesWindow's
/// custom hover popup, which REPLACED WPF's own ToolTipService/ContextMenu
/// entirely after live testing on the owner's PC showed those unreliable
/// on this window's exotic styles ("hover shows a different cursor but no
/// popup with the info, right-click doesn't do anything" — see
/// HoverStateMachine's class doc for the full story). No WPF/Win32/timer
/// dependency here — every tick is a plain Update(HoverPollSample) call
/// with an explicit elapsed duration, so dwell timing is fully
/// deterministic in these tests.
/// </summary>
public class HoverStateMachineTests
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(60); // matches IntegratedBoxesWindow.HoverPollIntervalMs

    private static HoverPollSample Sample(bool isOverHotspot, int hotspotIndex, bool rightButtonDown = false, bool dialogOpen = false, TimeSpan? elapsed = null) =>
        new(isOverHotspot, hotspotIndex, rightButtonDown, dialogOpen, elapsed ?? TickInterval);

    [Fact]
    public void NoActionWhileDwellingBelowThreshold()
    {
        var machine = new HoverStateMachine();

        // 60ms ticks, threshold is 250ms — 3 ticks (180ms) isn't enough yet.
        for (var i = 0; i < 3; i++)
        {
            var result = machine.Update(Sample(isOverHotspot: true, hotspotIndex: 0));
            Assert.Equal(HoverPopupAction.None, result.PopupAction);
        }
    }

    [Fact]
    public void ShowsExactlyOnceOnceDwellThresholdIsReachedThenGoesQuiet()
    {
        var machine = new HoverStateMachine();

        // 4 ticks * 60ms = 240ms (still below 250ms threshold).
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(HoverPopupAction.None, machine.Update(Sample(true, 0)).PopupAction);
        }

        // 5th tick crosses 250ms (300ms total) — Show fires exactly here.
        var showResult = machine.Update(Sample(true, 0));
        Assert.Equal(HoverPopupAction.Show, showResult.PopupAction);

        // Continuing to hover the SAME hotspot never re-shows.
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HoverPopupAction.None, machine.Update(Sample(true, 0)).PopupAction);
        }
    }

    [Fact]
    public void LeavingAfterThePopupWasShownHidesIt()
    {
        var machine = new HoverStateMachine();
        DwellUntilShown(machine, hotspotIndex: 0);

        var leaveResult = machine.Update(Sample(isOverHotspot: false, hotspotIndex: -1));

        Assert.Equal(HoverPopupAction.Hide, leaveResult.PopupAction);
    }

    [Fact]
    public void LeavingBeforeThePopupEverShowedIsANoOpNotAHide()
    {
        var machine = new HoverStateMachine();

        // Only 2 ticks (120ms) — well below the 250ms threshold, nothing shown yet.
        machine.Update(Sample(true, 0));
        machine.Update(Sample(true, 0));

        var leaveResult = machine.Update(Sample(isOverHotspot: false, hotspotIndex: -1));

        Assert.Equal(HoverPopupAction.None, leaveResult.PopupAction);
    }

    [Fact]
    public void SwitchingDirectlyToADifferentHotspotHidesTheOldPopupAndRestartsDwell()
    {
        var machine = new HoverStateMachine();
        DwellUntilShown(machine, hotspotIndex: 0);

        // Moved straight from hotspot 0 to hotspot 1 without ever leaving —
        // must hide the OLD popup immediately, not slide/relabel it.
        var switchResult = machine.Update(Sample(true, hotspotIndex: 1));
        Assert.Equal(HoverPopupAction.Hide, switchResult.PopupAction);

        // Dwell restarted at zero for hotspot 1 — same number of ticks that
        // triggered the first Show should trigger it again here.
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(HoverPopupAction.None, machine.Update(Sample(true, 1)).PopupAction);
        }
        Assert.Equal(HoverPopupAction.Show, machine.Update(Sample(true, 1)).PopupAction);
    }

    [Fact]
    public void MovingToADifferentHotspotBeforeThePopupShowedIsANoOpNotAHide()
    {
        var machine = new HoverStateMachine();
        machine.Update(Sample(true, 0)); // one tick on hotspot 0 — well below threshold

        var switchResult = machine.Update(Sample(true, hotspotIndex: 1));

        Assert.Equal(HoverPopupAction.None, switchResult.PopupAction);
    }

    [Fact]
    public void RightClickTriggersOnThePressTransitionInsideAHotspot()
    {
        var machine = new HoverStateMachine();

        var upResult = machine.Update(Sample(true, 0, rightButtonDown: false));
        Assert.False(upResult.RightClickTriggered);

        var downResult = machine.Update(Sample(true, 0, rightButtonDown: true));
        Assert.True(downResult.RightClickTriggered);
    }

    [Fact]
    public void RightClickDoesNotRepeatWhileTheButtonStaysHeldDown()
    {
        var machine = new HoverStateMachine();
        machine.Update(Sample(true, 0, rightButtonDown: false));
        Assert.True(machine.Update(Sample(true, 0, rightButtonDown: true)).RightClickTriggered);

        // Still held down on subsequent ticks — must not fire again.
        Assert.False(machine.Update(Sample(true, 0, rightButtonDown: true)).RightClickTriggered);
        Assert.False(machine.Update(Sample(true, 0, rightButtonDown: true)).RightClickTriggered);
    }

    [Fact]
    public void RightClickFiresAgainAfterReleaseAndAFreshPress()
    {
        var machine = new HoverStateMachine();
        machine.Update(Sample(true, 0, rightButtonDown: false));
        Assert.True(machine.Update(Sample(true, 0, rightButtonDown: true)).RightClickTriggered);

        machine.Update(Sample(true, 0, rightButtonDown: false)); // released
        Assert.True(machine.Update(Sample(true, 0, rightButtonDown: true)).RightClickTriggered); // pressed again
    }

    [Fact]
    public void RightClickNeverTriggersWhileNotOverAnyHotspot()
    {
        var machine = new HoverStateMachine();
        machine.Update(Sample(isOverHotspot: false, hotspotIndex: -1, rightButtonDown: false));

        var result = machine.Update(Sample(isOverHotspot: false, hotspotIndex: -1, rightButtonDown: true));

        Assert.False(result.RightClickTriggered);
    }

    [Fact]
    public void RightClickPressThatStartedOffAHotspotDoesNotFireWhenTheCursorThenEntersOne()
    {
        // The transition itself (up -> down) happened while off every
        // hotspot; merely being over one on a LATER tick (button already
        // down, no new down-transition) must not retroactively fire.
        var machine = new HoverStateMachine();
        machine.Update(Sample(isOverHotspot: false, hotspotIndex: -1, rightButtonDown: false));
        machine.Update(Sample(isOverHotspot: false, hotspotIndex: -1, rightButtonDown: true)); // press while off-hotspot

        var result = machine.Update(Sample(isOverHotspot: true, hotspotIndex: 0, rightButtonDown: true)); // still held, now over a hotspot

        Assert.False(result.RightClickTriggered);
    }

    [Fact]
    public void RightClickForceHidesThePopupEvenWhileItWasAlreadyShowing()
    {
        // Owner UX change: right-click hides the hover popup immediately
        // — its info reappears inside the report dialog instead.
        var machine = new HoverStateMachine();
        DwellUntilShown(machine, hotspotIndex: 0);

        var clickResult = machine.Update(Sample(true, 0, rightButtonDown: true));

        Assert.Equal(HoverPopupAction.Hide, clickResult.PopupAction);
        Assert.True(clickResult.RightClickTriggered);
    }

    [Fact]
    public void RightClickForceHidesThePopupEvenBeforeDwellEverShowedIt()
    {
        // Right-click doesn't wait out the dwell delay (pre-existing
        // guarantee) — hiding on click must also be a no-op-safe Hide
        // even when there was never anything shown yet.
        var machine = new HoverStateMachine();
        machine.Update(Sample(true, 0)); // one tick, well below dwell threshold

        var clickResult = machine.Update(Sample(true, 0, rightButtonDown: true));

        Assert.Equal(HoverPopupAction.Hide, clickResult.PopupAction);
        Assert.True(clickResult.RightClickTriggered);
    }

    [Fact]
    public void DialogOpenSuppressesRightClickButDwellStillTracksNormally()
    {
        // The dialog-open guard only gates RightClickTriggered — it must
        // not interfere with unrelated dwell/popup bookkeeping.
        var machine = new HoverStateMachine();

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(HoverPopupAction.None, machine.Update(Sample(true, 0, dialogOpen: true)).PopupAction);
        }
        var showResult = machine.Update(Sample(true, 0, dialogOpen: true));
        Assert.Equal(HoverPopupAction.Show, showResult.PopupAction);

        var clickResult = machine.Update(Sample(true, 0, rightButtonDown: true, dialogOpen: true));
        Assert.False(clickResult.RightClickTriggered);
        // Not a right-click, so the dwell logic's own decision (None —
        // already shown, nothing changed) stands; only an ACTUAL fired
        // click forces Hide.
        Assert.Equal(HoverPopupAction.None, clickResult.PopupAction);
    }

    [Fact]
    public void StuckGuardRegression_RightClickFiresAgainOnceDialogOpenGoesFalse()
    {
        var machine = new HoverStateMachine();
        DwellUntilShown(machine, hotspotIndex: 0);

        // Press while a dialog is (incorrectly, in this hypothetical)
        // still marked open — suppressed.
        machine.Update(Sample(true, 0, rightButtonDown: true, dialogOpen: true));
        // Release, dialog closes.
        machine.Update(Sample(true, 0, rightButtonDown: false, dialogOpen: false));

        // A genuinely fresh press now fires normally — the guard never
        // got stuck.
        var result = machine.Update(Sample(true, 0, rightButtonDown: true, dialogOpen: false));
        Assert.True(result.RightClickTriggered);
        Assert.Equal(HoverPopupAction.Hide, result.PopupAction);
    }

    [Fact]
    public void ResetClearsDwellSoTheNextHoverStartsAFreshCycle()
    {
        var machine = new HoverStateMachine();
        DwellUntilShown(machine, hotspotIndex: 0);

        machine.Reset();

        // Same hotspot index as before Reset — if the dwell state weren't
        // actually cleared, this would look like "already dwelling here"
        // and could behave inconsistently; asserting the FULL threshold is
        // required again is the real proof Reset worked.
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(HoverPopupAction.None, machine.Update(Sample(true, 0)).PopupAction);
        }
        Assert.Equal(HoverPopupAction.Show, machine.Update(Sample(true, 0)).PopupAction);
    }

    /// <summary>Drives the machine through exactly enough 60ms ticks to cross the 250ms dwell threshold and return to a "just showed" state, for tests that only care about what happens AFTER the popup is already up.</summary>
    private static void DwellUntilShown(HoverStateMachine machine, int hotspotIndex)
    {
        for (var i = 0; i < 4; i++)
        {
            machine.Update(Sample(true, hotspotIndex));
        }
        var result = machine.Update(Sample(true, hotspotIndex));
        Assert.Equal(HoverPopupAction.Show, result.PopupAction);
    }
}
