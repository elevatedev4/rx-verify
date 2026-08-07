using System;
using System.Collections.Generic;

namespace RxVerifyOverlay.Uia;

/// <summary>
/// Caches the located UIA element for each ENTERED-side field
/// (FieldReader.ReadEntered), keyed by AutomationId, for one attached
/// PioneerRx window instance at a time — latency fix (branch brief
/// "fix-uia-read-latency": FieldReader.ReadEntered was the dominant
/// ~2.5-3s "uia" timing bucket because every one of its ~14 fields did
/// its own FindFirstDescendant walk from the window root on EVERY
/// refresh). PioneerRx's Pre-Check/Edit/New-Rx window layout is static
/// per session — the SAME control is uxPatientQuickSearch etc. for as
/// long as that window instance stays open — so the element only needs
/// to be FOUND once per window; only its current VALUE needs re-reading
/// on every refresh (see FieldReader.ResolveElement/ReadWithRetry,
/// which do that re-read — this class only ever stores element
/// REFERENCES, never values, per branch brief item 4: "NEVER serve
/// stale entered-field VALUES").
///
/// Deliberately generic over TElement (rather than referencing FlaUI's
/// AutomationElement directly) so the cache/invalidation bookkeeping
/// here is testable with a plain dummy type and no FlaUI/UIA/Windows
/// dependency at all — see RxVerifyOverlay.Tests/
/// EnteredFieldElementCacheTests.cs. FieldReader is the only production
/// caller, instantiating this as EnteredFieldElementCache&lt;AutomationElement&gt;.
///
/// THREADING: like Ocr/CaptureRegionCache.cs, deliberately unsynchronized
/// — every caller in this codebase (RefreshAsync/WatchAsync/DumpUiaTree)
/// runs on the WPF UI thread with no `await` between resolving a window
/// and finishing a read, so there's no real concurrent access to guard
/// against.
/// </summary>
public sealed class EnteredFieldElementCache<TElement> where TElement : class
{
    private IntPtr _windowHandle;
    private bool _hasWindow;
    private readonly Dictionary<string, TElement> _elements = new();
    private readonly HashSet<string> _everNonBlank = new();

    /// <summary>
    /// True (with <paramref name="element"/> populated) only if an
    /// element was previously cached for THIS EXACT window handle and
    /// AutomationId. A different window handle than last time silently
    /// resets the whole cache first (see EnsureWindow) — old elements
    /// belong to a window that may no longer even exist, and must never
    /// be handed back for a different one.
    /// </summary>
    public bool TryGetElement(IntPtr windowHandle, string automationId, out TElement? element)
    {
        EnsureWindow(windowHandle);
        return _elements.TryGetValue(automationId, out element);
    }

    public void SetElement(IntPtr windowHandle, string automationId, TElement element)
    {
        EnsureWindow(windowHandle);
        _elements[automationId] = element;
    }

    /// <summary>Evicts one field's cached element (e.g. a cached-but-now-stale element that threw on read) without disturbing any other field's cache entry.</summary>
    public void InvalidateField(IntPtr windowHandle, string automationId)
    {
        EnsureWindow(windowHandle);
        _elements.Remove(automationId);
    }

    /// <summary>
    /// Has this field EVER produced a non-blank read for the CURRENT
    /// window (see MarkNonBlank)? Used by FieldReader's retry-on-
    /// suspicion logic (branch brief item 4): a blank read is trusted
    /// outright the first time (a genuinely empty field is normal — the
    /// pharmacist hasn't filled it in yet), but a blank read AFTER this
    /// field has previously read real data is treated as suspicious
    /// (possible stale/wrong element) and triggers one re-resolve before
    /// being trusted.
    /// </summary>
    public bool HasEverReadNonBlank(IntPtr windowHandle, string automationId)
    {
        EnsureWindow(windowHandle);
        return _everNonBlank.Contains(automationId);
    }

    public void MarkNonBlank(IntPtr windowHandle, string automationId)
    {
        EnsureWindow(windowHandle);
        _everNonBlank.Add(automationId);
    }

    /// <summary>
    /// Full reset the moment <paramref name="windowHandle"/> differs from
    /// whatever this cache was last used for (including the very first
    /// call ever, when nothing is cached yet) — every element reference
    /// AND every "has read non-blank" flag from the old window is
    /// discarded, since neither is meaningful for a different window
    /// instance (which may not even still exist).
    /// </summary>
    private void EnsureWindow(IntPtr windowHandle)
    {
        if (_hasWindow && _windowHandle == windowHandle) return;

        _windowHandle = windowHandle;
        _hasWindow = true;
        _elements.Clear();
        _everNonBlank.Clear();
    }
}
