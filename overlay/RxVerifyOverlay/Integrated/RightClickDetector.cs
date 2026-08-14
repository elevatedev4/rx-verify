namespace RxVerifyOverlay.Integrated;

/// <summary>
/// Pure, edge-detected right-click gesture behind the verdict-bar
/// "Report error…" affordance — extracted out of HoverStateMachine
/// (RXVERIFY-TROUBLESHOOT, 2026-08: "hover works, right click doesn't")
/// so the gesture itself is directly unit-testable in isolation, with no
/// dwell/popup state mixed in.
///
/// EDGE DETECTION, NOT AN INSTANTANEOUS READ: a raw "is the button down
/// right now" check (GetAsyncKeyState's high bit, sampled fresh every
/// poll tick) fires on EVERY tick for as long as a press is held, not
/// just the moment it started — left unguarded, that would mean a single
/// physical right-click that happens to stay down across 2-3 ~60ms poll
/// ticks (entirely normal for a human click) opens 2-3 stacked report
/// dialogs instead of one. Comparing this tick's state against the
/// PREVIOUS tick's (see Update) turns that into a single up-to-down
/// TRANSITION, which is what actually fires — exactly once per physical
/// press, regardless of how long it's held or how many ticks land while
/// it is. GetAsyncKeyState's own low bit ("was pressed since the last
/// call") could answer a similar question, but it's shared, mutable OS
/// state — a DIFFERENT caller anywhere in the process (or another
/// process) reading it first consumes it, silently starving this poll.
/// Comparing our OWN previous sample instead has no such interference
/// and is deterministic to unit-test without any OS dependency at all.
///
/// DIALOG-OPEN GUARD: <paramref name="dialogOpen"/> in <see cref="Update"/>
/// is a plain per-call PARAMETER, not internal state this class owns or
/// mutates — the caller (IntegratedBoxesWindow, fed from
/// IntegratedOverlayCoordinator/MainWindow's own dialog lifetime) simply
/// reports the CURRENT truth every tick. There is no separate
/// clear/reset method to remember to call, so this guard can never get
/// stuck "on" after the dialog it was guarding against has already
/// closed — the very next tick after the caller starts passing false
/// re-enables firing, with no special-casing needed here. Gating the
/// FIRE result (rather than skipping edge tracking entirely while the
/// dialog is open) means a press that started before the dialog opened
/// and is still held when it closes is correctly NOT treated as a fresh
/// click the instant the guard lifts — see
/// RightClickDetectorTests.HeldButtonAcrossDialogCloseDoesNotRefireUntilANewPress.
/// </summary>
public sealed class RightClickDetector
{
    private bool _previousDown;

    /// <summary>
    /// One poll tick. Returns true exactly on the tick a right-click
    /// press transition (up last tick, down this tick) is observed WHILE
    /// the cursor is over a hotspot AND no report dialog is currently
    /// open — false in every other case, including every subsequent tick
    /// the same press stays held. Edge tracking (<c>_previousDown</c>)
    /// updates unconditionally, regardless of <paramref name="overHotspot"/>
    /// or <paramref name="dialogOpen"/>, so a press that started off a
    /// hotspot (or while a dialog was open) is never retroactively
    /// treated as a fresh click just because the cursor later drifts onto
    /// one, or the dialog later closes, while the SAME press is still
    /// held.
    /// </summary>
    public bool Update(bool isDown, bool overHotspot, bool dialogOpen)
    {
        var fire = overHotspot && isDown && !_previousDown && !dialogOpen;
        _previousDown = isDown;
        return fire;
    }
}
