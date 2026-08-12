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

    public AutomationElement WindowElement { get; }
    public Rectangle WindowBounds { get; }

    /// <summary>
    /// Native HWND of this window, or IntPtr.Zero if it couldn't be read
    /// (mirrors SafeNativeHandle's existing best-effort pattern below).
    /// Read once at attach time — combined with <see cref="RxNumber"/>
    /// and WindowBounds, this is the cache key EscriptImageCapture.
    /// ResolveCaptureRegion uses to avoid re-walking the UIA tree on
    /// every refresh (see Ocr/CaptureRegionCache.cs).
    /// </summary>
    public IntPtr NativeWindowHandle { get; }

    /// <summary>
    /// The same title-derived Rx identifier ScreenSignature/
    /// ExtractRxNumber compute below, captured once at attach time so
    /// callers (EscriptImageCapture's region cache) don't need a second
    /// UIA title read of their own.
    /// </summary>
    public string? RxNumber { get; }

    /// <summary>
    /// True when this instance came from TryAttach's FAST PATH (branch
    /// brief item 2d, latency fix) — the previously-resolved window was
    /// reused as-is (see AttachCacheDecision) instead of paying for a
    /// fresh top-level-window enumeration + disambiguation. Surfaced so
    /// OverlayViewModel can log it next to the "attach" timing bucket —
    /// see Diagnostics/RefreshTiming.cs AttachCacheHit.
    /// </summary>
    public bool WasAttachCacheHit { get; }

    /// <summary>
    /// The <c>AutomationBase</c> parameter TryAttach used to exist here
    /// (it's always <see cref="GetOrCreateSharedAutomation"/>'s shared
    /// instance now — see the ATTACH CACHE fields below) was dropped
    /// entirely rather than stored unused: nothing on this class needs
    /// to reference the automation session directly, only WindowElement
    /// (which already carries what it needs internally for FindFirst/
    /// FindAll calls).
    /// </summary>
    private PioneerRxWindow(AutomationElement windowElement, Application? application, bool wasAttachCacheHit)
    {
        WindowElement = windowElement;
        _application = application;
        WindowBounds = SafeBounds(windowElement);
        NativeWindowHandle = SafeNativeHandle(windowElement);
        RxNumber = ExtractRxNumber(SafeName(windowElement));
        WasAttachCacheHit = wasAttachCacheHit;
    }

    private static string SafeName(AutomationElement el)
    {
        try { return el.Name ?? ""; }
        catch { return ""; }
    }

    private static Rectangle SafeBounds(AutomationElement el)
    {
        try { return el.BoundingRectangle; }
        catch { return Rectangle.Empty; }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    // ------------------------------------------------------------------
    // ATTACH CACHE (latency fix, branch brief item 2d): the "attach"
    // timing bucket cost 240-335ms per refresh, and neither cost was
    // buying anything TryAttach's own callers actually needed on every
    // single call:
    //  - `new UIA3Automation()` was constructed and torn down (via
    //    PioneerRxWindow.Dispose -> automation.Dispose()) EVERY call,
    //    even though creating that COM automation session is real,
    //    fixed, per-instantiation overhead — not a per-window or
    //    per-property cost. GetOrCreateSharedAutomation below creates it
    //    once (lazily) and reuses it for the lifetime of the process.
    //  - The full top-level-window enumeration + disambiguation (see
    //    TryAttach's doc below) re-ran every call even when the exact
    //    same PioneerRx window was still both open AND still the
    //    foreground window — the fast path in TryAttach below skips
    //    straight to reusing that window in exactly the cases where the
    //    full disambiguation logic would have landed on the same answer
    //    anyway (see AttachCacheDecision).
    // Static, not per-instance: this overlay only ever tracks ONE
    // attached PioneerRx window at a time (mirrors Ocr/
    // CaptureRegionCache.cs and Uia/EnteredFieldElementCache.cs).
    // ------------------------------------------------------------------
    private static UIA3Automation? _sharedAutomation;
    private static IntPtr _cachedHandle = IntPtr.Zero;
    private static AutomationElement? _cachedWindowElement;

    private static UIA3Automation GetOrCreateSharedAutomation() => _sharedAutomation ??= new UIA3Automation();

    /// <summary>
    /// Attempts to find a top-level window whose title starts with one
    /// of FieldMap.TargetWindowTitlePrefixes. Returns null (does not
    /// throw) if none is currently open — callers should show "waiting
    /// for PioneerRx..." rather than crash, since the pharmacist may be
    /// on an unrelated screen at any given moment.
    ///
    /// FAST PATH (see the ATTACH CACHE fields above and
    /// AttachCacheDecision): if a window is already cached, two cheap
    /// Win32-only calls (IsWindow + GetForegroundWindow — no UIA/COM
    /// involved) decide whether it can be reused outright. Only when
    /// that's true is the cached AutomationElement asked for its current
    /// Name (one UIA property read, not a tree walk) to re-verify the
    /// title still looks like a target window — a cheap guard against
    /// the rare case of HWND reuse. Any failure at any step here falls
    /// straight through to the full resolve below; "when in doubt,
    /// re-resolve".
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
    ///      (This is also exactly the FAST PATH's own reuse condition —
    ///      see AttachCacheDecision's doc for why that can never regress
    ///      this disambiguation.)
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
        var automation = GetOrCreateSharedAutomation();

        if (_cachedWindowElement is not null && _cachedHandle != IntPtr.Zero)
        {
            var isAlive = IsWindow(_cachedHandle);
            var isForeground = isAlive && GetForegroundWindow() == _cachedHandle;

            // Short-circuit: only pay for the one UIA property read
            // (TitleStillMatches) when the two purely-native Win32 checks
            // already say this is worth checking — no reason to touch
            // the (possibly dead/stale) cached element at all otherwise.
            var titleStillMatches = isForeground && TitleStillMatches(_cachedWindowElement);

            if (AttachCacheDecision.CanReuseCachedWindow(isAlive, isForeground, titleStillMatches))
            {
                return new PioneerRxWindow(_cachedWindowElement, application: null, wasAttachCacheHit: true);
            }

            // Didn't pan out this call — fall through to the full
            // resolve below, which overwrites (or clears) the cache
            // with whatever it actually finds.
        }

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

            if (candidates.Count == 0)
            {
                _cachedHandle = IntPtr.Zero;
                _cachedWindowElement = null;
                return null;
            }

            var best = candidates.Count == 1 ? candidates[0] : (PickBestCandidate(candidates) ?? candidates[0]);

            _cachedHandle = SafeNativeHandle(best);
            _cachedWindowElement = best;

            return new PioneerRxWindow(best, application: null, wasAttachCacheHit: false);
        }
        catch
        {
            // The SHARED automation may itself have gone bad (e.g. the
            // accessibility service restarted) — recreate it so the
            // NEXT call can recover instead of staying permanently
            // broken, and drop the window cache alongside it (an
            // element from a torn-down automation session isn't safe to
            // reuse). Not retried inline here: the caller (OverlayViewModel.
            // RefreshAsync via SafeRefreshAsync) already turns this into
            // a status message / error dialog rather than crashing, same
            // as before this change, and the next refresh tick tries
            // again from a clean slate.
            //
            // Post-review fix: FieldReader.ElementCache holds its OWN
            // AutomationElement references, keyed only by window handle
            // — a same-handle cache hit there would otherwise survive
            // this reset untouched, reusing entered-field elements minted
            // under the automation session being disposed right here.
            // That would rely on undocumented COM disconnect-exception
            // behavior to ever self-heal rather than being invalidated
            // explicitly, so it's cleared alongside everything else.
            // CommonTabGate's own per-attach-session cache (Uia/
            // CommonTabGate.cs) holds the same kind of stale-session risk
            // — cleared here for the identical reason.
            _sharedAutomation?.Dispose();
            _sharedAutomation = null;
            _cachedHandle = IntPtr.Zero;
            _cachedWindowElement = null;
            FieldReader.InvalidateElementCache();
            CommonTabGate.InvalidateCache();
            throw;
        }
    }

    /// <summary>Cheap re-verification for the fast path above: does this element's CURRENT title still start with a target prefix? One UIA property read (Name), not a tree walk.</summary>
    private static bool TitleStillMatches(AutomationElement element)
    {
        string? name;
        try { name = element.Name; }
        catch { return false; }

        if (name is null) return false;

        foreach (var prefix in FieldMap.TargetWindowTitlePrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
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

    /// <summary>
    /// The automation session behind WindowElement is now ALWAYS the
    /// shared, process-lifetime instance (see
    /// GetOrCreateSharedAutomation) — never owned or disposed per
    /// PioneerRxWindow instance, only self-healing on a genuine failure
    /// (see TryAttach's catch block). Every existing call site still
    /// does `using var window = PioneerRxWindow.TryAttach();`, which is
    /// harmless: Dispose() here just no longer has an automation session
    /// of its own to tear down.
    /// </summary>
    public void Dispose()
    {
        _application?.Dispose();
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
