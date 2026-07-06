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
    /// <summary>Prescriber's office phone. Added per Will's live-test feedback so it's its own compared/displayed field.</summary>
    public string? Phone { get; set; }
    /// <summary>Prescriber's office address. Entered side is one combined string (Street only); source is split into components — same shape as PatientAddress.</summary>
    public Address? Address { get; set; }
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
    public string? Refills { get; set; }
    // DaysSupply removed: per Will's live-test feedback, days supply is
    // no longer read, compared, or displayed anywhere — see FieldOrder
    // below and types.ts's matching removal.
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
///
/// Per Will's live-test feedback round: "prescriber" is now FOUR
/// separate fields (name/NPI/phone/address), each with its own verdict
/// and display row, instead of one bundled field — a bundled field hid
/// which specific piece actually differed. "daysSupply" is REMOVED
/// entirely (not compared, not displayed, not in this list at all).
/// </summary>
public static class FieldOrder
{
    public static readonly IReadOnlyList<string> Fields = new[]
    {
        "patientName",
        "patientDOB",
        "patientAddress",
        "prescriberName",
        "prescriberNpi",
        "prescriberPhone",
        "prescriberAddress",
        "dateWritten",
        "drug",
        "sig",
        "quantity",
        "refills"
    };

    /// <summary>
    /// Human-readable label for each field, in fixed order, for the
    /// overlay UI. These are deliberately SHORT (no "Patient"/
    /// "Prescriber" prefix) — the category header the row lives under
    /// (see FieldCategories below, and MainWindow.xaml) already says
    /// "Patient"/"Prescriber"/"Rx", so repeating it on every row read as
    /// redundant clutter in the compact table.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> DisplayNames = new Dictionary<string, string>
    {
        ["patientName"] = "Name",
        ["patientDOB"] = "DOB",
        ["patientAddress"] = "Address",
        ["prescriberName"] = "Name",
        ["prescriberNpi"] = "NPI",
        ["prescriberPhone"] = "Phone",
        ["prescriberAddress"] = "Address",
        ["dateWritten"] = "Date Written",
        ["drug"] = "Drug",
        ["sig"] = "Sig / Directions",
        ["quantity"] = "Quantity",
        ["refills"] = "Refills"
    };
}

/// <summary>
/// Groups the 12 FieldOrder.Fields into the 3 categories the overlay's
/// compact table displays (Patient / Prescriber / Rx), per Will's spec:
/// "Patient (name, DOB), Prescriber (name, NPI), Rx (drug, sig, quantity,
/// days supply, refills, written date — whatever applies)" — days supply
/// has since been removed per the live-test feedback round, and
/// prescriber split into 4 fields (name/NPI/phone/address), all mapped to
/// the same Prescriber category here.
///   - "patientAddress" isn't in Will's 2-field Patient example, but it's
///     one of the fields the engine always returns and is clearly
///     patient-identity data, not Rx or Prescriber data — it's grouped
///     under Patient here as the only sensible home for it rather than
///     silently dropped from the compact view. Flag to Will if he'd
///     rather it live elsewhere or be hidden.
/// FieldOrder.Fields happens to already list all fields for one category
/// contiguously (patientName, patientDOB, patientAddress, prescriberName,
/// prescriberNpi, prescriberPhone, prescriberAddress, dateWritten, drug,
/// sig, quantity, refills), so building each category's rows by filtering
/// FieldOrder.Fields through this map preserves the pharmacist's required
/// field order within each category.
/// </summary>
public static class FieldCategories
{
    public const string Patient = "Patient";
    public const string Prescriber = "Prescriber";
    public const string Rx = "Rx";

    /// <summary>Fixed category display order — Patient, then Prescriber, then Rx.</summary>
    public static readonly IReadOnlyList<string> Order = new[] { Patient, Prescriber, Rx };

    public static readonly IReadOnlyDictionary<string, string> CategoryByField = new Dictionary<string, string>
    {
        ["patientName"] = Patient,
        ["patientDOB"] = Patient,
        ["patientAddress"] = Patient,
        ["prescriberName"] = Prescriber,
        ["prescriberNpi"] = Prescriber,
        ["prescriberPhone"] = Prescriber,
        ["prescriberAddress"] = Prescriber,
        ["dateWritten"] = Rx,
        ["drug"] = Rx,
        ["sig"] = Rx,
        ["quantity"] = Rx,
        ["refills"] = Rx
    };
}
