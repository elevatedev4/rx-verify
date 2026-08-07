using System.Threading.Tasks;

namespace RxVerifyOverlay.Ocr;

/// <summary>
/// Lets OcrFieldReader briefly get the overlay window itself out of the
/// way of a screen capture. The overlay is Topmost="True" and freely
/// movable/resizable (see MainWindow.xaml) — if the pharmacist has
/// dragged/resized it so it overlaps the capture region, an un-guarded
/// Graphics.CopyFromScreen would OCR the OVERLAY'S OWN UI (verdict table,
/// buttons, etc.) instead of the e-script, producing garbage text with no
/// obvious explanation. MainWindow implements this; OcrFieldReader calls
/// it around EscriptImageCapture.CaptureRegion only — see
/// OcrFieldReader.ReadSourceFromOcrAsync.
///
/// Deliberately its own tiny interface (not just exposing
/// Window.Hide()/Show() directly) so OcrFieldReader/OverlayViewModel
/// don't need a WPF Window reference at all, and so the "wait for the
/// screen to actually repaint" behavior lives in one place (MainWindow,
/// which owns the Dispatcher) rather than being reimplemented per-caller.
/// </summary>
public interface IOverlayVisibilityController
{
    /// <summary>
    /// Hides the overlay and waits long enough for the screen area it
    /// was covering to actually repaint (DWM composition isn't
    /// synchronous with the Visibility change) before returning. Must be
    /// called on the UI thread; the returned Task completes on the UI
    /// thread too.
    /// </summary>
    Task HideForCaptureAsync();

    /// <summary>Restores the overlay's visibility — always call this in a finally, even if the capture in between threw.</summary>
    void RestoreAfterCapture();

    /// <summary>
    /// True once SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) has
    /// been applied to the overlay's HWND and confirmed successful — in
    /// that state HideForCaptureAsync/RestoreAfterCapture are no-ops
    /// (Windows itself omits the overlay from any GDI capture, so there's
    /// nothing to hide/show), which is exactly the "freely-dragged
    /// Topmost overlay overlapping the capture region" scenario this
    /// class's doc above warns about — see MainWindow.xaml.cs
    /// OnSourceInitialized. False means the hide/show fallback above is
    /// the one actually running. Surfaced (not just an internal detail)
    /// so OverlayViewModel can log/report which capture path was live —
    /// see Ocr/OcrLogger.cs's startup log line and RxLogFormatter's
    /// "Capture exclusion" line in the "Copy logs" blob, both added
    /// specifically because a silent WDA_EXCLUDEFROMCAPTURE failure with
    /// no visibility would let the overlay's own UI feed the OCR pass
    /// with no obvious explanation — the same failure mode the class doc
    /// above already calls out for the un-guarded case.
    /// </summary>
    bool IsExcludedFromCapture { get; }
}
