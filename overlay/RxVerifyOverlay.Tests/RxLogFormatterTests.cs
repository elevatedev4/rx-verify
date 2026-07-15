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

    // --- "Copy logs (no HIPAA)" (redactPatient: true) --------------------
    // All values below are synthetic — no real patient/prescriber data.
    // The synthetic patient surname ("Testerson") is deliberately reused in
    // a prescriber-context OCR word/text below to exercise the "shared
    // token" over-redaction case (test E).

    [Fact]
    public void RedactPatient_TitlePatientPortionIsRedactedButRxNumberAndDrugSurvive()
    {
        var categories = new List<RxLogCategorySnapshot>
        {
            new("Rx", "Verify", new List<RxLogFieldSnapshot>
            {
                new("drug", "Drug", "Green", "SYNTHETIC DRUG SOURCE", "Synthderm 2% Cream", "exact_match", "")
            })
        };

        var blob = RxLogFormatter.BuildLogBlob(
            MakeSnapshot(
                categories: categories,
                rxWindowTitle: "Edit Rx - 9999999 - Synthderm 2% Cream - Jane (She/Her) Testerson - F - DOB: 4/5/1990 - Phone: (555) 010-2000"),
            redactPatient: true);

        Assert.Contains("Rx window: Edit Rx - 9999999 - Synthderm 2% Cream - [patient redacted]", blob);
        Assert.DoesNotContain("Testerson", blob);
        Assert.DoesNotContain("4/5/1990", blob);
        Assert.DoesNotContain("010-2000", blob);
    }

    [Fact]
    public void RedactPatient_PatientFieldSourceAndEnteredAreRedactedButFieldNameStatusAndReasonSurvive()
    {
        var categories = new List<RxLogCategorySnapshot>
        {
            new("Patient", "Match", new List<RxLogFieldSnapshot>
            {
                new("patientName", "Name", "Green", "Testerson, Jane", "Testerson, Jane", "exact_match", "Name matches exactly."),
                new("patientDOB", "DOB", "Green", "04/05/1990", "4/5/1990", "exact_match", "Dates match exactly after normalization."),
                new("patientAddress", "Address", "Yellow", "100 SYNTH LN SPRINGFIELD IL620991234", "100 Synth Ln Springfield, IL", "address_differs", "Address differs.")
            })
        };

        var blob = RxLogFormatter.BuildLogBlob(MakeSnapshot(categories: categories), redactPatient: true);

        Assert.Contains("Name (patientName): Green", blob);
        Assert.Contains("DOB (patientDOB): Green", blob);
        Assert.Contains("Address (patientAddress): Yellow", blob);
        Assert.Contains("reason=[exact_match] Name matches exactly.", blob);
        Assert.Contains("reason=[address_differs] Address differs.", blob);
        Assert.DoesNotContain("Testerson", blob);
        Assert.DoesNotContain("04/05/1990", blob);
        Assert.DoesNotContain("4/5/1990", blob);
        Assert.DoesNotContain("SYNTH LN", blob);
        Assert.DoesNotContain("Synth Ln", blob);
    }

    [Fact]
    public void RedactPatient_ScrubsPatientTokensFromRawOcrTextAndWords()
    {
        var categories = new List<RxLogCategorySnapshot>
        {
            new("Patient", "Match", new List<RxLogFieldSnapshot>
            {
                new("patientName", "Name", "Green", "Testerson, Jane", "Testerson, Jane", "", ""),
                new("patientAddress", "Address", "Green", "100 SYNTH LN SPRINGFIELD IL620991234", "100 Synth Ln Springfield, IL", "", "")
            })
        };
        var words = new List<OcrWord>
        {
            new() { Text = "Testerson,", X = 1, Y = 2, W = 3, H = 4 },
            new() { Text = "Jane", X = 5, Y = 6, W = 7, H = 8 },
            new() { Text = "IL620991234", X = 9, Y = 10, W = 11, H = 12 }
        };

        var blob = RxLogFormatter.BuildLogBlob(
            MakeSnapshot(
                categories: categories,
                ocrWords: words,
                rawOcrText: "Prescription for Testerson, Jane at 100 SYNTH LN SPRINGFIELD IL620991234 today"),
            redactPatient: true);

        Assert.DoesNotContain("Testerson", blob);
        Assert.DoesNotContain("SYNTH LN", blob);
        Assert.DoesNotContain("IL620991234", blob);
        Assert.Contains("Prescription for", blob);
        Assert.Contains("today", blob);
    }

    [Fact]
    public void RedactPatient_PreservesPrescriberDrugSigQuantityRefillsDatesAndOcrGeometry()
    {
        var categories = new List<RxLogCategorySnapshot>
        {
            new("Patient", "Match", new List<RxLogFieldSnapshot>
            {
                new("patientName", "Name", "Green", "Testerson, Jane", "Testerson, Jane", "", "")
            }),
            new("Prescriber", "Match", new List<RxLogFieldSnapshot>
            {
                new("prescriberName", "Name", "Green", "Sample, Priya", "Sample, Priya, MD", "exact_match", ""),
                new("prescriberNpi", "NPI", "Green", "1122334455", "1122334455", "exact_match", "")
            }),
            new("Rx", "Verify", new List<RxLogFieldSnapshot>
            {
                new("drug", "Drug", "Green", "SYNTHDERM 2 % CREAM", "Synthderm 2% Cream", "exact_match", ""),
                new("dateWritten", "Date Written", "Green", "05/05/2026", "5/5/2026", "exact_match", ""),
                new("quantity", "Quantity", "Green", "30", "30", "exact_match", ""),
                new("refills", "Refills", "Green", "2", "2", "exact_match", "")
            }),
            new("Sig", "Match", new List<RxLogFieldSnapshot>
            {
                new("sig", "Sig / Directions", "Green", "Apply twice daily", "Apply twice daily", "exact_match", "")
            })
        };
        var words = new List<OcrWord>
        {
            new() { Text = "Sample,", X = 100, Y = 200, W = 30, H = 12 },
            new() { Text = "Priya", X = 140, Y = 200, W = 30, H = 12 }
        };

        var blob = RxLogFormatter.BuildLogBlob(
            MakeSnapshot(
                categories: categories,
                ocrWords: words,
                rawOcrText: "Prescriber Sample, Priya Drug SYNTHDERM 2 % CREAM Qty 30 Refills 2 Written 05/05/2026"),
            redactPatient: true);

        Assert.Contains("Sample, Priya", blob);
        Assert.Contains("1122334455", blob);
        Assert.Contains("SYNTHDERM 2 % CREAM", blob);
        Assert.Contains("Synthderm 2% Cream", blob);
        Assert.Contains("Apply twice daily", blob);
        Assert.Contains("30", blob);
        Assert.Contains("05/05/2026", blob);
        Assert.Contains("5/5/2026", blob);
        Assert.Contains("\"Sample,\" @ (100, 200, 30, 12)", blob);
        Assert.Contains("\"Priya\" @ (140, 200, 30, 12)", blob);
    }

    [Fact]
    public void RedactPatient_TokenSharedWithPrescriberContextIsScrubbedEverywhere()
    {
        // "Testerson" is the patient's surname AND happens to also be the
        // supervising prescriber's surname in this synthetic scenario —
        // over-redaction is preferred, so it must be scrubbed everywhere,
        // even where it appears in prescriber-only free text.
        var categories = new List<RxLogCategorySnapshot>
        {
            new("Patient", "Match", new List<RxLogFieldSnapshot>
            {
                new("patientName", "Name", "Green", "Testerson, Jane", "Testerson, Jane", "", "")
            })
        };

        var blob = RxLogFormatter.BuildLogBlob(
            MakeSnapshot(
                categories: categories,
                rawOcrText: "Supervising: Dr. Kyle Testerson, MD approved this refill"),
            redactPatient: true);

        Assert.DoesNotContain("Testerson", blob);
        Assert.Contains("Supervising: Dr. Kyle", blob);
        Assert.Contains("MD approved this refill", blob);
    }
}
