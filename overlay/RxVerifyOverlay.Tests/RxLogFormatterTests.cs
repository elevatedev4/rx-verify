using System;
using System.Collections.Generic;
using RxVerifyOverlay.Diagnostics;
using RxVerifyOverlay.Models;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for RxLogFormatter.BuildLogBlob (Diagnostics/
/// RxLogFormatter.cs) — the pure formatter behind the "Copy logs" button
/// (OverlayViewModel.BuildCurrentLogBlob / MainWindow.xaml.cs
/// OnCopyLogsClick). All values below are synthetic — no real
/// patient/prescriber data.
/// </summary>
public class RxLogFormatterTests
{
    private static RxLogSnapshot MakeSnapshot(
        IReadOnlyList<RxLogCategorySnapshot>? categories = null,
        IReadOnlyList<string>? notes = null,
        IReadOnlyList<OcrWord>? ocrWords = null,
        string? rawOcrText = "SYNTHETIC RAW OCR TEXT",
        string? rxWindowTitle = "Edit Rx - 0000001 - PioneerRx",
        string? ocrStatusText = "OCR: 400ms (capture 100ms + ocr 300ms) · 512 chars",
        int greenCount = 10,
        int yellowCount = 2,
        int redCount = 1)
    {
        return new RxLogSnapshot
        {
            CapturedAt = new DateTime(2026, 1, 2, 3, 4, 5),
            AppVersion = "1.2.3.0",
            CommitSha = "abc12345",
            Method = "OCR",
            RxWindowTitle = rxWindowTitle,
            StatusMessage = "Last checked 3:04:05 AM.",
            OcrStatusText = ocrStatusText ?? "",
            RawOcrText = rawOcrText,
            OcrWords = ocrWords ?? Array.Empty<OcrWord>(),
            Categories = categories ?? Array.Empty<RxLogCategorySnapshot>(),
            Notes = notes ?? Array.Empty<string>(),
            GreenCount = greenCount,
            YellowCount = yellowCount,
            RedCount = redCount
        };
    }

    [Fact]
    public void IncludesHeaderMetadata_VersionCommitMethodAndRxWindow()
    {
        var blob = RxLogFormatter.BuildLogBlob(MakeSnapshot());

        Assert.Contains("App version: 1.2.3.0", blob);
        Assert.Contains("Commit: abc12345", blob);
        Assert.Contains("Method: OCR", blob);
        Assert.Contains("Rx window: Edit Rx - 0000001 - PioneerRx", blob);
        Assert.Contains("Status: Last checked 3:04:05 AM.", blob);
    }

    [Fact]
    public void IncludesFieldVerdictsWithSourceEnteredAndReason()
    {
        var categories = new List<RxLogCategorySnapshot>
        {
            new("Patient", "Partial match", new List<RxLogFieldSnapshot>
            {
                new("patientName", "Name", "Green", "Jane Synthtest", "Jane Synthtest", "", ""),
                new("patientDOB", "DOB", "Yellow", "(not provided)", "01/01/2000", "not_provided", "Source did not provide a DOB.")
            })
        };

        var blob = RxLogFormatter.BuildLogBlob(MakeSnapshot(categories: categories));

        Assert.Contains("[Patient — Partial match]", blob);
        Assert.Contains("Name (patientName): Green", blob);
        Assert.Contains("source=\"Jane Synthtest\"  entered=\"Jane Synthtest\"", blob);
        Assert.Contains("DOB (patientDOB): Yellow", blob);
        Assert.Contains("reason=[not_provided] Source did not provide a DOB.", blob);
        Assert.Contains("Summary: 10 green / 2 yellow / 1 red", blob);
    }

    [Fact]
    public void EmptyCategoryRendersNoDataMarker()
    {
        var categories = new List<RxLogCategorySnapshot>
        {
            new("Prescriber", "No data", Array.Empty<RxLogFieldSnapshot>())
        };

        var blob = RxLogFormatter.BuildLogBlob(MakeSnapshot(categories: categories));

        Assert.Contains("[Prescriber — No data]", blob);
        Assert.Contains("(no data)", blob);
    }

    [Fact]
    public void IncludesNotesWhenPresent()
    {
        var blob = RxLogFormatter.BuildLogBlob(MakeSnapshot(notes: new[] { "SYNTHETIC NOTE: verify quantity" }));

        Assert.Contains("--- E-script notes ---", blob);
        Assert.Contains("SYNTHETIC NOTE: verify quantity", blob);
    }

    [Fact]
    public void OmitsNotesSectionWhenEmpty()
    {
        var blob = RxLogFormatter.BuildLogBlob(MakeSnapshot(notes: Array.Empty<string>()));

        Assert.DoesNotContain("--- E-script notes ---", blob);
    }

    [Fact]
    public void IncludesRawOcrTextAndWordGeometry()
    {
        var words = new List<OcrWord>
        {
            new() { Text = "Synthtest", X = 12, Y = 34, W = 56, H = 12 }
        };

        var blob = RxLogFormatter.BuildLogBlob(MakeSnapshot(ocrWords: words));

        Assert.Contains("Raw OCR text:", blob);
        Assert.Contains("SYNTHETIC RAW OCR TEXT", blob);
        Assert.Contains("OCR words (1), text @ (x, y, w, h):", blob);
        Assert.Contains("\"Synthtest\" @ (12, 34, 56, 12)", blob);
    }

    [Fact]
    public void HandlesNoOcrReadYet()
    {
        var blob = RxLogFormatter.BuildLogBlob(MakeSnapshot(rawOcrText: null));

        Assert.DoesNotContain("Raw OCR text:", blob);
    }

    [Fact]
    public void NullRxWindowTitleOmitsRxWindowLine()
    {
        var blob = RxLogFormatter.BuildLogBlob(MakeSnapshot(rxWindowTitle: null));

        Assert.DoesNotContain("Rx window:", blob);
    }

    /// <summary>
    /// Regression test for the reviewer-flagged blocker: OverlayViewModel.
    /// ClearCategories (window not found / screen disappeared branches of
    /// RefreshAsync/WatchAsync) used to clear Categories/Notes/_lastOcrWords
    /// but NOT OcrStatusText/LastOcrRawText, so a previously-reviewed Rx's
    /// raw OCR text/PHI would still be sitting in those bound properties —
    /// and would still show up in a "Copy logs" blob — even after the
    /// pharmacist closed that Rx and RxWindowTitle had already gone null.
    /// This models the exact before/after ClearCategories state (see
    /// OverlayViewModel.cs ClearCategories's fix: OcrStatusText reset to
    /// "OCR: not read yet." and LastOcrRawText reset to "") and asserts
    /// the "after" blob is fully scrubbed of the "before" Rx's OCR text.
    /// </summary>
    [Fact]
    public void ClearedStateBlob_ContainsNoPreviousRxOcrTextOrStatus()
    {
        const string previousRxOcrText = "SYNTHETIC RX-A RAW OCR TEXT: Jane Synthtest, Amoxicillin 500mg";
        const string previousRxOcrStatus = "OCR: 350ms (capture 90ms + ocr 260ms) · 480 chars";

        var loadedCategories = new List<RxLogCategorySnapshot>
        {
            new("Patient", "Match", new List<RxLogFieldSnapshot>
            {
                new("patientName", "Name", "Green", "Jane Synthtest", "Jane Synthtest", "", "")
            })
        };

        // "Before": Rx A is loaded and its OCR read has populated the
        // ViewModel's bound OCR properties — sanity-checks that the
        // fixture actually contains what a real "previous Rx" would.
        var beforeBlob = RxLogFormatter.BuildLogBlob(MakeSnapshot(
            categories: loadedCategories,
            rawOcrText: previousRxOcrText,
            ocrStatusText: previousRxOcrStatus,
            rxWindowTitle: "Edit Rx - 0000001 - PioneerRx"));

        Assert.Contains(previousRxOcrText, beforeBlob);
        Assert.Contains(previousRxOcrStatus, beforeBlob);

        // "After": the PioneerRx window has closed/changed — RefreshAsync's
        // "window not found" branch (or WatchAsync's "screen disappeared"
        // branch) has run ClearCategories, which now resets EVERY piece of
        // per-Rx state, not just Categories/Notes/_lastOcrWords.
        var afterBlob = RxLogFormatter.BuildLogBlob(MakeSnapshot(
            categories: Array.Empty<RxLogCategorySnapshot>(),
            notes: Array.Empty<string>(),
            ocrWords: Array.Empty<OcrWord>(),
            rawOcrText: "",
            ocrStatusText: "OCR: not read yet.",
            rxWindowTitle: null,
            greenCount: 0,
            yellowCount: 0,
            redCount: 0));

        Assert.DoesNotContain(previousRxOcrText, afterBlob);
        Assert.DoesNotContain(previousRxOcrStatus, afterBlob);
        Assert.DoesNotContain("Jane Synthtest", afterBlob);
        Assert.DoesNotContain("Rx window:", afterBlob);
    }
}
