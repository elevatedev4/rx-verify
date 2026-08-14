using System;

namespace RxVerifyOverlay.Integrated;

/// <summary>
/// One poll tick's inputs, already reduced to exactly what
/// HoverStateMachine needs — no Win32/WPF dependency (GetCursorPos/
/// GetAsyncKeyState/CursorHitTest all live in IntegratedBoxesWindow's
/// PollCursorForHover, which builds one of these per tick).
/// </summary>
/// <param name="IsOverHotspot">True when the cursor is currently inside ANY hotspot (CursorHitTest.FindContainingRectIndex &gt;= 0).</param>
/// <param name="HotspotIndex">-1 when !IsOverHotspot; otherwise the index of whichever hotspot the cursor is over, into the same list IntegratedBoxesWindow keeps _hotspotFields aligned with. Used ONLY to detect "moved to a DIFFERENT hotspot without ever leaving" — see the class doc.</param>
/// <param name="IsRightButtonDown">Live right mouse button state (GetAsyncKeyState(VK_RBUTTON)), sampled fresh every tick regardless of which window has focus/activation — this is what makes right-click detection independent of whether real mouse messages are reaching this exotic-styled window at all.</param>
/// <param name="IsDialogOpen">True while a Report-error dialog is already open — see RightClickDetector's own doc for why this is a plain per-tick parameter (never internal state this machine or RightClickDetector remembers), so the guard against stacking a second dialog can never get stuck true after the dialog that set it closes.</param>
/// <param name="ElapsedSinceLastSample">Wall-clock time since the previous Update call. IntegratedBoxesWindow passes the poll timer's own configured Interval rather than measuring an actual clock delta — see that class's doc for why a fixed per-tick value is deterministic and good enough for a 250ms dwell threshold.</param>
public readonly record struct HoverPollSample(bool IsOverHotspot, int HotspotIndex, bool IsRightButtonDown, bool IsDialogOpen, TimeSpan ElapsedSinceLastSample);

/// <summary>What IntegratedBoxesWindow should do to its custom popup window this tick, per HoverStateMachine.Update's return.</summary>
public enum HoverPopupAction
{
    /// <summary>No change — either nothing is happening, or the dwell/hover state didn't just cross a Show/Hide boundary this tick.</summary>
    None,

    /// <summary>Show the popup for HoverPollResult's implied "current" hotspot (the caller already knows which — it's whatever index it just passed in as IsOverHotspot/HotspotIndex).</summary>
    Show,

    /// <summary>Hide the popup — the cursor left every hotspot, or moved to a DIFFERENT one (see the class doc: switching hotspots hides the old popup immediately rather than sliding/relabeling it).</summary>
    Hide
}

/// <summary>Return of one HoverStateMachine.Update call — the popup action to take (if any) plus whether a right-click press was JUST detected this tick.</summary>
public readonly record struct HoverPollResult(HoverPopupAction PopupAction, bool RightClickTriggered);

/// <summary>
/// Pure dwell/right-click state machine behind IntegratedBoxesWindow's
/// custom hover popup.
///
/// REDESIGN (owner live-test feedback, fix/hover-popup-live branch):
/// WPF's own ToolTipService/ContextMenu turned out to be unreliable in
/// real live testing on this window's exotic styles (WS_EX_NOACTIVATE +
/// WS_EX_TOOLWINDOW + Topmost + dynamically-toggled WS_EX_TRANSPARENT) —
/// "hover shows a different cursor but no popup, right-click does
/// nothing." The Cursor-changing DOES work (confirmed live), which is a
/// simpler, lower-level WM_SETCURSOR-driven mechanism than ToolTipService's
/// delayed-show timer chain or ContextMenu's focus/activation-dependent
/// open — this class replaces BOTH of the unreliable ones with logic
/// driven entirely by the poll timer that already reliably drives the
/// click-through toggle (see IntegratedBoxesWindow's HOVER section),
/// rather than depending on WPF's own input-event plumbing at all.
///
/// DWELL: the popup shows only after the cursor has sat continuously
/// inside the SAME hotspot for >= DwellThreshold (owner's ask: "~250ms").
/// Leaving a hotspot (even briefly, even by moving straight onto a
/// DIFFERENT one) resets the dwell clock for whatever hotspot is current
/// now, and hides any popup that was showing for the previous one — so a
/// cursor merely passing over several bars on its way somewhere else
/// never flashes a popup for each one it crosses.
///
/// RIGHT-CLICK: delegated entirely to RightClickDetector (extracted out
/// of this class, RXVERIFY-TROUBLESHOOT 2026-08 — see that class's own
/// doc for the full edge-detection/dialog-guard design). A transition
/// (button was up last sample, is down this sample) while CURRENTLY over
/// a hotspot AND no report dialog is already open fires exactly once per
/// press — never repeatedly while the button stays held, never for a
/// press that started or is currently sitting off every hotspot, and
/// never while IsDialogOpen is true. Firing ALSO force-hides the popup on
/// that same tick (owner ask: "hide the hover so its info shows in the
/// report dialog instead") regardless of what the dwell logic below would
/// otherwise have returned — see the override at the end of Update.
///
/// No Win32/WPF dependency at all — directly unit-testable without any
/// live window; see RxVerifyOverlay.Tests/HoverStateMachineTests.cs for
/// coverage of every enter/dwell/leave/switch/right-click transition, and
/// RxVerifyOverlay.Tests/RightClickDetectorTests.cs for the gesture
/// itself in isolation.
/// NOT thread-safe and not meant to be — IntegratedBoxesWindow owns
/// exactly one instance, updated only from its own DispatcherTimer tick
/// (always the UI thread).
/// </summary>
public sealed class HoverStateMachine
{
    public static readonly TimeSpan DwellThreshold = TimeSpan.FromMilliseconds(250);

    private int _dwellHotspotIndex = -1;
    private TimeSpan _dwellElapsed = TimeSpan.Zero;
    private bool _popupShownForCurrentDwell;
    private readonly RightClickDetector _rightClickDetector = new();

    public HoverPollResult Update(HoverPollSample sample)
    {
        // Right-click is evaluated independently of the dwell/popup logic
        // below — a press-transition inside a hotspot fires regardless of
        // whether the popup happens to be showing yet (a pharmacist
        // shouldn't have to wait out the dwell delay before right-click
        // works).
        var rightClickTriggered = _rightClickDetector.Update(sample.IsRightButtonDown, sample.IsOverHotspot, sample.IsDialogOpen);

        HoverPollResult result;

        if (!sample.IsOverHotspot)
        {
            var wasShowing = _popupShownForCurrentDwell;
            ResetDwell();
            result = new HoverPollResult(wasShowing ? HoverPopupAction.Hide : HoverPopupAction.None, rightClickTriggered);
        }
        else if (sample.HotspotIndex != _dwellHotspotIndex)
        {
            // Either a fresh hover (was hovering nothing) or a direct
            // switch to a DIFFERENT hotspot without leaving in between —
            // both restart the dwell clock at zero for the NEW hotspot;
            // hide whatever popup belonged to the PREVIOUS one first
            // (see class doc: switching never slides/relabels a popup
            // in place).
            var wasShowing = _popupShownForCurrentDwell;
            _dwellHotspotIndex = sample.HotspotIndex;
            _dwellElapsed = TimeSpan.Zero;
            _popupShownForCurrentDwell = false;
            result = new HoverPollResult(wasShowing ? HoverPopupAction.Hide : HoverPopupAction.None, rightClickTriggered);
        }
        else
        {
            _dwellElapsed += sample.ElapsedSinceLastSample;

            if (!_popupShownForCurrentDwell && _dwellElapsed >= DwellThreshold)
            {
                _popupShownForCurrentDwell = true;
                result = new HoverPollResult(HoverPopupAction.Show, rightClickTriggered);
            }
            else
            {
                result = new HoverPollResult(HoverPopupAction.None, rightClickTriggered);
            }
        }

        // OWNER UX CHANGE: a right-click always hides the popup
        // immediately — its info is about to reappear inside the report
        // dialog instead — regardless of whatever the dwell logic above
        // decided for this same tick (which, in practice, is never Show:
        // RightClickDetector can only fire while IsOverHotspot is true,
        // and dwell needs several MORE ticks after a hotspot is entered
        // before Show is even eligible).
        if (rightClickTriggered)
        {
            result = result with { PopupAction = HoverPopupAction.Hide };
        }

        return result;
    }

    /// <summary>
    /// Forces the machine back to "not hovering anything" — called by
    /// IntegratedBoxesWindow whenever the poll's own early-out fires
    /// (window hidden, no hotspots, GetCursorPos failed) or the boxes
    /// window itself is hidden (HideAndResetHover), so a stale dwell/popup
    /// state from before that moment can never leak into whatever the
    /// cursor happens to be over next. Does NOT itself return a
    /// HoverPollResult / tell the caller to hide the popup — callers that
    /// need "was a popup showing, so should I hide it" should check that
    /// BEFORE calling Reset (see PollCursorForHover/HideAndResetHover, both
    /// of which unconditionally hide their popup window right alongside
    /// calling this, so the ordering doesn't matter for them in practice).
    /// </summary>
    public void Reset() => ResetDwell();

    private void ResetDwell()
    {
        _dwellHotspotIndex = -1;
        _dwellElapsed = TimeSpan.Zero;
        _popupShownForCurrentDwell = false;
    }
}
