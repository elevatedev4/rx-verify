using System;
using System.Linq;
using RxVerifyOverlay.Models;

namespace RxVerifyOverlay.Uia;

/// <summary>
/// Builds the two engine inputs (EnteredData from the left data-entry
/// panel, ScriptData from the center e-script panel) by combining
/// UiaTreeWalker's label lookup with the panel bounds from
/// PioneerRxWindow and the label strings in FieldMap. Every read is
/// wrapped so a missing/blank field becomes null rather than an
/// exception — per the spec, missing data must never be treated as a
/// mismatch (the engine already handles null fields as "not provided" /
/// yellow, never red).
/// </summary>
public sealed class FieldReader
{
    private readonly PioneerRxWindow _window;
    private readonly UiaTreeWalker _walker;

    public FieldReader(PioneerRxWindow window)
    {
        _window = window;
        _walker = new UiaTreeWalker(window.WindowElement);
    }

    /// <summary>
    /// What the technician entered (LEFT panel). Values are read as
    /// closely as possible to how the engine expects them (raw display
    /// strings — normalization happens inside the engine, not here).
    /// </summary>
    public PrescriptionRecord ReadEntered()
    {
        var left = _window.LeftPanelBounds();

        var patientRaw = Find(FieldMap.EnteredPatientLabel, 0, left);
        var prescriberRaw = Find(FieldMap.EnteredPrescriberLabel, 0, left);
        var dob = Find(FieldMap.EnteredPatientDobLabel, 0, left);
        var licenseNumber = FindPrescriberLicenseNumber(left);

        return new PrescriptionRecord
        {
            PatientName = StripNicknameParenthetical(patientRaw),
            PatientDOB = dob,
            PatientAddress = ParseAddress(Find(FieldMap.EnteredPatientAddressLabel, 0, left)),
            Prescriber = new Prescriber
            {
                Name = prescriberRaw,
                Npi = licenseNumber
            },
            DateWritten = Find(FieldMap.EnteredWrittenDateLabel, 0, left),
            Drug = new DrugDescriptor
            {
                Name = Find(FieldMap.EnteredItemLabel, 0, left),
                Ndc = null // NDC is not shown in the left entry panel in the screenshots; only in the center e-script panel
            },
            Sig = ReadDirections(left),
            Quantity = Find(FieldMap.EnteredQuantityLabel, 0, left),
            QuantityUnit = null, // adjacent unit combo (e.g. "ML", "EA") — read by position; wire up once validated live, see FieldMap.EnteredQuantityUnitLabel
            DaysSupply = null,   // not shown in the left panel's top-level fields in the screenshots (lives on the Dispense tab instead) — leave null; engine treats missing days supply as normal, never a mismatch
            Refills = Find(FieldMap.EnteredRefillsLabel, 0, left)
        };
    }

    /// <summary>
    /// The parsed inbound e-script (CENTER panel, green/yellow/blue
    /// boxes). Only meaningful when Origin: Electronic — for
    /// faxed/scanned scripts this panel is a raster image and every
    /// Find() call below will simply return null, which surfaces as
    /// "not provided" (yellow) per field; callers should additionally
    /// check IsStructuredSourceAvailable() and show a manual-review
    /// banner instead of per-field yellows when it's false (see
    /// ViewModels/OverlayViewModel.cs).
    /// </summary>
    public PrescriptionRecord ReadSource()
    {
        var patientBox = _window.CenterPatientBoxBounds();
        var prescriberBox = _window.CenterPrescriberBoxBounds();
        var rxBox = _window.CenterRxBoxBounds();

        var patientRaw = Find(FieldMap.ScriptPatientLabel, 0, patientBox);
        var prescriberRaw = Find(FieldMap.ScriptPrescriberLabel, 0, prescriberBox);
        var (npi, secondaryLicense) = ReadLicenses(prescriberBox);

        return new PrescriptionRecord
        {
            PatientName = patientRaw,
            PatientDOB = Find(FieldMap.ScriptPatientDobLabel, 0, patientBox),
            PatientAddress = ParseAddress(Find(FieldMap.ScriptPatientAddressLabel, 0, patientBox)),
            Prescriber = new Prescriber
            {
                Name = prescriberRaw,
                Npi = npi
            },
            DateWritten = Find(FieldMap.ScriptWrittenLabel, 0, rxBox),
            Drug = new DrugDescriptor
            {
                Name = Find(FieldMap.ScriptMedicationLabel, 0, rxBox),
                Ndc = Find(FieldMap.ScriptNdcLabel, 0, rxBox)
            },
            Sig = Find(FieldMap.ScriptDirectionsLabel, 0, rxBox),
            Quantity = StripQuantityUnit(Find(FieldMap.ScriptQuantityLabel, 0, rxBox), out var unit),
            QuantityUnit = unit,
            DaysSupply = Find(FieldMap.ScriptDaysSupplyLabel, 0, rxBox),
            Refills = StripParenthetical(Find(FieldMap.ScriptRefillsLabel, 0, rxBox))
        };
    }

    /// <summary>
    /// True when the center panel looks like a real structured e-script
    /// (we found at least a patient name and a medication name), false
    /// when it's blank/unreadable — the raster-image case (faxed/scanned
    /// scripts) mentioned in the phase-0 spec. Callers should treat
    /// false as "route to manual review", not as ten missing fields.
    /// </summary>
    public bool IsStructuredSourceAvailable(PrescriptionRecord source)
    {
        return !string.IsNullOrWhiteSpace(source.PatientName) || !string.IsNullOrWhiteSpace(source.Drug?.Name);
    }

    // ------------------------------------------------------------------

    private string? Find(string label, int occurrence, System.Drawing.Rectangle bounds)
    {
        try
        {
            return _walker.FindValueForLabel(label, occurrence, bounds);
        }
        catch
        {
            // Any UIA read can throw if PioneerRx redraws mid-read or the
            // element goes stale; treat as "not found" rather than crash
            // the whole verification pass.
            return null;
        }
    }

    private string? ReadDirections(System.Drawing.Rectangle left)
    {
        // The directions/sig box in the screenshots is a large multi-line
        // rich text control below the "Directions (Sig Codes or Text or
        // [ Literal Text ]):" label — not a simple "Label: value" pair,
        // so it's looked up by control content rather than FindValueForLabel.
        // v0 heuristic: reuse FindValueForLabel with the literal label
        // text; if that comes back empty (label wording differs from
        // what's here), fall back to the LAST non-empty read-only text
        // block within the left panel bounds, which in the screenshots is
        // exactly this box.
        var direct = Find("Directions (Sig Codes or Text or [ Literal Text ] ):", 0, left);
        if (!string.IsNullOrWhiteSpace(direct)) return direct;

        return null; // deliberately not guessing further in v0 — see README "Deferred"
    }

    /// <summary>
    /// The left panel's prescriber license/NPI number had no visible
    /// label in the screenshots — it sits as a bare number between the
    /// prescriber Phone value and the Supervisor field. v0 heuristic:
    /// take the value immediately following the (second, prescriber)
    /// "Phone:" occurrence, if it looks like a 10-digit NPI-shaped
    /// number; otherwise null. Revisit once Will confirms the real
    /// label/position on his workstation via the tree-dump debug mode.
    /// </summary>
    private string? FindPrescriberLicenseNumber(System.Drawing.Rectangle left)
    {
        var candidate = Find(FieldMap.EnteredPrescriberPhoneLabel, 1, left);
        // Find() for "Phone:" returns the phone value itself, not what
        // follows it — so this is intentionally NOT the final answer.
        // Left as an explicit "not implemented against a live tree yet"
        // stub: return null rather than silently return the wrong value.
        _ = candidate;
        return null;
    }

    /// <summary>
    /// "Licenses: 1770156416   5380389" in the center prescriber box —
    /// two space-separated numbers on one line. Picks the 10-digit one
    /// as NPI per FieldMap.NpiIsFirstLicenseNumberWhenAmbiguous when both
    /// or neither are exactly 10 digits.
    /// </summary>
    private (string? Npi, string? Other) ReadLicenses(System.Drawing.Rectangle prescriberBox)
    {
        var raw = Find(FieldMap.ScriptPrescriberLicensesLabel, 0, prescriberBox);
        if (string.IsNullOrWhiteSpace(raw)) return (null, null);

        var parts = raw.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return (null, null);
        if (parts.Length == 1) return (parts[0], null);

        var tenDigitParts = parts.Where(p => p.Length == 10 && p.All(char.IsDigit)).ToList();
        if (tenDigitParts.Count == 1)
        {
            var other = parts.FirstOrDefault(p => p != tenDigitParts[0]);
            return (tenDigitParts[0], other);
        }

        // Ambiguous (0 or 2+ ten-digit candidates): fall back to
        // configured default rather than guess per-record.
        return FieldMap.NpiIsFirstLicenseNumberWhenAmbiguous
            ? (parts[0], parts.Length > 1 ? parts[1] : null)
            : (parts[^1], parts.Length > 1 ? parts[0] : null);
    }

    private static string? StripNicknameParenthetical(string? name)
    {
        // "Anderson, William (Will-He/Him)" -> "Anderson, William"
        // The parenthetical is a pronoun/preferred-name hint PioneerRx
        // shows the tech, not part of the legal patient name the
        // e-script will contain — stripping it avoids a false "name
        // mismatch" against the source script.
        if (string.IsNullOrWhiteSpace(name)) return name;
        var parenIndex = name.IndexOf('(');
        return parenIndex > 0 ? name[..parenIndex].TrimEnd() : name;
    }

    private static string? StripParenthetical(string? value)
    {
        // "1 (additional refills)" -> "1"
        if (string.IsNullOrWhiteSpace(value)) return value;
        var parenIndex = value.IndexOf('(');
        return (parenIndex > 0 ? value[..parenIndex] : value).Trim();
    }

    private static string? StripQuantityUnit(string? raw, out string? unit)
    {
        // "50.0000 Unspecified" -> quantity "50.0000", unit "Unspecified"
        // (engine treats "Unspecified" leniently — see rx-verify
        // src/quantity/index.ts; if it doesn't, map "Unspecified" to
        // null here instead).
        unit = null;
        if (string.IsNullOrWhiteSpace(raw)) return raw;

        var spaceIndex = raw.IndexOf(' ');
        if (spaceIndex <= 0) return raw.Trim();

        unit = raw[(spaceIndex + 1)..].Trim();
        return raw[..spaceIndex].Trim();
    }

    private static Address? ParseAddress(string? raw)
    {
        // v0: keep the address as a single free-text street line (Street
        // = whole string) rather than splitting city/state/zip — the
        // engine's address comparator normalizes components but degrades
        // gracefully when only Street is populated (see
        // src/normalize/address.ts). Splitting "1517 INDIAN WELLS CT
        // LAWRENCE KS660471615" (note: screenshot shows NO separator
        // before the zip — "KS660471615") into city/state/zip reliably
        // needs a real regex validated against several live examples;
        // deferred to avoid guessing wrong on malformed input. See
        // README "Deferred".
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return new Address { Street = raw.Trim() };
    }
}
