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
}
