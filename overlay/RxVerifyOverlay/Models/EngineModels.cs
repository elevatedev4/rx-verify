using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RxVerifyOverlay.Models;

/// <summary>
/// C# mirror of rx-verify's src/types.ts. These are plain data classes so
/// they round-trip through System.Text.Json exactly the same shape the
/// TypeScript engine (called as a subprocess, see Engine/EngineClient.cs)
/// expects on stdin and produces on stdout. Keep this file in sync with
/// types.ts by hand — there is no codegen for v0.
/// </summary>

public sealed class Address
{
    public string? Street { get; set; }
    public string? Unit { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Zip { get; set; }
}

public sealed class Prescriber
{
    public string? Name { get; set; }
    public string? Npi { get; set; }
}

public sealed class DrugDescriptor
{
    /// <summary>Raw display name, e.g. "Clindamycin Phosp 1% Lotion".</summary>
    public string? Name { get; set; }
    /// <summary>NDC code if known (10 or 11 digit, any common format).</summary>
    public string? Ndc { get; set; }
}

/// <summary>
/// One side of the comparison (source e-script or technician-entered
/// data). Matches PrescriptionRecord in types.ts. All fields optional —
/// the UIA reader is defensive about missing/blank fields (see
/// Uia/FieldReader.cs) and the engine treats "not provided" as yellow,
/// never a mismatch.
/// </summary>
public sealed class PrescriptionRecord
{
    public string? PatientName { get; set; }
    public string? PatientDOB { get; set; }
    public Address? PatientAddress { get; set; }
    public Prescriber? Prescriber { get; set; }
    public string? DateWritten { get; set; }
    public DrugDescriptor? Drug { get; set; }
    public string? Sig { get; set; }

    /// <summary>
    /// String or number in the TS engine (quantity is sometimes typed as
    /// "60" and sometimes 60). We always send it as a string from the
    /// overlay — the engine's normalize logic parses either.
    /// </summary>
    public string? Quantity { get; set; }
    public string? QuantityUnit { get; set; }
    public string? DaysSupply { get; set; }
    public string? Refills { get; set; }
}

/// <summary>Request body sent to verify-cli on stdin: { source, entered }.</summary>
public sealed class VerifyCliRequest
{
    public PrescriptionRecord Source { get; set; } = new();
    public PrescriptionRecord Entered { get; set; } = new();
}

public enum VerdictStatus
{
    Green,
    Yellow,
    Red
}

public sealed class FieldVerdict
{
    public string Field { get; set; } = "";

    // Enum naming (camelCase "green"/"yellow"/"red") is configured on the
    // JsonSerializerOptions in Engine/EngineClient.cs, not per-property,
    // so both this converter and all the PascalCase-vs-camelCase property
    // mapping above stay in one place.
    public VerdictStatus Status { get; set; }

    public string ReasonCode { get; set; } = "";
    public string Explanation { get; set; } = "";
    public string? SourceValue { get; set; }
    public string? EnteredValue { get; set; }
}

public sealed class VerifySummary
{
    public int Green { get; set; }
    public int Yellow { get; set; }
    public int Red { get; set; }
    public int Total { get; set; }
}

/// <summary>
/// Response from verify-cli on stdout: either a VerifyResult, or (on a
/// bad-input / crash) an { "error": "..." } object — see EngineClient
/// for how the two are distinguished.
/// </summary>
public sealed class VerifyResult
{
    public List<FieldVerdict> Verdicts { get; set; } = new();
    public VerifySummary Summary { get; set; } = new();
    public string? Error { get; set; }
}

/// <summary>
/// FIXED FIELD ORDER — hard requirement from the pharmacist owner.
/// Mirrors FIELD_ORDER in types.ts exactly. The overlay renders rows in
/// THIS order always; it must never re-sort by severity. The engine
/// itself asserts this order in its own output, so this is a
/// belt-and-suspenders duplicate check on the C# side (see
/// ViewModels/OverlayViewModel.cs).
/// </summary>
public static class FieldOrder
{
    public static readonly IReadOnlyList<string> Fields = new[]
    {
        "patientName",
        "patientDOB",
        "patientAddress",
        "prescriber",
        "dateWritten",
        "drug",
        "sig",
        "quantity",
        "daysSupply",
        "refills"
    };

    /// <summary>Human-readable label for each field, in fixed order, for the overlay UI.</summary>
    public static readonly IReadOnlyDictionary<string, string> DisplayNames = new Dictionary<string, string>
    {
        ["patientName"] = "Patient",
        ["patientDOB"] = "Patient DOB",
        ["patientAddress"] = "Patient Address",
        ["prescriber"] = "Prescriber",
        ["dateWritten"] = "Date Written",
        ["drug"] = "Drug",
        ["sig"] = "Sig / Directions",
        ["quantity"] = "Quantity",
        ["daysSupply"] = "Days Supply",
        ["refills"] = "Refills"
    };
}
