using RxVerifyOverlay.Integrated;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for RibbonStatusTextShortener (2026-08-19, Will verbatim:
/// "Just say 'waiting' instead of the long string about waiting for a
/// precheck. The current string is hiding all the buttons."). Each
/// non-fallback case below uses the EXACT literal text one of
/// ViewModels/OverlayViewModel.cs's StatusMessage assignments produces
/// (copied verbatim from that file, including trailing punctuation), so a
/// future edit to that wording that silently stops matching a prefix here
/// shows up as a failing test rather than a re-broken ribbon.
/// </summary>
public class RibbonStatusTextShortenerTests
{
    [Fact]
    public void TheReportedWaitingStringShortensToWaiting()
    {
        // The exact string from Will's report.
        Assert.Equal("Waiting", RibbonStatusTextShortener.Shorten("Waiting for a PioneerRx Pre-Check/Edit/New Rx window..."));
    }

    [Fact]
    public void CheckedWithTimingDetailShortensToChecked()
    {
        Assert.Equal("Checked", RibbonStatusTextShortener.Shorten("Checked 3:45:12 PM (612ms). Drug lookup running…"));
    }

    [Fact]
    public void CheckedBareShortensToChecked()
    {
        Assert.Equal("Checked", RibbonStatusTextShortener.Shorten("Checked 3:45:12 PM."));
    }

    [Fact]
    public void NotAnEscriptShortensToNotEscript()
    {
        Assert.Equal("Not escript", RibbonStatusTextShortener.Shorten("Rx is not an escript."));
    }

    [Fact]
    public void OcrNotEnoughTextShortensToNoOcrText()
    {
        const string full = "OCR didn't find enough text on the captured e-script image to attempt a comparison. " +
                             "Check the capture region (Engine settings) and the raw OCR text below.";
        Assert.Equal("No OCR text", RibbonStatusTextShortener.Shorten(full));
    }

    [Fact]
    public void NoWindowToDumpShortensToNoWindow()
    {
        Assert.Equal("No window", RibbonStatusTextShortener.Shorten("No PioneerRx window found to dump."));
    }

    [Fact]
    public void OpenEscriptTabFallbackShortensToOpenEscript()
    {
        Assert.Equal("Open Escript", RibbonStatusTextShortener.Shorten("Open the Escript tab to verify this e-script."));
    }

    [Fact]
    public void UiaReadFailedShortensToUiaError()
    {
        Assert.Equal("UIA error", RibbonStatusTextShortener.Shorten("UIA read failed: Object reference not set to an instance of an object.. Try 'Dump UIA Tree' to diagnose."));
    }

    [Fact]
    public void UiaSourceReadFailedShortensToUiaError()
    {
        Assert.Equal("UIA error", RibbonStatusTextShortener.Shorten("UIA source read failed: some exception message here. Try 'Dump UIA Tree' to diagnose."));
    }

    [Fact]
    public void DrugLookupFailedShortensToLookupError()
    {
        Assert.Equal("Lookup error", RibbonStatusTextShortener.Shorten("Drug lookup failed: request timed out"));
    }

    // ---- Fallback: fully dynamic messages (ocrResult.Error/fastResult.Error/
    // result.Error/reader.SourceUnavailableReason) have no fixed prefix to
    // match — generic length-capped fallback instead. ----

    [Fact]
    public void ShortUnrecognizedTextPassesThroughUnchanged()
    {
        Assert.Equal("Short msg", RibbonStatusTextShortener.Shorten("Short msg"));
    }

    [Fact]
    public void LongUnrecognizedTextIsTruncatedWithEllipsis()
    {
        const string longDynamicError = "Something completely unexpected went wrong reading the capture region";
        var shortened = RibbonStatusTextShortener.Shorten(longDynamicError);

        Assert.EndsWith("…", shortened);
        Assert.True(shortened.Length <= 15); // FallbackMaxLength (14) + the ellipsis char
    }

    [Fact]
    public void NullInputYieldsEmptyString()
    {
        Assert.Equal("", RibbonStatusTextShortener.Shorten(null));
    }

    [Fact]
    public void EmptyInputYieldsEmptyString()
    {
        Assert.Equal("", RibbonStatusTextShortener.Shorten(""));
    }
}
