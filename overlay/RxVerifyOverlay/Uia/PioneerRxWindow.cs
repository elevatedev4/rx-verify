using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace RxVerifyOverlay.Uia;

/// <summary>
/// Finds and attaches to the active PioneerRx Pre-Check/Edit/New-Rx
/// window, using UIA3 (the modern UIA COM API — see FlaUI.UIA3).
///
/// This used to also own rough fractional-panel-bounds geometry
/// (LeftPanelBounds/CenterPanelBounds/CenterPatientBoxBounds/etc.) to
/// disambiguate repeated labels like "Address:"/"Phone:" by screen
/// position. That was inferred from screenshots, never validated, and
/// has been removed entirely: both the ENTERED fields (FieldReader.
/// ReadEntered) and the SOURCE e-script (FieldReader.ReadSource) are now
/// found by AutomationId / Escript-tree-node-name (see FieldMap.cs and
/// UiaTreeWalker.cs), which needs no panel geometry at all — see
/// FieldMap.cs header for the two real UIA dumps this was confirmed
/// against.
/// </summary>
public sealed class PioneerRxWindow : IDisposable
{
    private readonly Application? _application;
    private readonly AutomationBase _automation;

    public AutomationElement WindowElement { get; }
    public Rectangle WindowBounds { get; }

    private PioneerRxWindow(AutomationBase automation, AutomationElement windowElement, Application? application)
    {
        _automation = automation;
        WindowElement = windowElement;
        _application = application;
        WindowBounds = SafeBounds(windowElement);
    }

    private static Rectangle SafeBounds(AutomationElement el)
    {
        try { return el.BoundingRectangle; }
        catch { return Rectangle.Empty; }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    /// <summary>
    /// Attempts to find a top-level window whose title starts with one
    /// of FieldMap.TargetWindowTitlePrefixes. Returns null (does not
    /// throw) if none is currently open — callers should show "waiting
    /// for PioneerRx..." rather than crash, since the pharmacist may be
    /// on an unrelated screen at any given moment.
    ///
    /// DISAMBIGUATING MULTIPLE MATCHES (latency fix — field report of an
    /// auto-watch transition missed for ~55s, not just delayed): if MORE
    /// THAN ONE target-prefixed window is open at once (e.g. an older
    /// "Pre-Check Rx" window Will hasn't closed yet, alongside a
    /// newly-opened one for a different Rx), UIA's FindAllChildren
    /// enumeration order is NOT documented as z-order/foreground-first —
    /// picking the first match could keep returning the SAME stale
    /// window forever, meaning GetScreenSignature (below) would never
    /// observe the new window's title at all. So when there's more than
    /// one candidate:
    ///   1. Prefer whichever candidate IS the current OS foreground
    ///      window (GetForegroundWindow, compared by native HWND) — the
    ///      common case: Will just switched to/opened the new screen.
    ///   2. Otherwise (e.g. PioneerRx itself is behind some other app,
    ///      so none of the candidates are foreground), prefer whichever
    ///      candidate is highest in Z-order among top-level windows —
    ///      EnumWindows enumerates top-level windows top-to-bottom in
    ///      Z-order (long-standing, widely-relied-on Win32 behavior,
    ///      even though not a formal MSDN contract), so the first
    ///      candidate HWND it reports is the most-recently-active one.
    ///   3. If neither native call yields a usable HWND for any
    ///      candidate (e.g. NativeWindowHandle unavailable through this
    ///      accessibility API in some edge case), fall back to the
    ///      original first-FindAllChildren-match behavior rather than
    ///      returning null — a possibly-stale match beats none.
    /// With a single candidate, none of this runs — same fast path as
    /// before.
    /// </summary>
    public static PioneerRxWindow? TryAttach()
    {
        var automation = new UIA3Automation();
        try
        {
            var desktop = automation.GetDesktop();
            var allTopLevel = desktop.FindAllChildren();

            var candidates = new List<AutomationElement>();
            foreach (var window in allTopLevel)
            {
                string? name;
                try { name = window.Name; }
                catch { continue; }

                if (name is null) continue;

                foreach (var prefix in FieldMap.TargetWindowTitlePrefixes)
                {
                    if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        candidates.Add(window);
                        break;
                    }
                }
            }

            if (candidates.Count == 0) return null;
            if (candidates.Count == 1) return new PioneerRxWindow(automation, candidates[0], application: null);

            var best = PickBestCandidate(candidates) ?? candidates[0];
            return new PioneerRxWindow(automation, best, application: null);
        }
        catch
        {
            automation.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Implements TryAttach's disambiguation doc (foreground match, then
    /// Z-order-topmost match) across MORE THAN ONE target-prefixed
    /// candidate. Returns null if no candidate has a usable native HWND
    /// at all, in which case TryAttach falls back to enumeration order.
    /// </summary>
    private static AutomationElement? PickBestCandidate(List<AutomationElement> candidates)
    {
        var handles = new Dictionary<IntPtr, AutomationElement>();
        foreach (var candidate in candidates)
        {
            var handle = SafeNativeHandle(candidate);
            if (handle != IntPtr.Zero && !handles.ContainsKey(handle))
            {
                handles[handle] = candidate;
            }
        }

        if (handles.Count == 0) return null;

        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero && handles.TryGetValue(foreground, out var foregroundMatch))
        {
            return foregroundMatch;
        }

        // No candidate is foreground (e.g. PioneerRx is behind some
        // other app) — walk top-level windows in Z-order and take the
        // first one that's also one of our candidates, i.e. the
        // most-recently-active target window.
        AutomationElement? topmost = null;
        EnumWindows((hWnd, _) =>
        {
            if (handles.TryGetValue(hWnd, out var match))
            {
                topmost = match;
                return false; // stop enumerating, we found the topmost candidate
            }
            return true; // keep going
        }, IntPtr.Zero);

        return topmost;
    }

    private static IntPtr SafeNativeHandle(AutomationElement element)
    {
        try
        {
            var handle = element.FrameworkAutomationElement.NativeWindowHandle;
            return handle ?? IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        _application?.Dispose();
        _automation.Dispose();
    }

    /// <summary>
    /// Cheap snapshot of "is the pre-check/edit/new-rx screen open, and
    /// which Rx is it showing" — just Present + an opaque RxNumber
    /// string parsed from the window title. Used by
    /// OverlayViewModel.WatchAsync (W-T9 item 5, auto-watch) to detect
    /// when a full refresh is warranted WITHOUT doing a full refresh's
    /// work every tick.
    /// </summary>
    public readonly record struct ScreenSignature(bool Present, string? RxNumber)
    {
        public static readonly ScreenSignature NotPresent = new(false, null);
    }

    /// <summary>
    /// Attaches only long enough to read the window's title (.Name), then
    /// immediately disposes — no FieldReader panel walk, no Escript tree
    /// read, no engine subprocess call. This is the whole point: it costs
    /// roughly what TryAttach's own top-level-window scan costs, so it's
    /// safe to call on a short (~1s) timer for change-detection, unlike a
    /// full RefreshAsync which also reads both UIA panels and calls the
    /// TS engine.
    ///
    /// PioneerRx window titles always start with the screen name followed
    /// by the Rx number ("Edit Rx - &lt;rx number&gt; - ...", confirmed
    /// in real UIA dumps — see FieldMap.cs doc). RxNumber is parsed as
    /// the segment between the first two " - " separators; if the title
    /// doesn't have that shape (e.g. a fresh "New Rx" screen with no
    /// number assigned yet), the RxNumber falls back to the full title
    /// text itself, so a change in title (e.g. a different Rx opened, or
    /// New Rx -> a saved/numbered Rx) still trips change-detection even
    /// without a parseable number.
    /// </summary>
    public static ScreenSignature GetScreenSignature()
    {
        using var window = TryAttach();
        if (window is null) return ScreenSignature.NotPresent;

        string name;
        try { name = window.WindowElement.Name ?? ""; }
        catch { name = ""; }

        return new ScreenSignature(true, ExtractRxNumber(name));
    }

    /// <summary>
    /// Parses the Rx number segment out of a title of shape
    /// "&lt;Screen Name&gt; - &lt;rx number&gt; - ...". Splits on " - "
    /// and returns the second segment if there are at least 3 segments;
    /// otherwise returns the whole title unchanged (see
    /// GetScreenSignature doc for why that fallback still works for
    /// change-detection).
    /// </summary>
    private static string? ExtractRxNumber(string title)
    {
        if (string.IsNullOrEmpty(title)) return null;

        var parts = title.Split(" - ", StringSplitOptions.None);
        return parts.Length >= 3 ? parts[1].Trim() : title;
    }
}
