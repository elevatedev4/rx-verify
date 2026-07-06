using RxVerifyOverlay.Models;
using RxVerifyOverlay.Parsing;

namespace RxVerifyOverlay.Uia;

/// <summary>
/// Builds the two engine inputs (EnteredData from the left RxDetailsPanel,
/// ScriptData from the Escript tab's UIA Tree) using AutomationId lookups
/// (FieldMap's Entered*Id constants, via UiaTreeWalker) and a tree walk
/// (FieldMap's Node*/Key* constants, via UiaTreeWalker.BuildEscriptTree +
/// EscriptTreeParser) respectively — confirmed against two real UIA
/// dumps, replacing the old label+position/fractional-panel-bounds
/// approach entirely (see FieldMap.cs / UiaTreeWalker.cs headers).
///
/// Every read is wrapped so a missing/blank field becomes null rather
/// than an exception — per the spec, missing data must never be treated
/// as a mismatch (the engine already handles null fields as "not
/// provided" / yellow, never red).
///
/// DRUG COMPARISON (documented gap, not a silent drop): the SOURCE
/// (Escript tree) record carries both a drug NAME (DrugDescription) and
/// an NDC (DrugCoded/ProductCode/Code) straight from the e-script — see
/// EscriptTreeParser.ParseDrug. The ENTERED record's Drug.Ndc is always
/// null: uxPrescribedItemQuickSearch (the only entered drug field) only
/// exposes a typed item NAME, and neither real dump shows any other
/// entered-side control carrying an NDC. This means NDC-level mismatches
/// (e.g. right drug name entered, but a different generic/pack size than
/// what the e-script specifies) can only be caught if the engine's drug
/// comparator (rx-verify src/drug/index.ts) falls back to comparing
/// EnteredDrug.Name against SourceDrug.Name/Ndc-resolved-description when
/// EnteredDrug.Ndc is null. Confirm that fallback exists and is exercised
/// here — if the comparator instead treats a null EnteredNdc as "skip,
/// not comparable" and only ever compares Ndc-to-Ndc, this reader would
/// never surface a real drug-identity mismatch for any e-script rx, which
/// would defeat a primary purpose of the app. This was NOT changed here
/// per the brief (don't touch EngineClient's wire protocol without a
/// genuine need) but Will should verify src/drug/index.ts's actual
/// behavior before relying on this field for e-scripts.
/// </summary>
public sealed class FieldReader
{
    private readonly UiaTreeWalker _walker;
    private bool _escriptTreeFound;

    public FieldReader(PioneerRxWindow window)
    {
        _walker = new UiaTreeWalker(window.WindowElement);
    }

    /// <summary>
    /// Set by the most recent ReadSource() call: null when a structured
    /// source is available, otherwise a message explaining why (e.g. the
    /// Escript tab was never opened this session) — see
    /// IsStructuredSourceAvailable and ViewModels/OverlayViewModel.cs.
    /// </summary>
    public string? SourceUnavailableReason { get; private set; }

    /// <summary>
    /// What the technician entered (LEFT RxDetailsPanel), read by
    /// AutomationId anywhere under the window — never by label text or
    /// screen position.
    /// </summary>
    public PrescriptionRecord ReadEntered()
    {
        return new PrescriptionRecord
        {
            PatientName = StripNicknameParenthetical(ReadEditOrCombo(FieldMap.EnteredPatientQuickSearchId)),
            PatientDOB = ReadText(FieldMap.EnteredPatientDobId),
            PatientAddress = ParseAddress(ReadText(FieldMap.EnteredPatientAddressId)),
            Prescriber = new Prescriber
            {
                Name = ReadEditOrCombo(FieldMap.EnteredPrescriberQuickSearchId),
                Npi = ReadText(FieldMap.EnteredPrescriberNpiId)
            },
            DateWritten = ReadEditOrCombo(FieldMap.EnteredWrittenDateId),
            Drug = new DrugDescriptor
            {
                Name = ReadEditOrCombo(FieldMap.EnteredItemQuickSearchId),
                // No NDC is exposed anywhere in the left entered panel in
                // either real dump — see this class's doc comment above
                // ("DRUG COMPARISON").
                Ndc = null
            },
            Sig = ReadEditOrCombo(FieldMap.EnteredDirectionsId),
            Quantity = ReadEditOrCombo(FieldMap.EnteredQuantityId),
            QuantityUnit = ReadEditOrCombo(FieldMap.EnteredQuantityUnitId),
            // Not present as a top-level entered field on the Common/
            // Edit-Rx screen in either real dump (it lives on the
            // Dispense tab, not captured). Engine treats missing as "not
            // provided", never a mismatch.
            DaysSupply = null,
            Refills = ReadEditOrCombo(FieldMap.EnteredRefillsId)
        };
    }

    /// <summary>
    /// The parsed inbound e-script (Escript tab's UIA Tree,
    /// AutomationId ux10Dot6Escript). Only meaningful when that tree is
    /// actually present (the Escript tab has been opened/rendered this
    /// session) — see IsStructuredSourceAvailable.
    /// </summary>
    public PrescriptionRecord ReadSource()
    {
        var messageNode = _walker.BuildEscriptTree();
        _escriptTreeFound = messageNode is not null;

        if (messageNode is null)
        {
            SourceUnavailableReason = "Open the Escript tab to verify this e-script.";
            return new PrescriptionRecord();
        }

        var record = EscriptTreeParser.Parse(messageNode);

        SourceUnavailableReason =
            string.IsNullOrWhiteSpace(record.PatientName) && string.IsNullOrWhiteSpace(record.Drug?.Name)
                ? "Escript tab is open, but its e-script tree didn't parse a patient or drug — confirm the tree shows a NewRx message before trusting this check."
                : null;

        return record;
    }

    /// <summary>
    /// True when the Escript tree was found AND it parsed to at least a
    /// patient name and a drug name. False covers two real cases: the
    /// Escript tab was never opened (tree control absent entirely), or it
    /// was opened but shows something other than a parseable NewRx
    /// message. Either way, callers should show SourceUnavailableReason
    /// as a manual-review banner instead of per-field yellows — replaces
    /// the old (wrong) fax/image-heuristic version of this method
    /// entirely.
    /// </summary>
    public bool IsStructuredSourceAvailable(PrescriptionRecord source)
    {
        return _escriptTreeFound
            && !string.IsNullOrWhiteSpace(source.PatientName)
            && !string.IsNullOrWhiteSpace(source.Drug?.Name);
    }

    // ------------------------------------------------------------------

    private string? ReadText(string automationId)
    {
        try { return _walker.ReadTextByAutomationId(automationId); }
        catch
        {
            // Any UIA read can throw if PioneerRx redraws mid-read or the
            // element goes stale; treat as "not found" rather than crash
            // the whole verification pass.
            return null;
        }
    }

    private string? ReadEditOrCombo(string automationId)
    {
        try { return _walker.ReadEditOrComboByAutomationId(automationId); }
        catch { return null; }
    }

    /// <summary>
    /// e.g. "Testperson, Jamie (Jay/They)" -&gt; "Testperson, Jamie" —
    /// restored from the pre-rewrite FieldReader. PioneerRx's quick-search
    /// can show a pronoun/preferred-name hint in parentheses after the
    /// legal name; that hint is not part of the legal patient name the
    /// e-script will contain, so it must be stripped before comparison or
    /// it produces a false "name mismatch" against the source script on
    /// every rx for that patient. Only applied to the entered PatientName
    /// — the source (Escript tree) name is built from
    /// LastName/FirstName/MiddleName leaves directly and never carries
    /// this kind of parenthetical.
    /// </summary>
    private static string? StripNicknameParenthetical(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name;
        var parenIndex = name.IndexOf('(');
        return parenIndex > 0 ? name[..parenIndex].TrimEnd() : name;
    }

    private static Address? ParseAddress(string? raw)
    {
        // Keep the address as a single free-text street line (Street =
        // whole string) rather than splitting city/state/zip — both real
        // dumps show uxPatientAddress as one combined string (e.g. "100
        // Fake St Testville, KS") with no separate city/state/zip
        // controls in the entered panel. The engine's address comparator
        // normalizes components but degrades gracefully when only Street
        // is populated (see rx-verify src/normalize/address.ts).
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return new Address { Street = raw.Trim() };
    }
}
