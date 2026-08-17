namespace RxVerifyOverlay.OrderAssist.Geometry;

/// <summary>
/// Pure "how tall should the Order Assist highlight window actually be"
/// rule — see OrderAssistCoordinator.OrderModeBottomInsetDip's own doc for
/// the owner report this closes ("The overlay on order mode is covering
/// some buttons ... doesn't cover the New button"). OrderAssistOverlayWindow
/// is otherwise repositioned to exactly the target Pioneer window's own
/// bounds; this trims a fixed DIP amount off ONLY the bottom of that
/// presented height, converted to physical pixels via the same DPI scale
/// OrderAssistCoordinator already computes for the tick. Kept here (not
/// inline in OrderAssistCoordinator.TickAsync) purely so the arithmetic is
/// unit-testable with plain numbers, same posture as every other class in
/// this folder — see RowRect's own doc.
/// </summary>
public static class OrderModeOverlayBoundsRule
{
    /// <summary>
    /// Returns the physical-pixel height OrderAssistOverlayWindow should
    /// actually be repositioned to: <paramref name="targetHeightPhysical"/>
    /// (the target Pioneer window's own full height) minus
    /// <paramref name="bottomInsetDip"/> converted to physical pixels via
    /// <paramref name="scale"/>, floored at 0 so a small/degenerate target
    /// window (or an inset larger than the window itself) can never
    /// produce a negative height for SetWindowPos.
    /// </summary>
    public static int TrimmedHeightPhysical(int targetHeightPhysical, double bottomInsetDip, double scale)
    {
        var insetPhysical = (int)System.Math.Round(bottomInsetDip * scale);
        var trimmed = targetHeightPhysical - insetPhysical;
        return trimmed > 0 ? trimmed : 0;
    }
}
