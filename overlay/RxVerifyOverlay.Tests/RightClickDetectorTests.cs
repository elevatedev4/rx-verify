using RxVerifyOverlay.Integrated;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for RightClickDetector (Integrated/RightClickDetector.cs) —
/// the pure edge-detected right-click gesture extracted out of
/// HoverStateMachine (RXVERIFY-TROUBLESHOOT, 2026-08: "hover works, right
/// click doesn't"). No Win32/WPF/timer dependency — every tick is a
/// plain Update(isDown, overHotspot, dialogOpen) call.
/// </summary>
public class RightClickDetectorTests
{
    [Fact]
    public void FiresOnceOnThePressTransitionInsideAHotspot()
    {
        var detector = new RightClickDetector();

        Assert.False(detector.Update(isDown: false, overHotspot: true, dialogOpen: false));
        Assert.True(detector.Update(isDown: true, overHotspot: true, dialogOpen: false));
    }

    [Fact]
    public void DoesNotRepeatWhileTheButtonStaysHeldDown()
    {
        var detector = new RightClickDetector();
        detector.Update(false, true, false);

        Assert.True(detector.Update(true, true, false));
        // Still held on subsequent ticks — must not fire again.
        Assert.False(detector.Update(true, true, false));
        Assert.False(detector.Update(true, true, false));
    }

    [Fact]
    public void FiresAgainAfterReleaseAndAFreshPress()
    {
        var detector = new RightClickDetector();
        detector.Update(false, true, false);

        Assert.True(detector.Update(true, true, false));

        detector.Update(false, true, false); // released
        Assert.True(detector.Update(true, true, false)); // pressed again
    }

    [Fact]
    public void NeverFiresWhileNotOverAnyHotspot()
    {
        var detector = new RightClickDetector();
        detector.Update(false, false, false);

        Assert.False(detector.Update(true, false, false));
    }

    [Fact]
    public void PressThatStartedOffAHotspotDoesNotFireWhenTheCursorThenEntersOne()
    {
        // The transition itself (up -> down) happened while off every
        // hotspot; merely being over one on a LATER tick (button already
        // down, no new down-transition) must not retroactively fire.
        var detector = new RightClickDetector();
        detector.Update(false, false, false);
        detector.Update(true, false, false); // press while off-hotspot

        Assert.False(detector.Update(true, true, false)); // still held, now over a hotspot
    }

    [Fact]
    public void MissedInstantaneousReadStillFiresOnWhicheverTickCatchesTheDownState()
    {
        // Regression guard for the ORIGINAL bug shape (a raw "is it down
        // RIGHT NOW" instantaneous check, no previous-state comparison):
        // a physical click that only overlaps ONE poll tick (a fast
        // click, or a click that straddles two ticks such that only one
        // samples it as down) must still be caught by whichever single
        // tick actually observes isDown=true, exactly once — not
        // silently dropped, and not double-fired by the release tick
        // that follows.
        var detector = new RightClickDetector();

        Assert.False(detector.Update(false, true, false)); // before the click
        Assert.True(detector.Update(true, true, false));   // the one tick that caught it down
        Assert.False(detector.Update(false, true, false)); // released again — no re-fire
    }

    [Fact]
    public void DialogOpenSuppressesFiringEvenOnAFreshPressTransition()
    {
        var detector = new RightClickDetector();
        detector.Update(false, true, false);

        Assert.False(detector.Update(true, true, dialogOpen: true));
    }

    [Fact]
    public void StuckGuardRegression_FiringResumesAssoonAsDialogOpenGoesFalseAgain()
    {
        // Suspect #2 from the brief: a guard left stuck true forever
        // would permanently disable right-click after the first report.
        // Since dialogOpen is a plain per-call parameter (not internal
        // state this class remembers), the very next tick the caller
        // reports false must behave exactly as if the guard never
        // existed for a FRESH press.
        var detector = new RightClickDetector();
        detector.Update(false, true, dialogOpen: true);
        Assert.False(detector.Update(true, true, dialogOpen: true));

        // Button released while the dialog was still open...
        detector.Update(false, true, dialogOpen: true);
        // ...dialog now closes, and a genuinely NEW press follows.
        Assert.True(detector.Update(true, true, dialogOpen: false));
    }

    [Fact]
    public void HeldButtonAcrossDialogCloseDoesNotRefireUntilANewPress()
    {
        // The press that opened the dialog is STILL physically held down
        // when the dialog closes (guard flips false) — this must NOT be
        // treated as a brand-new click just because the guard lifted;
        // edge tracking already consumed this press.
        var detector = new RightClickDetector();
        detector.Update(false, true, dialogOpen: false);
        Assert.True(detector.Update(true, true, dialogOpen: false)); // opens the dialog

        // Dialog now open; button still held across several ticks.
        Assert.False(detector.Update(true, true, dialogOpen: true));
        Assert.False(detector.Update(true, true, dialogOpen: true));

        // Dialog closes; SAME press is still held — must not refire.
        Assert.False(detector.Update(true, true, dialogOpen: false));

        // Only a genuine release-then-press fires again.
        detector.Update(false, true, dialogOpen: false);
        Assert.True(detector.Update(true, true, dialogOpen: false));
    }
}
