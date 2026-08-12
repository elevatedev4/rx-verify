using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace RxVerifyOverlay.Integrated;

/// <summary>
/// ROUND 7 (owner, live pharmacy testing: "when Pioneer opens a small
/// window [e.g. an 'add comment on prescription' dialog] above its main
/// window, the integrated control box moves down/over to that popup. It
/// needs to stay put at the top-right of the MAIN window"): pure decision
/// behind WHICH of PioneerRx's own top-level windows counts as its MAIN
/// window, and whether to keep anchoring to one already chosen.
///
/// THE BUG THIS REPLACES: round 5's ForegroundAnchorRule derived the
/// control-box anchor from GetAncestor(foregroundHwnd, GA_ROOTOWNER) —
/// walking a popup UP to whatever owns it. That only resolves back to the
/// main window when the popup is genuinely OWNED. PioneerRx evidently
/// opens some top-level windows (this "add comment" dialog, at least)
/// that aren't owned by anything at all, so GA_ROOTOWNER just returns the
/// popup's own hwnd/rect unchanged, and the box jumped to it.
///
/// THE FIX: stop deriving the main window from whatever happens to be
/// foreground at all — POSITIVELY identify it among Pioneer's own
/// top-level windows. Integrated mode is already maximized-only, so the
/// main window is whichever of Pioneer's top-level windows is actually
/// maximized; if more than one is (a pharmacist could have two Pioneer
/// windows open), the largest by rect area wins. If NONE is currently
/// maximized (e.g. mid-restore-from-minimize), fall back to the largest
/// visible, non-minimized top-level window instead of finding nothing.
///
/// STICKY: once a main-window hwnd has been chosen, <see cref="Resolve"/>
/// keeps returning THAT SAME hwnd (with its current rect) on every
/// subsequent call as long as it's still among the candidates and still
/// visible/not-minimized — regardless of which window is foreground right
/// now, and even if it's no longer maximized (a pharmacist mid-drag/
/// restore shouldn't cause a re-pick mid-gesture). Only a closed, hidden,
/// or minimized cached window forces a fresh <see cref="Choose"/>.
///
/// IntegratedOverlayCoordinator supplies the Win32 data (EnumWindows +
/// GetWindowThreadProcessId filtered to FieldMap.TargetProcessNames,
/// IsWindowVisible, IsIconic, IsZoomed, GetWindowRect) — this class never
/// touches Win32 itself, so it's testable on Mac.
/// </summary>
public static class MainWindowAnchorRule
{
    /// <summary>Plain Win32 snapshot of one of PioneerRx's own top-level windows.</summary>
    public readonly record struct Candidate(IntPtr Handle, bool IsVisible, bool IsMinimized, bool IsMaximized, Rectangle Bounds);

    /// <summary>Which HWND/rect pair to anchor the control box to.</summary>
    public readonly record struct Anchor(IntPtr Handle, Rectangle Bounds);

    /// <summary>
    /// STICKY entry point — call this every tick. <paramref name="cachedHandle"/>
    /// is whatever <see cref="Anchor.Handle"/> the PREVIOUS call returned
    /// (IntPtr.Zero if there wasn't one yet, e.g. the very first tick or
    /// after a run where nothing was found). If that handle is still
    /// present among <paramref name="candidates"/> and still eligible
    /// (visible, not minimized, sane rect — see <see cref="IsEligible"/>),
    /// its current rect is returned and nothing else is re-evaluated;
    /// otherwise falls through to a fresh <see cref="Choose"/>.
    /// </summary>
    public static Anchor? Resolve(IntPtr cachedHandle, IReadOnlyList<Candidate> candidates)
    {
        if (cachedHandle != IntPtr.Zero)
        {
            foreach (var candidate in candidates)
            {
                if (candidate.Handle == cachedHandle && IsEligible(candidate))
                {
                    return new Anchor(candidate.Handle, candidate.Bounds);
                }
            }
        }

        return Choose(candidates);
    }

    /// <summary>
    /// Fresh selection with no memory of any previous pick: among the
    /// eligible (visible, not minimized, sane rect) candidates, prefer
    /// maximized ones — largest by rect area if more than one qualifies;
    /// if none is maximized, fall back to the largest eligible candidate
    /// overall. Null when there are no eligible candidates at all (e.g.
    /// PioneerRx has no window open, or every one is minimized/invisible).
    /// </summary>
    public static Anchor? Choose(IReadOnlyList<Candidate> candidates)
    {
        var eligible = candidates.Where(IsEligible).ToList();
        if (eligible.Count == 0) return null;

        var maximized = eligible.Where(c => c.IsMaximized).ToList();
        var pool = maximized.Count > 0 ? maximized : eligible;

        var best = pool.OrderByDescending(c => Area(c.Bounds)).First();
        return new Anchor(best.Handle, best.Bounds);
    }

    private static bool IsEligible(Candidate candidate) =>
        candidate.IsVisible && !candidate.IsMinimized && IsSaneWindowRect(candidate.Bounds);

    /// <summary>Same degenerate-rect guard round 5's (now-removed) ForegroundAnchorRule.IsSaneWindowRect used — never anchor to a zero/negative-size rect.</summary>
    public static bool IsSaneWindowRect(Rectangle rect) => rect.Width > 0 && rect.Height > 0;

    private static long Area(Rectangle rect) => (long)rect.Width * rect.Height;
}
