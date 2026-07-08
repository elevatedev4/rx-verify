using System.Drawing;
using System.Drawing.Imaging;
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
/// fields and MainWindow.xaml's "Engine settings" expander. Default
/// (no explicit override saved) is the center Escript pane's own
/// on-screen bounds (FieldMap.CenterTabControlAutomationId,
/// AutomationId "cntTabControl" — the same control FieldReader's UIA
/// path reads structured data from, so by default the OCR capture region
/// naturally tracks wherever that pane actually is on Will's monitor),
/// falling back to the whole PioneerRx window's bounds if that control
/// can't be found.
/// </summary>
public static class EscriptImageCapture
{
    /// <summary>
    /// Resolves the screen rectangle to capture, in this priority order:
    ///   1. settings.UseExplicitCaptureRegion — an explicit L/T/W/H the
    ///      owner typed into Engine settings, used verbatim.
    ///   2. The center Escript pane's live BoundingRectangle
    ///      (cntTabControl), read fresh off the currently-attached
    ///      PioneerRx window every call — tracks the window if it moves.
    ///   3. window.WindowBounds (the whole PioneerRx window) as a last
    ///      resort if cntTabControl can't be found this call.
    /// Never throws — a UIA read failure at step 2 just falls through to
    /// step 3, since a stale/empty region is a much less confusing
    /// failure mode than crashing the refresh.
    /// </summary>
    public static Rectangle ResolveCaptureRegion(PioneerRxWindow window, OverlaySettings settings)
    {
        if (settings.UseExplicitCaptureRegion)
        {
            return new Rectangle(settings.CaptureRegionLeft, settings.CaptureRegionTop,
                settings.CaptureRegionWidth, settings.CaptureRegionHeight);
        }

        try
        {
            var walker = new UiaTreeWalker(window.WindowElement);
            var escriptPane = walker.FindDescendantByAutomationId(FieldMap.CenterTabControlAutomationId);
            if (escriptPane is not null)
            {
                var rect = escriptPane.BoundingRectangle;
                if (rect.Width > 0 && rect.Height > 0)
                {
                    return rect;
                }
            }
        }
        catch
        {
            // Fall through to the WindowBounds fallback below — any UIA
            // read can throw if PioneerRx redraws mid-read (same
            // defensive pattern as Uia/FieldReader.cs).
        }

        return window.WindowBounds;
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
