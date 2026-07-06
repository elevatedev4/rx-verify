using RxVerifyOverlay.Parsing;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for EscriptTreeParser using SYNTHETIC data only (no real
/// patient/prescriber information anywhere in this file) — the tree
/// shapes below mirror what was confirmed against a real PioneerRx
/// Escript-tab UIA dump (see Uia/FieldMap.cs header), but every name,
/// NPI, NDC, address, and sig text here is invented for this test.
/// </summary>
public class EscriptTreeParserTests
{
    /// <summary>
    /// Builds a full synthetic Message > Body > NewRx > {...} tree with
    /// every field populated, matching the real nesting shape: Name
    /// (First/Middle/Last), DateOfBirth > Date (nested one level),
    /// Address, Prescriber > Identification > NPI (nested, NOT a direct
    /// leaf), MedicationPrescribed with DrugDescription as a direct
    /// leaf, DrugCoded > ProductCode > Code, Quantity > Value +
    /// QuantityUnitOfMeasure, DaysSupply as a direct leaf, WrittenDate >
    /// Date, the Refills multi-colon key, and Sig > SigText containing an
    /// embedded time (colon) in its value.
    /// </summary>
    private static EscriptNode BuildFullSyntheticMessage() =>
        EscriptNode.Container("Message",
            EscriptNode.Container("Body",
                EscriptNode.Container("NewRx",
                    EscriptNode.Container("Patient",
                        EscriptNode.Container("Name",
                            EscriptNode.Leaf("LastName", "Testperson"),
                            EscriptNode.Leaf("FirstName", "Jamie"),
                            EscriptNode.Leaf("MiddleName", "Q")),
                        EscriptNode.Container("DateOfBirth",
                            EscriptNode.Leaf("Date", "1990-01-15")),
                        EscriptNode.Container("Address",
                            EscriptNode.Leaf("AddressLine1", "100 Fake St"),
                            EscriptNode.Leaf("City", "Testville"),
                            EscriptNode.Leaf("StateProvince", "KS"),
                            EscriptNode.Leaf("PostalCode", "660000000"),
                            EscriptNode.Leaf("CountryCode", "US"))),
                    EscriptNode.Container("Prescriber",
                        EscriptNode.Container("Identification",
                            EscriptNode.Leaf("StateLicenseNumber", "1234567"),
                            EscriptNode.Leaf("NPI", "1111111111")),
                        EscriptNode.Container("Name",
                            EscriptNode.Leaf("LastName", "Doctorson"),
                            EscriptNode.Leaf("FirstName", "Pat"))),
                    EscriptNode.Container("MedicationPrescribed",
                        EscriptNode.Leaf("DrugDescription", "Fakamycin 1 % Lotion"),
                        EscriptNode.Container("DrugCoded",
                            EscriptNode.Container("ProductCode",
                                EscriptNode.Leaf("Code", "00000000001"),
                                EscriptNode.Leaf("Qualifier", "ND")),
                            EscriptNode.Container("DrugDBCode",
                                EscriptNode.Leaf("Code", "999999"),
                                EscriptNode.Leaf("Qualifier", "SCD"))),
                        EscriptNode.Container("Quantity",
                            EscriptNode.Leaf("Value", "50"),
                            EscriptNode.Leaf("CodeListQualifier", "38"),
                            EscriptNode.Leaf("QuantityUnitOfMeasure", "C38046 (Unspecified)")),
                        EscriptNode.Leaf("DaysSupply", "30"),
                        EscriptNode.Container("WrittenDate",
                            EscriptNode.Leaf("Date", "2026-01-01")),
                        EscriptNode.Leaf("Substitutions", "0 (No Product Selection Indicated)"),
                        new EscriptNode("Refills (NewRx: One dispense, plus (Quantity) refills): 2"),
                        EscriptNode.Container("Sig",
                            EscriptNode.Leaf("SigText", "Take 1 tablet at 10:00 AM daily"))))));

    [Fact]
    public void Parse_FullTree_MapsEveryField()
    {
        var record = EscriptTreeParser.Parse(BuildFullSyntheticMessage());

        Assert.Equal("Jamie Q Testperson", record.PatientName);
        Assert.Equal("1990-01-15", record.PatientDOB);
        Assert.NotNull(record.PatientAddress);
        Assert.Equal("100 Fake St", record.PatientAddress!.Street);
        Assert.Equal("Testville", record.PatientAddress.City);
        Assert.Equal("KS", record.PatientAddress.State);
        Assert.Equal("660000000", record.PatientAddress.Zip);

        Assert.NotNull(record.Prescriber);
        Assert.Equal("Pat Doctorson", record.Prescriber!.Name);
        Assert.Equal("1111111111", record.Prescriber.Npi);

        Assert.NotNull(record.Drug);
        Assert.Equal("Fakamycin 1 % Lotion", record.Drug!.Name);
        Assert.Equal("00000000001", record.Drug.Ndc);

        Assert.Equal("50", record.Quantity);
        Assert.Equal("Unspecified", record.QuantityUnit);
        Assert.Equal("30", record.DaysSupply);
        Assert.Equal("2026-01-01", record.DateWritten);
        Assert.Equal("2", record.Refills);
        Assert.Equal("Take 1 tablet at 10:00 AM daily", record.Sig);
    }

    [Fact]
    public void Parse_SigValueContainingColon_SplitsOnlyOnFirstColonSpace()
    {
        // "SigText: ..." has exactly one ": " before the value; the
        // embedded "10:00" has no space after ITS colon, so the
        // first-": "-split rule stays correct even with a colon-bearing
        // value.
        var message = EscriptNode.Container("Message",
            EscriptNode.Container("Body",
                EscriptNode.Container("NewRx",
                    EscriptNode.Container("MedicationPrescribed",
                        EscriptNode.Leaf("DrugDescription", "Placebo 10 MG Tablet"),
                        EscriptNode.Container("Sig",
                            EscriptNode.Leaf("SigText", "Apply twice daily, once at 08:00 and once at 20:00"))))));

        var record = EscriptTreeParser.Parse(message);

        Assert.Equal("Apply twice daily, once at 08:00 and once at 20:00", record.Sig);
    }

    [Fact]
    public void Parse_RefillsMultiColonKey_ExtractsOnlyTheTrailingValue()
    {
        var message = EscriptNode.Container("Message",
            EscriptNode.Container("Body",
                EscriptNode.Container("NewRx",
                    EscriptNode.Container("MedicationPrescribed",
                        EscriptNode.Leaf("DrugDescription", "Placebo 10 MG Tablet"),
                        new EscriptNode("Refills (NewRx: One dispense, plus (Quantity) refills): 5")))));

        var record = EscriptTreeParser.Parse(message);

        Assert.Equal("5", record.Refills);
    }

    [Fact]
    public void Parse_ZeroRefills_IsPreservedNotTreatedAsMissing()
    {
        var message = EscriptNode.Container("Message",
            EscriptNode.Container("Body",
                EscriptNode.Container("NewRx",
                    EscriptNode.Container("MedicationPrescribed",
                        EscriptNode.Leaf("DrugDescription", "Placebo 10 MG Tablet"),
                        new EscriptNode("Refills (NewRx: One dispense, plus (Quantity) refills): 0")))));

        var record = EscriptTreeParser.Parse(message);

        Assert.Equal("0", record.Refills);
    }

    [Fact]
    public void Parse_MissingOptionalContainers_YieldsNullFieldsNotExceptions()
    {
        // Only Patient > Name present; no DOB, no Address, no Prescriber,
        // no MedicationPrescribed at all.
        var message = EscriptNode.Container("Message",
            EscriptNode.Container("Body",
                EscriptNode.Container("NewRx",
                    EscriptNode.Container("Patient",
                        EscriptNode.Container("Name",
                            EscriptNode.Leaf("LastName", "Solo"),
                            EscriptNode.Leaf("FirstName", "Jamie"))))));

        var record = EscriptTreeParser.Parse(message);

        Assert.Equal("Jamie Solo", record.PatientName);
        Assert.Null(record.PatientDOB);
        Assert.Null(record.PatientAddress);
        Assert.Null(record.Prescriber);
        Assert.Null(record.Drug);
        Assert.Null(record.Sig);
        Assert.Null(record.Quantity);
        Assert.Null(record.QuantityUnit);
        Assert.Null(record.DaysSupply);
        Assert.Null(record.DateWritten);
        Assert.Null(record.Refills);
    }

    [Fact]
    public void Parse_NoNewRxContainer_YieldsEmptyRecord()
    {
        // e.g. a renewal response or cancel message, not a NewRx.
        var message = EscriptNode.Container("Message",
            EscriptNode.Container("Body",
                EscriptNode.Container("Header",
                    EscriptNode.Leaf("MessageID", "abc123"))));

        var record = EscriptTreeParser.Parse(message);

        Assert.Null(record.PatientName);
        Assert.Null(record.Drug);
        Assert.Null(record.Prescriber);
    }

    [Fact]
    public void Parse_NestedDateOfBirthAndWrittenDate_ReadsOneLevelDown()
    {
        // Regression guard for the specific real-dump nesting: both of
        // these are CONTAINERS with a single "Date: <value>" leaf child,
        // not direct "DateOfBirth: <value>" / "WrittenDate: <value>" leaves.
        var message = EscriptNode.Container("Message",
            EscriptNode.Container("Body",
                EscriptNode.Container("NewRx",
                    EscriptNode.Container("Patient",
                        EscriptNode.Container("DateOfBirth",
                            EscriptNode.Leaf("Date", "2000-06-15"))),
                    EscriptNode.Container("MedicationPrescribed",
                        EscriptNode.Leaf("DrugDescription", "Placebo 10 MG Tablet"),
                        EscriptNode.Container("WrittenDate",
                            EscriptNode.Leaf("Date", "2026-02-02"))))));

        var record = EscriptTreeParser.Parse(message);

        Assert.Equal("2000-06-15", record.PatientDOB);
        Assert.Equal("2026-02-02", record.DateWritten);
    }

    [Fact]
    public void Parse_PrescriberNpiNestedUnderIdentification_NotADirectLeaf()
    {
        var message = EscriptNode.Container("Message",
            EscriptNode.Container("Body",
                EscriptNode.Container("NewRx",
                    EscriptNode.Container("Prescriber",
                        EscriptNode.Container("Identification",
                            EscriptNode.Leaf("NPI", "2222222222")),
                        EscriptNode.Container("Name",
                            EscriptNode.Leaf("LastName", "Prescriber"),
                            EscriptNode.Leaf("FirstName", "Sam"))))));

        var record = EscriptTreeParser.Parse(message);

        Assert.NotNull(record.Prescriber);
        Assert.Equal("2222222222", record.Prescriber!.Npi);
        Assert.Equal("Sam Prescriber", record.Prescriber.Name);
    }
}
