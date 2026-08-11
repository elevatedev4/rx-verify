using System;
using System.Drawing;

namespace RxVerifyOverlay.Integrated;

/// <summary>
/// ADDENDUM (round 5 — "the little overlay box is jumping around every
/// time Pioneer opens a new little popup window. It needs to stay put in
/// the top right"): pure decision behind which window/rect the control
/// box should anchor to when PioneerRx is the foreground app.
///
/// THE BUG: the foreground window can be a small transient popup/dialog
/// PioneerRx OWNS (a save confirmation, a lookup picker, etc.) rather
/// than its main shell window. IntegratedOverlayCoordinator.
/// TryGetForegroundPioneerRxWindow was anchoring the control box directly
/// to THAT popup's tiny rect — correct process, wrong window — which is
/// exactly what made the box visibly jump every time such a popup opened
/// and closed.
///
/// THE FIX: TryGetForegroundPioneerRxWindow walks up from the foreground
/// window to its ROOT OWNER via a single Win32 GetAncestor(hwnd,
/// GA_ROOTOWNER) call (that walk itself isn't testable without a live
/// window — see that method's own doc for the Win32 path) and hands the
/// raw result to Choose below, which is pure and covers the actual
/// DECISION: anchor to the owner's rect only when an owner genuinely
/// exists AND its rect is sane; otherwise fall back to the foreground
/// window itself, i.e. exactly the pre-fix behavior (so a foreground
/// window that's ALREADY the top-level shell, with no owner at all,
/// behaves identically to before).
/// </summary>
public static class ForegroundAnchorRule
{
    /// <summary>Which HWND/rect pair to anchor the control box to.</summary>
    public readonly record struct Anchor(IntPtr Handle, Rectangle Bounds);

    /// <summary>
    /// <paramref name="ownerHandle"/>/<paramref name="ownerBounds"/> come
    /// from GetAncestor(foregroundHandle, GA_ROOTOWNER) and a GetWindowRect
    /// call on whatever that returned — IntPtr.Zero/null when there's no
    /// owner at all (the foreground window IS already a genuine top-level
    /// window with nothing owning it, e.g. Pioneer's own main shell) or
    /// GetWindowRect on it failed. Falls back to
    /// <paramref name="foregroundHandle"/>/<paramref name="foregroundBounds"/>
    /// (the pre-fix behavior) whenever the owner is missing or its rect
    /// fails IsSaneWindowRect.
    /// </summary>
    public static Anchor Choose(IntPtr foregroundHandle, Rectangle foregroundBounds, IntPtr ownerHandle, Rectangle? ownerBounds)
    {
        if (ownerHandle != IntPtr.Zero && ownerBounds is { } bounds && IsSaneWindowRect(bounds))
        {
            return new Anchor(ownerHandle, bounds);
        }

        return new Anchor(foregroundHandle, foregroundBounds);
    }

    /// <summary>A rect GetWindowRect reports as successful but that's degenerate (zero or negative width/height) is treated the same as a failed lookup — never anchor to it.</summary>
    public static bool IsSaneWindowRect(Rectangle rect) => rect.Width > 0 && rect.Height > 0;
}
