using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace RxVerifyOverlay.OrderAssist.Windows;

/// <summary>
/// POPUP FIX (owner's live pharmacy report, 2026-08-14: "the recommended
/// order pops up in a window above the main Pioneer... make sure the
/// logic will work with the popup window because right now nothing
/// works"): pure decision behind WHICH of PioneerRx's own top-level
/// windows is the current Order Assist target, given every one of them
/// this tick (not just whichever happens to be the OS foreground window).
///
/// THE BUG THIS REPLACES: OrderAssistWindowLocator.FindForegroundTarget
/// only ever looked at GetForegroundWindow() — fine while the "Create
/// Recommended Orders" screen IS the foreground window, but the owner's
/// report describes exactly the case where it isn't: "Recommended Order -
/// Catalog Item Substitution Selection" floats as its own top-level
/// window ABOVE "Create Recommended Orders", and depending on exactly
/// which one last took OS activation, foreground can land on either — or,
/// per the owner's fix request for the control box (a separate,
/// WS_EX_NOACTIVATE, never-activating window), a stray click on THAT can
/// leave neither Order Assist screen foreground at all, at which point
/// the old code found nothing and highlighted nothing, no matter how
/// visibly the popup sat on screen.
///
/// THE FIX: same shape as Integrated/MainWindowAnchorRule.cs's own round-7
/// fix for the identical class of bug — stop deriving the target purely
/// from whatever's foreground; enumerate every one of PioneerRx's own
/// top-level windows and pick among THOSE. Foreground is kept as a FAST
/// PATH (first choice when it genuinely is one of the two target windows,
/// preserving today's common-case behavior exactly) but is no longer the
/// ONLY path — see <see cref="Choose"/>.
///
/// OrderAssistWindowLocator supplies the Win32 data (EnumWindows +
/// GetWindowThreadProcessId filtered to FieldMap.TargetProcessNames,
/// IsWindowVisible, IsIconic, GetWindowRect, GetWindowText + Classify) —
/// this class never touches Win32 itself, so it's testable on Mac, same
/// separation as MainWindowAnchorRule/IntegratedOverlayCoordinator.
/// </summary>
public static class OrderAssistWindowSelectionRule
{
    /// <summary>
    /// Plain snapshot of one of PioneerRx's own top-level windows, already
    /// title-classified. <paramref name="Kind"/> is <see
    /// cref="OrderAssistWindowKind.None"/> for the (overwhelming majority
    /// of) windows that aren't either target screen — such candidates are
    /// never selected (see <see cref="Choose"/>) but the caller still
    /// builds one Candidate per visible PioneerRx window regardless of
    /// Kind, since OrderAssistCoordinator's own diagnostic logging wants
    /// the full visible-title list, matched or not.
    /// </summary>
    public readonly record struct Candidate(IntPtr Handle, OrderAssistWindowKind Kind, bool IsVisible, bool IsMinimized, Rectangle Bounds);

    /// <summary>
    /// Picks which candidate (if any) Order Assist should target this
    /// tick. Fast path: if the CURRENT foreground window is itself an
    /// eligible target-kind candidate, it wins outright — no behavior
    /// change from the old foreground-only code for the common case where
    /// that's already true. Otherwise, falls through to the FIRST eligible
    /// target-kind candidate in <paramref name="candidates"/>'s own order —
    /// callers pass candidates in EnumWindows' own top-to-bottom Z order
    /// (see OrderAssistWindowLocator.Scan), so this resolves to whichever
    /// target screen currently sits TOPMOST on screen: exactly the "Catalog
    /// Item Substitution Selection" dialog floating above the still-open
    /// "Create Recommended Orders" window in the owner's report, even
    /// though neither is foreground right now.
    /// </summary>
    public static Candidate? Choose(IReadOnlyList<Candidate> candidates, IntPtr foregroundHandle)
    {
        var eligible = candidates.Where(IsEligibleTarget).ToList();
        if (eligible.Count == 0) return null;

        if (foregroundHandle != IntPtr.Zero)
        {
            foreach (var candidate in eligible)
            {
                if (candidate.Handle == foregroundHandle) return candidate;
            }
        }

        return eligible[0];
    }

    private static bool IsEligibleTarget(Candidate candidate) =>
        candidate.Kind != OrderAssistWindowKind.None &&
        candidate.IsVisible &&
        !candidate.IsMinimized &&
        IsSaneWindowRect(candidate.Bounds);

    /// <summary>Same degenerate-rect guard as MainWindowAnchorRule.IsSaneWindowRect — never target a zero/negative-size rect.</summary>
    public static bool IsSaneWindowRect(Rectangle rect) => rect.Width > 0 && rect.Height > 0;
}
