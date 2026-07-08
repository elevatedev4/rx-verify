using RxVerifyOverlay.Parsing;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for OcrEscriptParser (PROVISIONAL v0 heuristic parser —
/// see Parsing/OcrEscriptParser.cs's class doc) using SYNTHETIC OCR text
/// only, invented for this file — no real patient/prescriber data.
/// Covers two representative raw-OCR-text shapes: one where every label
/// carries an explicit value on the same line ("Prescriber Address:
/// ..."), and one where field values wrap onto the next line and
/// ambiguous labels ("Name:", "Address:") are disambiguated by a bare
/// section heading above them — both are plausible OCR-noise variants of
/// how an on-screen e-script layout could read once run through OCR.
///
/// Cannot run `dotnet test` on this Mac (.NET WPF/Windows-only project) —
/// these are written to be structurally correct and are UNRUN here; the
/// owner runs them on Windows.
/// </summary>
public class OcrEscriptParserTests
{
    [Fact]
    public void Parse_CombinedLabelLayout_PopulatesAllFields()
    {
        const string ocrText = """
            Patient: Jamie Testperson
            DOB: 01/15/1990
            Patient Address: 100 Fake St, Testville, KS 66000

            Prescriber: Dr. Sam Prescriberton
            NPI: 1234567890
            Prescriber Phone: 555-123-4567
            Prescriber Address: 200 Clinic Way, Testville, KS 66000

            Date Written: 07/01/2026
            Drug: Clindamycin Phosphate 1% Lotion
            NDC: 00000-0000-00
            Sig: Apply to affected area twice daily
            Quantity: 60 ML
            Refills: 2
            DAW: Substitution Not Allowed
            """;

        var record = OcrEscriptParser.Parse(ocrText);

        Assert.Equal("Jamie Testperson", record.PatientName);
        Assert.Equal("01/15/1990", record.PatientDOB);
        Assert.Equal("100 Fake St, Testville, KS 66000", record.PatientAddress?.Street);

        Assert.NotNull(record.Prescriber);
        Assert.Equal("Dr. Sam Prescriberton", record.Prescriber!.Name);
        Assert.Equal("1234567890", record.Prescriber.Npi);
        Assert.Equal("555-123-4567", record.Prescriber.Phone);
        Assert.Equal("200 Clinic Way, Testville, KS 66000", record.Prescriber.Address?.Street);

        Assert.Equal("07/01/2026", record.DateWritten);

        Assert.NotNull(record.Drug);
        Assert.Equal("Clindamycin Phosphate 1% Lotion", record.Drug!.Name);
        Assert.Equal("00000-0000-00", record.Drug.Ndc);

        Assert.Equal("Apply to affected area twice daily", record.Sig);
        Assert.Equal("60", record.Quantity);
        Assert.Equal("ML", record.QuantityUnit);
        Assert.Equal("2", record.Refills);
        Assert.True(record.SubstitutionsNotAllowed);
    }

    [Fact]
    public void Parse_SectionHeaderLayoutWithWrappedValues_DisambiguatesNameAndAddress()
    {
        // Simulates OCR that dropped some values onto their own line
        // (common with dense on-screen text) and used bare section
        // headings instead of "Patient Address:"/"Prescriber Address:"
        // combined labels — the ambiguous "Name:"/"Address:" labels must
        // resolve against whichever section heading came before them.
        const string ocrText = """
            Patient
            Name:
            Alex Sampleton
            DOB: 03/22/1985
            Address:
            42 Synthetic Ave, Testville, KS 66001

            Prescriber
            Name:
            Dr. Robin Fakename
            NPI: 9876543210
            Phone: 555-987-6543
            Address:
            9 Clinic Plaza, Testville, KS 66001

            Drug: Amoxicillin 500mg Capsule
            Sig: Take one capsule by mouth three times daily
            Qty: 30
            Refills: 0
            Substitution: Allowed
            """;

        var record = OcrEscriptParser.Parse(ocrText);

        Assert.Equal("Alex Sampleton", record.PatientName);
        Assert.Equal("03/22/1985", record.PatientDOB);
        Assert.Equal("42 Synthetic Ave, Testville, KS 66001", record.PatientAddress?.Street);

        Assert.NotNull(record.Prescriber);
        Assert.Equal("Dr. Robin Fakename", record.Prescriber!.Name);
        Assert.Equal("9876543210", record.Prescriber.Npi);
        Assert.Equal("555-987-6543", record.Prescriber.Phone);
        Assert.Equal("9 Clinic Plaza, Testville, KS 66001", record.Prescriber.Address?.Street);

        Assert.NotNull(record.Drug);
        Assert.Equal("Amoxicillin 500mg Capsule", record.Drug!.Name);
        Assert.Null(record.Drug.Ndc);

        Assert.Equal("Take one capsule by mouth three times daily", record.Sig);
        Assert.Equal("30", record.Quantity);
        Assert.Null(record.QuantityUnit);
        Assert.Equal("0", record.Refills);
        Assert.False(record.SubstitutionsNotAllowed);
    }

    [Fact]
    public void Parse_EmptyOrWhitespaceText_ReturnsBlankRecord()
    {
        var record = OcrEscriptParser.Parse("   \n  \n ");

        Assert.Null(record.PatientName);
        Assert.Null(record.PatientAddress);
        Assert.Null(record.Prescriber);
        Assert.Null(record.Drug);
        Assert.Null(record.Sig);
    }

    [Fact]
    public void Parse_NullText_ReturnsBlankRecordWithoutThrowing()
    {
        var record = OcrEscriptParser.Parse(null);

        Assert.Null(record.PatientName);
        Assert.Null(record.Drug);
    }

    [Fact]
    public void Parse_GarbageOcrNoise_NeverThrowsAndLeavesUnrecognizedFieldsNull()
    {
        const string ocrText = """
            asdkjh 39$#@ ---
            :::::
            random unlabeled line
            Not A Real Label With No Colon
            """;

        var record = OcrEscriptParser.Parse(ocrText);

        Assert.Null(record.PatientName);
        Assert.Null(record.Drug);
        Assert.Null(record.Sig);
    }
}
