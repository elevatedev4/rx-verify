using System;
using System.Drawing;

namespace RxVerifyOverlay.Ocr;

/// <summary>
/// Identifies "the same attached PioneerRx window, showing the same Rx,
/// at the same screen position/size" for CaptureRegionCache below. A
/// plain value-type record (System.IntPtr/System.Drawing.Rectangle only
/// — no UIA/WPF types) so equality is automatic field-by-field and this
/// stays usable from a non-Windows test host.
///
/// WindowHandle + RxNumber mirrors the same "window + Rx" identity
/// PioneerRxWindow.ScreenSignature already uses for the auto-watch
/// timer's cheap change-detection (see PioneerRxWindow.cs). WindowBounds
/// is added on top so a move/resize of the SAME Rx/window (no title or
/// handle change) still invalidates the cache — cheap to include since
/// PioneerRxWindow already reads WindowBounds once per attach regardless.
/// </summary>
public readonly record struct CaptureWindowSignature(IntPtr WindowHandle, string? RxNumber, Rectangle WindowBounds);

/// <summary>
/// Caches the screen-capture region EscriptImageCapture.ResolveCaptureRegion
/// resolves via a UIA tree walk, so that walk only has to run once per
/// (attached window + Rx + window bounds) instead of on every single
/// refresh — branch brief item 3 (latency fix: caching removes a UIA
/// walk from the hot path of every auto-watch-triggered refresh).
///
/// Deliberately a tiny standalone class (not just static fields inside
/// EscriptImageCapture) so the invalidation decision — same signature
/// reuses the cached rect, any mismatch re-resolves — is covered by
/// fast xUnit tests with no live PioneerRx window or Windows runtime
/// needed; see RxVerifyOverlay.Tests/CaptureRegionCacheTests.cs.
///
/// "When in doubt, re-resolve" (branch brief item 3): TryGet is a plain
/// equality check with no fuzzy/partial matching — anything other than
/// an exact signature match, including nothing cached yet, is a miss.
/// One known gap, called out here rather than hidden: if the Escript
/// tree control (FieldMap.EscriptTreeAutomationId) doesn't exist yet on
/// the first resolve for a given Rx (tab never opened this session — see
/// EscriptImageCapture's class doc), that first resolve falls back to
/// the wider cntTabControl rect and THAT gets cached; opening the
/// Escript tab for the first time afterward, for the same window/Rx/
/// bounds, won't narrow the cached rect until the signature changes
/// (different Rx, moved/resized window, or a live invalidation). Not
/// fixed here per the brief's "keep it simple" — flagged for Will to
/// judge whether it matters in practice.
/// </summary>
public sealed class CaptureRegionCache
{
    private CaptureWindowSignature? _signature;
    private Rectangle _region;

    /// <summary>True (with <paramref name="region"/> populated) only if the cache was previously Set with this exact signature.</summary>
    public bool TryGet(CaptureWindowSignature signature, out Rectangle region)
    {
        if (_signature is { } cached && cached.Equals(signature))
        {
            region = _region;
            return true;
        }

        region = Rectangle.Empty;
        return false;
    }

    public void Set(CaptureWindowSignature signature, Rectangle region)
    {
        _signature = signature;
        _region = region;
    }

    /// <summary>Forces the next TryGet to miss regardless of signature. Not called by production code today (signature comparison already covers every invalidation case the brief called for) — kept as an explicit escape hatch for tests and any future invalidation trigger.</summary>
    public void Invalidate()
    {
        _signature = null;
    }
}
