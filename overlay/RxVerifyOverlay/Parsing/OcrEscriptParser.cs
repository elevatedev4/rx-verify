using System;
using System.Collections.Generic;
using System.Linq;
using RxVerifyOverlay.Models;

namespace RxVerifyOverlay.Parsing;

/// <summary>
/// PROVISIONAL — WILL BE REWRITTEN FROM REAL OCR DUMPS. This is a
/// first-pass, best-effort label/line heuristic over raw OCR text
/// captured from the on-screen e-script image (see
/// Ocr/EscriptImageCapture.cs and Uia/OcrFieldReader.cs). VerifyOCR v0 is
/// explicitly diagnostic-first: prove capture+OCR speed and text quality
/// (Ocr/OcrLogger.cs) BEFORE investing in a tuned parser. Once Will has a
/// handful of real ocr-*.log dumps off his workstation, this should be
/// rebuilt against actual OCR output shapes/noise, not the synthetic
/// strings this file's tests use.
///
/// Deliberately a PURE function of a string — no WPF/WinRT/UIA
/// references anywhere in this file — so it's unit-testable without
/// Windows (see RxVerifyOverlay.Tests/OcrEscriptParserTests.cs, synthetic
/// e-script text only, no real patient data).
///
/// HEURISTIC SHAPE: scans line-by-line for "Label: value" pairs (value
/// may be on the same line after the colon, or on the very next line if
/// the colon line is bare — OCR frequently drops content onto its own
/// line). A handful of bare section headers ("Patient" / "Prescriber",
/// no colon) switch which side an ambiguous label like "Name:" or
/// "Address:" applies to, since a real e-script's on-screen layout
/// typically groups patient fields and prescriber fields under their own
/// headings. Recognizes: Patient/DOB/Address, Prescriber/NPI/Phone/
/// Address, Drug/NDC, Sig, Quantity, Refills, DAW/substitution, Date
/// Written. Free-text Notes are NOT extracted in v0 — Parse's signature
/// returns only a PrescriptionRecord (matching the engine boundary,
/// EngineClient.VerifyAsync), which has no Notes field; that's a real
/// gap vs. the UIA path's EscriptTreeParser.ParseNotes, flagged in the
/// branch report rather than silently worked around.
/// </summary>
public static class OcrEscriptParser
{
    private enum Section
    {
        None,
        Patient,
        Prescriber
    }

    public static PrescriptionRecord Parse(string? ocrText)
    {
        var record = new PrescriptionRecord();
        if (string.IsNullOrWhiteSpace(ocrText)) return record;

        var lines = ocrText
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        var prescriber = new Prescriber();
        var drug = new DrugDescriptor();
        Address? patientAddress = null;
        Address? prescriberAddress = null;
        var section = Section.None;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];

            if (TryMatchSectionHeader(line, out var newSection))
            {
                section = newSection;
                continue;
            }

            var (label, inlineValue) = SplitLabel(line);
            if (label is null) continue;

            var value = ResolveValue(inlineValue, lines, i);

            switch (label)
            {
                case "patient":
                    record.PatientName = value;
                    break;
                case "dob":
                    record.PatientDOB = value;
                    break;
                case "name":
                    // Ambiguous label — only trust it inside a known
                    // section (see class doc); outside one, a bare
                    // "Name:" is more likely OCR noise than a field this
                    // parser should guess at.
                    if (section == Section.Patient) record.PatientName = value;
                    else if (section == Section.Prescriber) prescriber.Name = value;
                    break;
                case "address":
                    if (section == Section.Prescriber) prescriberAddress = ParseAddressValue(value);
                    else patientAddress = ParseAddressValue(value); // default: patient (matches FieldReader.ParseAddress's single-street-line shape)
                    break;
                case "prescriber":
                case "written by":
                    prescriber.Name = value;
                    break;
                case "npi":
                    prescriber.Npi = value;
                    break;
                case "phone":
                case "prescriber phone":
                    prescriber.Phone = value;
                    break;
                case "prescriber address":
                    prescriberAddress = ParseAddressValue(value);
                    break;
                case "patient address":
                    patientAddress = ParseAddressValue(value);
                    break;
                case "date written":
                case "written":
                case "written date":
                    record.DateWritten = value;
                    break;
                case "drug":
                case "medication":
                case "rx":
                    drug.Name = value;
                    break;
                case "ndc":
                    drug.Ndc = value;
                    break;
                case "sig":
                case "directions":
                case "sig/directions":
                    record.Sig = value;
                    break;
                case "qty":
                case "quantity":
                    ApplyQuantity(value, record);
                    break;
                case "refills":
                    record.Refills = value;
                    break;
                case "daw":
                case "substitution":
                case "substitutions":
                    record.SubstitutionsNotAllowed = ParseSubstitution(value);
                    break;
                    // "note"/"notes" intentionally unhandled — see class doc.
            }
        }

        record.PatientAddress = patientAddress;

        prescriber.Address = prescriberAddress;
        var prescriberHasData = prescriber.Name is not null || prescriber.Npi is not null
            || prescriber.Phone is not null || prescriber.Address is not null;
        record.Prescriber = prescriberHasData ? prescriber : null;

        record.Drug = string.IsNullOrWhiteSpace(drug.Name) && string.IsNullOrWhiteSpace(drug.Ndc) ? null : drug;

        return record;
    }

    /// <summary>Bare (no-colon) heading lines like "Patient" or "Prescriber Information" that switch which side an ambiguous label applies to — see class doc.</summary>
    private static bool TryMatchSectionHeader(string line, out Section section)
    {
        section = Section.None;
        if (line.Contains(':')) return false;

        var normalized = line.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "patient":
            case "patient information":
            case "patient info":
                section = Section.Patient;
                return true;
            case "prescriber":
            case "prescriber information":
            case "prescriber info":
            case "physician":
            case "prescriber/physician":
                section = Section.Prescriber;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Splits "Label: value" on the first colon; returns (null, null) for any line with no colon or a blank label. Label is normalized (trimmed, lowercased) for the switch above.</summary>
    private static (string? label, string? inlineValue) SplitLabel(string line)
    {
        var colonIndex = line.IndexOf(':');
        if (colonIndex <= 0) return (null, null);

        var rawLabel = line[..colonIndex].Trim();
        if (rawLabel.Length == 0) return (null, null);

        var rawValue = line[(colonIndex + 1)..].Trim();
        return (rawLabel.ToLowerInvariant(), rawValue.Length == 0 ? null : rawValue);
    }

    /// <summary>If the colon line itself had a value, use it; otherwise fall back to the very next non-empty line (OCR commonly wraps a label and its value onto separate lines) — but never borrow a line that is itself another recognized label (avoids swallowing the next field's line as this field's value).</summary>
    private static string? ResolveValue(string? inlineValue, IReadOnlyList<string> lines, int labelIndex)
    {
        if (!string.IsNullOrWhiteSpace(inlineValue)) return inlineValue;

        if (labelIndex + 1 >= lines.Count) return null;

        var candidate = lines[labelIndex + 1];
        var (nextLabel, _) = SplitLabel(candidate);
        if (nextLabel is not null) return null; // next line is itself a label — nothing to borrow
        if (TryMatchSectionHeader(candidate, out _)) return null;

        return candidate;
    }

    /// <summary>Single free-text street line — mirrors Uia/FieldReader.cs ParseAddress's shape (Street = whole string, no city/state/zip split), since OCR text has no more structure to work with than the UIA entered-panel string did.</summary>
    private static Address? ParseAddressValue(string? raw)
    {
        return string.IsNullOrWhiteSpace(raw) ? null : new Address { Street = raw.Trim() };
    }

    /// <summary>"60 EA" -&gt; Quantity="60", QuantityUnit="EA"; "60" -&gt; Quantity="60" only.</summary>
    private static void ApplyQuantity(string? value, PrescriptionRecord record)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        var parts = value.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        record.Quantity = parts.Length > 0 ? parts[0].Trim() : null;
        record.QuantityUnit = parts.Length > 1 ? parts[1].Trim() : null;
    }

    /// <summary>Mirrors Models/EngineModels.cs SubstitutionsNotAllowed semantics: true for an explicit "not allowed"/DAW indicator, false for an explicit "allowed", null (not provided) for anything else/ambiguous — never guessed.</summary>
    private static bool? ParseSubstitution(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Contains("not allowed") || normalized == "1" || normalized.Contains("daw")) return true;
        if (normalized.Contains("allowed") || normalized == "0") return false;
        return null;
    }
}
