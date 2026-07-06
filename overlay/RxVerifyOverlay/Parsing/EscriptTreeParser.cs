using System;
using System.Globalization;
using System.Linq;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.Uia;

namespace RxVerifyOverlay.Parsing;

/// <summary>
/// Pure function: EscriptNode tree (see EscriptNode.cs) -> PrescriptionRecord.
/// No FlaUI/UIA dependency, so this is unit-testable with synthetic
/// in-memory trees. Mirrors the real NCPDP SCRIPT NewRx shape confirmed
/// against a live PioneerRx Escript-tab UIA dump (see Uia/FieldMap.cs
/// header for the source note) — Message > Body > NewRx >
/// {Patient, Prescriber, MedicationPrescribed, ...}.
///
/// Every lookup is defensive: a missing container or leaf simply yields a
/// null field, never an exception — the engine treats "not provided" as
/// "not comparable" (yellow), never a mismatch (red). See Uia/FieldReader.cs.
/// </summary>
public static class EscriptTreeParser
{
    /// <param name="message">
    /// The top-level "Message" node (the Escript Tree's single top-level
    /// TreeItem in the real dump). Passing anything else (e.g. a node
    /// that isn't a NewRx message — a renewal response, cancel, etc.)
    /// simply yields an empty PrescriptionRecord, since no "NewRx"
    /// container will be found under Body.
    /// </param>
    public static PrescriptionRecord Parse(EscriptNode message)
    {
        var body = Child(message, FieldMap.NodeBody);
        var newRx = body is null ? null : Child(body, FieldMap.NodeNewRx);
        if (newRx is null)
        {
            return new PrescriptionRecord();
        }

        return new PrescriptionRecord
        {
            PatientName = ParsePatientName(newRx),
            PatientDOB = ParsePatientDob(newRx),
            PatientAddress = ParsePatientAddress(newRx),
            Prescriber = ParsePrescriber(newRx),
            DateWritten = ParseWrittenDate(newRx),
            Drug = ParseDrug(newRx),
            Sig = ParseSig(newRx),
            Quantity = ParseQuantityValue(newRx),
            QuantityUnit = ParseQuantityUnit(newRx),
            // DaysSupply removed entirely per Will's live-test feedback —
            // no longer read, compared, or displayed.
            Refills = ParseRefills(newRx)
        };
    }

    // ------------------------------------------------------------------
    // NewRx > Patient
    // ------------------------------------------------------------------

    private static string? ParsePatientName(EscriptNode newRx)
    {
        var patient = Child(newRx, FieldMap.NodePatient);
        var name = patient is null ? null : Child(patient, FieldMap.NodeName);
        if (name is null) return null;

        return JoinNameLastFirstMiddle(
            Leaf(name, FieldMap.KeyFirstName),
            Leaf(name, FieldMap.KeyMiddleName),
            Leaf(name, FieldMap.KeyLastName));
    }

    private static string? ParsePatientDob(EscriptNode newRx)
    {
        var patient = Child(newRx, FieldMap.NodePatient);
        var dob = patient is null ? null : Child(patient, FieldMap.NodeDateOfBirth);
        // DateOfBirth is a CONTAINER with a single nested "Date: <value>"
        // leaf one level down — NOT a direct leaf on Patient itself.
        var raw = dob is null ? null : Leaf(dob, FieldMap.KeyDate);
        // Reformat from the e-script's ISO "yyyy-MM-dd" to the same
        // M/d/yyyy style PioneerRx's uxPatientDOB control displays (see
        // Uia/FieldMap.cs EnteredPatientDobId), so the source/entered
        // columns line up visually instead of looking like a mismatch at
        // a glance. The engine compares dates format-agnostically either
        // way (see rx-verify src/normalize/date.ts) — this is display
        // formatting only.
        return FormatIsoDateForDisplay(raw);
    }

    private static Address? ParsePatientAddress(EscriptNode newRx)
    {
        var patient = Child(newRx, FieldMap.NodePatient);
        var address = patient is null ? null : Child(patient, FieldMap.NodeAddress);
        return ParseAddressContainer(address);
    }

    // ------------------------------------------------------------------
    // NewRx > Prescriber
    // ------------------------------------------------------------------

    private static Prescriber? ParsePrescriber(EscriptNode newRx)
    {
        var prescriber = Child(newRx, FieldMap.NodePrescriber);
        if (prescriber is null) return null;

        // NPI is nested under Prescriber > Identification > NPI, NOT a
        // direct leaf on Prescriber.
        var identification = Child(prescriber, FieldMap.NodeIdentification);
        var npi = identification is null ? null : Leaf(identification, FieldMap.KeyNpi);

        var name = Child(prescriber, FieldMap.NodeName);
        var prescriberName = name is null
            ? null
            : JoinNameLastFirstMiddle(Leaf(name, FieldMap.KeyFirstName), null, Leaf(name, FieldMap.KeyLastName));

        // Phone (CommunicationNumbers > PrimaryTelephone > Number) and
        // Address (same AddressLine1/City/StateProvince/PostalCode shape
        // as Patient > Address) added per Will's live-test feedback so
        // the engine can compare all four prescriber fields separately —
        // see FieldMap.cs NodeCommunicationNumbers/NodePrimaryTelephone
        // and Models/EngineModels.cs Prescriber.
        var phone = ParsePrimaryTelephone(prescriber);
        var address = ParseAddressContainer(Child(prescriber, FieldMap.NodeAddress));

        if (npi is null && prescriberName is null && phone is null && address is null) return null;
        return new Prescriber { Name = prescriberName, Npi = npi, Phone = phone, Address = address };
    }

    /// <summary>Shared by ParsePrescriber (and any future caller) for a Prescriber/Patient/Pharmacy-shaped &lt;container&gt; &gt; Address leaf group.</summary>
    private static Address? ParseAddressContainer(EscriptNode? address)
    {
        if (address is null) return null;
        return new Address
        {
            Street = Leaf(address, FieldMap.KeyAddressLine1),
            City = Leaf(address, FieldMap.KeyCity),
            State = Leaf(address, FieldMap.KeyStateProvince),
            Zip = Leaf(address, FieldMap.KeyPostalCode)
        };
    }

    /// <summary>CommunicationNumbers &gt; PrimaryTelephone &gt; "Number: &lt;digits&gt;", confirmed against escript-249.txt's Prescriber section.</summary>
    private static string? ParsePrimaryTelephone(EscriptNode container)
    {
        var comm = Child(container, FieldMap.NodeCommunicationNumbers);
        var primary = comm is null ? null : Child(comm, FieldMap.NodePrimaryTelephone);
        return primary is null ? null : Leaf(primary, FieldMap.KeyNumber);
    }

    // ------------------------------------------------------------------
    // NewRx > MedicationPrescribed
    // ------------------------------------------------------------------

    private static DrugDescriptor? ParseDrug(EscriptNode newRx)
    {
        var med = Child(newRx, FieldMap.NodeMedicationPrescribed);
        if (med is null) return null;

        // DrugDescription is a direct leaf on MedicationPrescribed.
        var name = Leaf(med, FieldMap.KeyDrugDescription);

        // NDC is nested MedicationPrescribed > DrugCoded > ProductCode > Code.
        var drugCoded = Child(med, FieldMap.NodeDrugCoded);
        var productCode = drugCoded is null ? null : Child(drugCoded, FieldMap.NodeProductCode);
        var ndc = productCode is null ? null : Leaf(productCode, FieldMap.KeyCode);

        // Note: DrugCoded > DrugDBCode > Code is the RxCUI. There is no
        // field for it on DrugDescriptor (engine's local NDC dataset only
        // needs the NDC to resolve the drug) so it is deliberately not
        // read here — see FieldReader.cs class doc for the full drug
        // comparison discussion.
        if (name is null && ndc is null) return null;
        return new DrugDescriptor { Name = name, Ndc = ndc };
    }

    private static string? ParseSig(EscriptNode newRx)
    {
        var med = Child(newRx, FieldMap.NodeMedicationPrescribed);
        var sig = med is null ? null : Child(med, FieldMap.NodeSig);
        return sig is null ? null : Leaf(sig, FieldMap.KeySigText);
    }

    private static string? ParseQuantityValue(EscriptNode newRx)
    {
        var med = Child(newRx, FieldMap.NodeMedicationPrescribed);
        var quantity = med is null ? null : Child(med, FieldMap.NodeQuantity);
        return quantity is null ? null : Leaf(quantity, FieldMap.KeyValue);
    }

    private static string? ParseQuantityUnit(EscriptNode newRx)
    {
        var med = Child(newRx, FieldMap.NodeMedicationPrescribed);
        var quantity = med is null ? null : Child(med, FieldMap.NodeQuantity);
        var raw = quantity is null ? null : Leaf(quantity, FieldMap.KeyQuantityUnitOfMeasure);
        // Real value looks like "C38046 (Unspecified)" — surface just the
        // human-readable parenthetical ("Unspecified") as the unit.
        return ExtractParenthetical(raw);
    }

    private static string? ParseWrittenDate(EscriptNode newRx)
    {
        var med = Child(newRx, FieldMap.NodeMedicationPrescribed);
        var writtenDate = med is null ? null : Child(med, FieldMap.NodeWrittenDate);
        var raw = writtenDate is null ? null : Leaf(writtenDate, FieldMap.KeyDate);
        // Same display-alignment reformat as ParsePatientDob — the
        // entered uxWrittenDate control shows M/d/yyyy.
        return FormatIsoDateForDisplay(raw);
    }

    private static string? ParseRefills(EscriptNode newRx)
    {
        var med = Child(newRx, FieldMap.NodeMedicationPrescribed);
        if (med is null) return null;

        // The Refills leaf's raw Name is unlike every other leaf here —
        // its key text itself contains a colon inside a parenthetical,
        // e.g. "Refills (NewRx: One dispense, plus (Quantity) refills): 1".
        // Find it directly (by prefix) rather than via Leaf()'s normal
        // exact-key lookup, since Leaf()/SplitKeyValue always does the
        // FIRST-": " split — that would land right after "NewRx" here
        // and misparse this one specifically. Split on the LAST ": "
        // instead, which lands right after the closing paren and before
        // the integer refill count.
        var refillsLeaf = med.Children.FirstOrDefault(c => c.Name.StartsWith(FieldMap.RefillsKeyPrefix, StringComparison.Ordinal));
        if (refillsLeaf is null) return null;

        var value = SplitOnLastColonSpace(refillsLeaf.Name);
        return NullIfEmpty(value);
    }

    /// <summary>
    /// Returns everything AFTER the LAST occurrence of ": " in the text.
    /// Used only for the Refills leaf (see ParseRefills, and
    /// FieldMap.RefillsKeyPrefix) — every other leaf in the tree uses the
    /// general first-": "-split via SplitKeyValue/Leaf().
    /// </summary>
    private static string SplitOnLastColonSpace(string text)
    {
        var splitIndex = text.LastIndexOf(": ", StringComparison.Ordinal);
        return splitIndex < 0 ? "" : text[(splitIndex + 2)..];
    }

    // ------------------------------------------------------------------
    // Tree-walk primitives
    // ------------------------------------------------------------------

    /// <summary>Finds a direct child CONTAINER by exact name (e.g. "Patient"). Container nodes' whole Name IS the container name (no ": " in it).</summary>
    private static EscriptNode? Child(EscriptNode node, string name) =>
        node.Children.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));

    /// <summary>Finds a direct child LEAF ("Key: Value") by key, split on the FIRST ": " (values may themselves contain colons, e.g. a sig time "10:00" — splitting on the first occurrence only is always correct as long as the value's colon isn't immediately followed by a space, which holds for every leaf here except Refills, handled separately in ParseRefills).</summary>
    private static string? Leaf(EscriptNode node, string key)
    {
        foreach (var child in node.Children)
        {
            var (k, v) = SplitKeyValue(child.Name);
            if (string.Equals(k, key, StringComparison.Ordinal)) return NullIfEmpty(v);
        }
        return null;
    }

    /// <summary>
    /// Splits a leaf's raw Name into (Key, Value) on the FIRST occurrence
    /// of ": ". This is the general rule for every leaf in the tree
    /// (container names never contain ": ", and no observed value in the
    /// real dump has a colon-followed-by-space inside it except the
    /// Refills key text itself, which callers must route through
    /// ParseRefills's prefix-based lookup instead of Leaf()).
    /// </summary>
    private static (string Key, string Value) SplitKeyValue(string text)
    {
        var splitIndex = text.IndexOf(": ", StringComparison.Ordinal);
        if (splitIndex < 0) return (text, "");
        return (text[..splitIndex], text[(splitIndex + 2)..]);
    }

    /// <summary>
    /// Builds "Last, First Middle" (comma format) instead of "First
    /// Middle Last" — matches the style PioneerRx's own quick-search
    /// fields display (uxPatientQuickSearch/uxPrescriberQuickSearch,
    /// e.g. "Rivera, Jordan Alex"), so the source/entered columns look
    /// visually aligned side by side instead of looking like a mismatch
    /// at a glance. Purely a display-format choice — the engine's name
    /// comparator normalizes both orderings to the same canonical token
    /// set either way (see rx-verify src/normalize/name.ts
    /// wholeNameTokens), so this has no effect on the actual verdict.
    /// </summary>
    private static string? JoinNameLastFirstMiddle(string? first, string? middle, string? last)
    {
        var firstMiddle = string.Join(" ", new[] { first, middle }.Where(p => !string.IsNullOrWhiteSpace(p)));
        var lastTrimmed = string.IsNullOrWhiteSpace(last) ? null : last!.Trim();

        if (lastTrimmed is null) return NullIfEmpty(firstMiddle);
        if (string.IsNullOrWhiteSpace(firstMiddle)) return lastTrimmed;
        return $"{lastTrimmed}, {firstMiddle}";
    }

    /// <summary>
    /// "yyyy-MM-dd" (the e-script's ISO date format) -&gt; "M/d/yyyy" (the
    /// format PioneerRx's own DOB/Written-date controls display). Falls
    /// back to the raw string unreformatted if it isn't a recognized ISO
    /// date, rather than guessing — display-only, never affects the
    /// engine's format-agnostic date comparison.
    /// </summary>
    private static string? FormatIsoDateForDisplay(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        return DateTime.TryParseExact(raw.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? $"{parsed.Month}/{parsed.Day}/{parsed.Year}"
            : raw;
    }

    private static string? ExtractParenthetical(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var open = raw.IndexOf('(');
        var close = raw.IndexOf(')', open + 1);
        if (open >= 0 && close > open) return raw[(open + 1)..close].Trim();
        return raw.Trim();
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
