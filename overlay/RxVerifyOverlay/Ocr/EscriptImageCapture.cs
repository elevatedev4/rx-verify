using System.Drawing;
using System.Drawing.Imaging;
using FlaUI.Core.AutomationElements;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.Uia;

namespace RxVerifyOverlay.Ocr;

/// <summary>
/// Captures a SCREEN REGION as a bitmap — never selects/switches any
/// PioneerRx tab, unlike the old FieldReader.ReadSource UIA path. This is
/// the whole point of VerifyOCR: whatever the pharmacist currently has
/// on screen (Escript tab, Image tab, anything) gets captured as pixels
/// and OCR'd, with zero interaction with PioneerRx itself.
///
/// REGION RESOLUTION (see ResolveCaptureRegion): the owner's screen isn't
/// visible from here, so the capture region MUST be configurable at
/// runtime rather than hardcoded — see OverlaySettings' capture-region
/// fields and MainWindow.xaml's "Engine settings" expander.
///
/// v1 NARROWING (branch brief item 6): v0 defaulted to
/// FieldMap.CenterTabControlAutomationId ("cntTabControl"), which
/// includes the Dispense/Image/Escript/... TAB STRIP itself, not just the
/// data pane beneath it — that toolbar row is exactly the chrome noise
/// src/ocr/parseEscriptOcr.ts has to skip on the TS side (see its "drop
/// everything above the first recognized label line" rule). The default
/// now prefers the Escript tree control itself
/// (FieldMap.EscriptTreeAutomationId, "ux10Dot6Escript" — the same
/// control the UIA/structured-read path already walks, see
/// Uia/UiaTreeWalker.BuildEscriptTree) when it's found AND genuinely
/// on-screen: its BoundingRectangle is just the data pane, with no tab
/// strip inside it. cntTabControl remains the fallback (covers both "the
/// Escript tab has never been opened this session, so its tree control
/// doesn't exist in the UIA tree at all yet" — a real, documented
/// FieldMap.cs caveat — AND "the Escript tab exists but isn't the
/// currently-visible tab", see OFFSCREEN GUARD below), then window bounds
/// as the last resort.
///
/// OFFSCREEN GUARD (post-review fix): VerifyOCR is explicitly "no tab
/// switch" — whatever tab the pharmacist actually has open gets OCR'd,
/// which may NOT be Escript. ux10Dot6Escript can exist in the UIA tree
/// (so FindDescendantByAutomationId finds it, and it may even report a
/// stale non-zero BoundingRectangle) while a DIFFERENT tab is the one
/// actually visible on screen. Trusting that rect blind would make
/// Graphics.CopyFromScreen grab whatever pixels are really there (e.g.
/// the Dispense tab) and silently OCR the wrong pane — no error, just a
/// wrong/garbage source. TryGetBoundingRectangle(requireOnscreen: true)
/// additionally checks the element's IsOffscreen automation property
/// (FlaUI: element.Properties.IsOffscreen) and rejects the rect if it's
/// offscreen OR if that property can't be read at all (unsupported by
/// this provider) — "unknown" is treated the same as "offscreen" here,
/// since a false NEGATIVE (wrongly skipping a genuinely visible Escript
/// pane) only costs falling back to cntTabControl — chrome-tolerant, no
/// pane switch — while a false POSITIVE (trusting a stale rect) risks
/// silently OCR'ing the wrong pane. cntTabControl itself is NOT put
/// through this guard — it's v0's proven region and, per the owner's own
/// live test, was already the reliable case this offscreen risk doesn't
/// apply to.
/// </summary>
public static class EscriptImageCapture
{
    /// <summary>
    /// REGION CACHE (latency fix, branch brief item 3): auto-detection
    /// (steps 2-4 below) walks the UIA tree, which is real work — caching
    /// its result per (window handle + Rx + window bounds) means only
    /// the FIRST refresh after attaching a given Rx pays that cost;
    /// every subsequent refresh of the same on-screen Rx (the common
    /// case under auto-watch, which can now fire far more often than the
    /// old 250ms poll — see Uia/TitleChangeWatcher.cs) reuses the cached
    /// rect instead. See Ocr/CaptureRegionCache.cs for the invalidation
    /// rule and its one documented gap. Deliberately a single static
    /// instance: this overlay only ever tracks one attached PioneerRx
    /// window at a time, so there's nothing to key beyond the signature
    /// itself.
    /// </summary>
    private static readonly CaptureRegionCache RegionCache = new();

    /// <summary>
    /// Resolves the screen rectangle to capture, in this priority order:
    ///   1. settings.UseExplicitCaptureRegion — an explicit L/T/W/H the
    ///      owner typed into Engine settings, used verbatim. This remains
    ///      the reliable escape hatch if auto-detection (steps 2-3) still
    ///      includes chrome, or ever mis-detects, on Will's actual screen.
    ///      Never cached (already O(1), and settings can change live).
    ///   2. REGION CACHE hit (see RegionCache field doc) — same window
    ///      handle + Rx number + window bounds as the last resolve, so
    ///      the UIA walk below is skipped entirely and the previously
    ///      resolved rect is reused as-is.
    ///   3. The Escript tree control's live BoundingRectangle
    ///      (FieldMap.EscriptTreeAutomationId, "ux10Dot6Escript") — the
    ///      DATA pane only, no tab strip. Only usable if that tab has
    ///      been opened at least once (so the control exists in the UIA
    ///      tree) AND is genuinely the on-screen tab right now — see
    ///      class doc "OFFSCREEN GUARD".
    ///   4. The center Escript pane's live BoundingRectangle
    ///      (cntTabControl) — v0's default, INCLUDES the tab strip/
    ///      toolbar row; kept as the fallback for step 3's gap (control
    ///      not found, OR found but offscreen/unverifiable).
    ///   5. window.WindowBounds (the whole PioneerRx window) as a last
    ///      resort if neither control can be found this call.
    /// Never throws — a UIA read failure at any step just falls through
    /// to the next, since a stale/wider region is a much less confusing
    /// failure mode than crashing the refresh. A freshly-resolved,
    /// non-empty rect from steps 3-5 is cached (keyed on the signature
    /// current at call time) before being returned.
    /// </summary>
    public static Rectangle ResolveCaptureRegion(PioneerRxWindow window, OverlaySettings settings)
    {
        if (settings.UseExplicitCaptureRegion)
        {
            return new Rectangle(settings.CaptureRegionLeft, settings.CaptureRegionTop,
                settings.CaptureRegionWidth, settings.CaptureRegionHeight);
        }

        var signature = new CaptureWindowSignature(window.NativeWindowHandle, window.RxNumber, window.WindowBounds);
        if (RegionCache.TryGet(signature, out var cachedRegion))
        {
            return cachedRegion;
        }

        var resolved = ResolveCaptureRegionUncached(window);
        if (resolved.Width > 0 && resolved.Height > 0)
        {
            RegionCache.Set(signature, resolved);
        }

        return resolved;
    }

    /// <summary>The actual UIA-walk-based auto-detection (steps 3-5 of ResolveCaptureRegion's doc) — factored out so the cache check/populate above stays easy to read.</summary>
    private static Rectangle ResolveCaptureRegionUncached(PioneerRxWindow window)
    {
        var walker = new UiaTreeWalker(window.WindowElement);

        // requireOnscreen: true — see class doc "OFFSCREEN GUARD". Only
        // this narrower pane needs the extra check; it's the one that can
        // exist-but-not-be-the-visible-tab under the "no tab switch" flow.
        var escriptDataPane = TryGetBoundingRectangle(walker, FieldMap.EscriptTreeAutomationId, requireOnscreen: true);
        if (escriptDataPane is { } narrowRect)
        {
            return narrowRect;
        }

        var tabControlPane = TryGetBoundingRectangle(walker, FieldMap.CenterTabControlAutomationId);
        if (tabControlPane is { } widerRect)
        {
            return widerRect;
        }

        return window.WindowBounds;
    }

    /// <summary>
    /// Best-effort BoundingRectangle lookup by AutomationId — null (not
    /// an exception) on "not found", a zero-size result, or (when
    /// <paramref name="requireOnscreen"/> is true) an offscreen/
    /// unverifiable element, so callers can chain fallbacks with a simple
    /// null-check. Any UIA read can throw if PioneerRx redraws mid-read
    /// (same defensive pattern as Uia/FieldReader.cs) — swallowed here
    /// for the same reason.
    /// </summary>
    /// <param name="requireOnscreen">
    /// See class doc "OFFSCREEN GUARD". When true, also rejects the
    /// element if its IsOffscreen automation property reads true, OR if
    /// that property can't be read at all (treated as "can't confirm
    /// it's visible" -&gt; reject, not accept) — the caller's fallback to
    /// the next candidate is the safe default, not trusting an
    /// unverifiable rect.
    /// </param>
    private static Rectangle? TryGetBoundingRectangle(UiaTreeWalker walker, string automationId, bool requireOnscreen = false)
    {
        try
        {
            var element = walker.FindDescendantByAutomationId(automationId);
            if (element is null) return null;

            var rect = element.BoundingRectangle;
            if (rect.Width <= 0 || rect.Height <= 0) return null;

            if (requireOnscreen && !IsGenuinelyOnscreen(element)) return null;

            return rect;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// True only if the element's IsOffscreen automation property was
    /// readable AND reads false. Any failure to read it (provider doesn't
    /// support the property, throws, etc.) returns false — "unknown" is
    /// NOT treated as "onscreen" (see TryGetBoundingRectangle's
    /// requireOnscreen doc for why that direction of error is the safe
    /// one here). Mirrors the existing defensive
    /// element.Properties.X.ValueOrDefault + try/catch pattern already
    /// used elsewhere for optional automation properties (see
    /// Uia/UiaTreeWalker.cs DumpRecursive's IsKeyboardFocusable read).
    /// </summary>
    private static bool IsGenuinelyOnscreen(AutomationElement element)
    {
        try
        {
            return !element.Properties.IsOffscreen.ValueOrDefault;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Grabs the given screen rectangle as a 32bpp ARGB Bitmap via GDI's
    /// Graphics.CopyFromScreen — the standard, cheap (no window handle
    /// needed, works across any window including ones not owned by this
    /// process) way to capture arbitrary screen pixels on Windows.
    /// Caller owns the returned Bitmap and must dispose it.
    /// </summary>
    public static Bitmap CaptureRegion(Rectangle region)
    {
        var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(region.Left, region.Top, 0, 0, region.Size, CopyPixelOperation.SourceCopy);
        return bitmap;
    }
}
