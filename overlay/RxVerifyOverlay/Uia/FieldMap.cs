namespace RxVerifyOverlay.Uia;

/// <summary>
/// AutomationIds (ENTERED / left RxDetailsPanel) and Escript-tree
/// container/key names (SOURCE / ux10Dot6Escript), replacing the old
/// label-text + fractional-position heuristics entirely — those were
/// inferred from screenshots and never validated; this version is
/// confirmed against two REAL UIA dumps Will captured off a live
/// PioneerRx "Edit Rx" workstation (one with the Escript tab active, one
/// with the Image tab active — both showed the identical RxDetailsPanel
/// AutomationId shape, which is the whole point: the left entered panel
/// is stable regardless of which right-hand tab is open). The raw dumps
/// themselves are NOT checked into this repo (they contain a real
/// patient's PHI) — see the manager session's scratchpad for them if you
/// need to re-verify something here.
/// </summary>
public static class FieldMap
{
    /// <summary>
    /// PioneerRx window titles always start with the screen name
    /// ("Edit Rx - 6407104 - ...", confirmed in both real dumps).
    /// </summary>
    public static readonly string[] TargetWindowTitlePrefixes =
    {
        "Pre-Check Rx",
        "Edit Rx",
        "New Rx"
    };

    public const string TargetProcessName = "PioneerRx";

    // ------------------------------------------------------------------
    // ENTERED (LEFT RxDetailsPanel) — found by AutomationId ANYWHERE
    // under the window (never a fixed ancestor chain, since the
    // right-hand tab's content is lazily rendered and must not affect
    // whether these are found).
    // ------------------------------------------------------------------

    /// <summary>Text, read-only. .Name IS the value directly, e.g. "1/1/1990" — confirmed identical in both real dumps.</summary>
    public const string EnteredPatientDobId = "uxPatientDOB";

    /// <summary>Text, read-only. .Name IS the value, one free-text line (no separate city/state/zip controls), e.g. "100 Fake St Testville, KS".</summary>
    public const string EnteredPatientAddressId = "uxPatientAddress";

    /// <summary>
    /// Edit. In BOTH real dumps, .Name is the placeholder "Patient:" —
    /// neither dump captured this control with a real patient name
    /// visibly populated via .Name. The actual typed/selected value must
    /// come from ValuePattern; see
    /// UiaTreeWalker.ReadEditOrComboByAutomationId for the documented
    /// ValuePattern-then-Name fallback, and confirm on a live workstation
    /// that ValuePattern actually returns the real name here.
    /// </summary>
    public const string EnteredPatientQuickSearchId = "uxPatientQuickSearch";

    /// <summary>Edit. Same ValuePattern caveat as EnteredPatientQuickSearchId — .Name in both dumps is the placeholder "Written By:".</summary>
    public const string EnteredPrescriberQuickSearchId = "uxPrescriberQuickSearch";

    /// <summary>Text, read-only. .Name IS the NPI digits directly, e.g. "1234567890" — confirmed identical in both real dumps.</summary>
    public const string EnteredPrescriberNpiId = "uxNpi";

    /// <summary>Edit. Label placeholder "Item:" in both dumps; real drug name must come via ValuePattern.</summary>
    public const string EnteredItemQuickSearchId = "uxPrescribedItemQuickSearch";

    /// <summary>Edit. Label placeholder "Quantity:" in both dumps.</summary>
    public const string EnteredQuantityId = "uxQuantityPrescribed";

    /// <summary>ComboBox (e.g. unit "ML"/"EA"). Its inner Edit child (AutomationId "1001") had an empty Name in both dumps — read ValuePattern on the ComboBox element itself first.</summary>
    public const string EnteredQuantityUnitId = "uxQuantityPrescribedUnit";

    /// <summary>Edit. Label placeholder "Refills:" in both dumps.</summary>
    public const string EnteredRefillsId = "uxRefills";

    /// <summary>
    /// Edit. UNLIKE every other Edit field above, .Name on THIS control
    /// is confirmed (in both real dumps) to already BE the actual typed
    /// sig text — not a placeholder — e.g. "APPLY A SMALL AMOUNT TO THE
    /// AFFECTED AREA TWICE DAILY AS NEEDED." So even without ValuePattern
    /// support the Name-fallback in ReadEditOrComboByAutomationId is
    /// already correct for this one control specifically.
    /// </summary>
    public const string EnteredDirectionsId = "uxDirections";

    /// <summary>Edit. Label placeholder "Written:" in both dumps.</summary>
    public const string EnteredWrittenDateId = "uxWrittenDate";

    /// <summary>Edit. Label placeholder "Expire:" in both dumps. Not currently mapped to any PrescriptionRecord field (no expiration field exists on the model).</summary>
    public const string EnteredExpirationDateId = "uxExpirationDate";

    // ------------------------------------------------------------------
    // SOURCE (Escript tab UIA Tree, AutomationId ux10Dot6Escript) —
    // container/key names confirmed against escript-249.txt's full real
    // tree (an NCPDP SCRIPT NewRx message).
    // ------------------------------------------------------------------

    /// <summary>The Tree control itself. Search for it by AutomationId anywhere under the window — do NOT hardcode the ancestor chain (cntEscript/uxEscriptViewer/uxTabControl/EscriptEPCS/...), the Escript tab renders lazily and may not exist at all if that tab has never been opened.</summary>
    public const string EscriptTreeAutomationId = "ux10Dot6Escript";

    public const string NodeBody = "Body";
    public const string NodeNewRx = "NewRx";
    public const string NodePatient = "Patient";
    public const string NodeName = "Name";
    public const string NodeDateOfBirth = "DateOfBirth";
    public const string NodeAddress = "Address";
    public const string NodePrescriber = "Prescriber";
    public const string NodeIdentification = "Identification";
    public const string NodeMedicationPrescribed = "MedicationPrescribed";
    public const string NodeDrugCoded = "DrugCoded";
    public const string NodeProductCode = "ProductCode";
    public const string NodeQuantity = "Quantity";
    public const string NodeWrittenDate = "WrittenDate";
    public const string NodeSig = "Sig";

    public const string KeyLastName = "LastName";
    public const string KeyFirstName = "FirstName";
    public const string KeyMiddleName = "MiddleName";
    public const string KeyDate = "Date";
    public const string KeyAddressLine1 = "AddressLine1";
    public const string KeyCity = "City";
    public const string KeyStateProvince = "StateProvince";
    public const string KeyPostalCode = "PostalCode";
    public const string KeyNpi = "NPI";
    public const string KeyDrugDescription = "DrugDescription";
    public const string KeyCode = "Code";
    public const string KeyValue = "Value";
    public const string KeyQuantityUnitOfMeasure = "QuantityUnitOfMeasure";
    public const string KeyDaysSupply = "DaysSupply";
    public const string KeySigText = "SigText";

    /// <summary>
    /// The Refills leaf's raw text embeds its own colon inside a
    /// parenthetical, e.g. "Refills (NewRx: One dispense, plus
    /// (Quantity) refills): 1" — a naive split-on-FIRST-": " breaks on
    /// the "NewRx: One dispense" colon. EscriptTreeParser special-cases
    /// any leaf whose Name starts with this prefix and splits on the
    /// LAST ": " instead, which lands correctly right after the closing
    /// paren and before the integer refill count. See
    /// EscriptTreeParser.ParseRefills / SplitKeyValue.
    /// </summary>
    public const string RefillsKeyPrefix = "Refills (";
}
