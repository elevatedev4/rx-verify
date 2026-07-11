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
        string? rxWindowTitle = "Edit Rx - 0000001 - PioneerRx")
    {
        return new RxLogSnapshot
        {
            CapturedAt = new DateTime(2026, 1, 2, 3, 4, 5),
            AppVersion = "1.2.3.0",
            CommitSha = "abc12345",
            Method = "OCR",
            RxWindowTitle = rxWindowTitle,
            StatusMessage = "Last checked 3:04:05 AM.",
            OcrStatusText = "OCR: 400ms (capture 100ms + ocr 300ms) · 512 chars",
            RawOcrText = rawOcrText,
            OcrWords = ocrWords ?? Array.Empty<OcrWord>(),
            Categories = categories ?? Array.Empty<RxLogCategorySnapshot>(),
            Notes = notes ?? Array.Empty<string>(),
            GreenCount = 10,
            YellowCount = 2,
            RedCount = 1
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
}
