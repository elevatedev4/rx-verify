using System.Collections.Generic;
using System.Drawing;

namespace RxVerifyOverlay.Integrated;

/// <summary>A window-relative DIP point — same space as DipRect (Integrated/DpiRectConverter.cs), just a point instead of a rect.</summary>
public readonly record struct DipPoint(double X, double Y);

/// <summary>
/// Pure math behind the hover/right-click affordance's click-through
/// toggle — see IntegratedBoxesWindow's hover-poll doc for the full
/// picture: that window is WS_EX_TRANSPARENT (click-through) by default so
/// the pharmacist can type/click straight into PioneerRx underneath it,
/// which also means it never receives real mouse messages WPF's own
/// ToolTip/ContextMenu machinery needs. The fix is to poll the cursor's
/// OS-level screen position on a short timer and dynamically clear
/// WS_EX_TRANSPARENT only while the cursor sits over one of the current
/// per-field verdict-bar hotspots (restoring it the instant it doesn't) —
/// this class is the pure geometry half of that: converting the cursor's
/// PHYSICAL screen position into the same window-relative DIP space
/// VerdictBarGeometry's bars already live in (identical formula to
/// DpiRectConverter.ToDipRect, just for a point instead of a rect), then
/// answering "is this point inside any of the current hotspot rects".
///
/// No WPF/Win32 dependency here — directly unit-testable, same
/// "pure geometry pulled out for its own tests" pattern as
/// VerdictBarGeometry/DpiRectConverter (see
/// RxVerifyOverlay.Tests/CursorHitTestTests.cs).
/// </summary>
public static class CursorHitTest
{
    /// <summary>
    /// <paramref name="cursorPhysical"/> and <paramref name="windowOriginPhysical"/>
    /// are both physical screen pixels in the SAME (virtual desktop)
    /// coordinate space — windowOriginPhysical is the boxes window's own
    /// top-left (the same value SetBoxes' windowOriginPhysical parameter
    /// already carries), so the result is relative to that window's
    /// client-area origin, directly comparable to the DipRects SetBoxes
    /// derives for its hotspots.
    /// </summary>
    public static DipPoint ToDipPoint(Point cursorPhysical, Point windowOriginPhysical, double dpiScaleX, double dpiScaleY)
    {
        var x = (cursorPhysical.X - windowOriginPhysical.X) / dpiScaleX;
        var y = (cursorPhysical.Y - windowOriginPhysical.Y) / dpiScaleY;
        return new DipPoint(x, y);
    }

    /// <summary>Inclusive on every edge (a cursor sitting exactly on a hotspot's boundary counts as "inside") — errs toward the transparent-toggle being ON (interactive) rather than a pharmacist's cursor sitting right at a bar's edge finding it unresponsive.</summary>
    public static bool IsWithinAnyRect(double x, double y, IReadOnlyList<DipRect> rects)
    {
        foreach (var rect in rects)
        {
            if (x >= rect.X && x <= rect.X + rect.Width && y >= rect.Y && y <= rect.Y + rect.Height)
            {
                return true;
            }
        }

        return false;
    }
}
