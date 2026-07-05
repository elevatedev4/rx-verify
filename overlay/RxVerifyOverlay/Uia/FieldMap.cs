namespace RxVerifyOverlay.Uia;

/// <summary>
/// THE PART OF THIS APP MOST LIKELY TO NEED TWEAKING ON WILL'S REAL
/// WORKSTATION. Everything below was inferred from 22 Accessibility
/// Insights screenshots of one Orchards workstation (Pre-Check Rx and
/// Edit Rx screens, PioneerRx). It is a best-effort map from what we
/// could see in those screenshots to UIA labels/positions. It has never
/// run against a live PioneerRx window (this was built on macOS, no
/// Windows/UIA available). Use DebugTreeDump (see Uia/UiaTreeWalker.cs,
/// wired to the "Dump UIA Tree" button in MainWindow) to compare this
/// map against the real tree and adjust the strings/heuristics here —
/// nothing else in the app should need to change.
///
/// PANEL LAYOUT (from the screenshots, Pre-Check Rx screen):
///   LEFT   = "data-entry" panel: what the technician typed/selected.
///            This is EnteredData in engine terms.
///   CENTER = "New Prescription" e-script panel (present when
///            Origin: Electronic): the parsed inbound e-script, shown
///            in three color-coded boxes (Patient/green, Prescriber/
///            yellow, Rx/blue). This is ScriptData (source of truth)
///            in engine terms.
///
/// Only e-scripts give you a real field-by-field source to compare
/// against (Origin: Electronic). Faxed/scanned scripts render as a
/// raster image in that same center pane — there is no structured text
/// to read there; those cases should surface as "manual review" per
/// the phase-0 spec (OCR is explicitly deferred, see README "Deferred").
/// </summary>
public static class FieldMap
{
    /// <summary>
    /// PioneerRx window titles seen in the screenshots always start with
    /// the screen name. Match by prefix + require "PioneerRx" doesn't
    /// appear in the title itself (it's the ribbon tab), so we anchor on
    /// these screen-name prefixes instead. Adjust/add if Will's
    /// workstation uses different screen names (e.g. a non-English
    /// locale, or a PioneerRx version with renamed screens).
    /// </summary>
    public static readonly string[] TargetWindowTitlePrefixes =
    {
        "Pre-Check Rx",
        "Edit Rx",
        "New Rx"
    };

    /// <summary>
    /// The executable to attach to. Confirm the real process name via
    /// Task Manager on the workstation (Details tab) if attach-by-title
    /// fails — PioneerRx's process name has been reported as
    /// "PioneerRx.exe" in public documentation but was not visible in
    /// the screenshots we have.
    /// </summary>
    public const string TargetProcessName = "PioneerRx";

    // ------------------------------------------------------------------
    // LEFT panel (EnteredData) — editable fields.
    // These showed up in the screenshots as UIA `edit`/`pane` controls,
    // Focusable, each carrying its own label as the Name OR via a
    // preceding static-text label sibling (screenshot 7/8 tooltips:
    // "edit 'Written:' Focusable", "pane 'Supervisor:' Focusable" —
    // i.e. the accessibility inspector resolved the control's Name to
    // the field's on-screen label). FieldReader looks for an exact or
    // prefix match against these labels via AutomationElement.Name,
    // then falls back to LabeledBy / nearest-preceding-static-text if
    // Name doesn't carry the label directly (see Uia/UiaTreeWalker.cs
    // FindValueForLabel).
    // ------------------------------------------------------------------

    public const string EnteredPatientLabel = "Patient:";
    public const string EnteredPrescriberLabel = "Written By:";
    public const string EnteredSupervisorLabel = "Supervisor:";
    public const string EnteredWrittenDateLabel = "Written:";
    public const string EnteredItemLabel = "Item:";
    public const string EnteredQuantityLabel = "Quantity:";
    public const string? EnteredQuantityUnitLabel = null; // adjacent combo box, right of Quantity edit — read by position, see FieldReader
    public const string EnteredRefillsLabel = "Refills:";
    public const string EnteredDirectionsLabel = "Directions"; // rich text box below "Directions (Sig Codes or Text or [ Literal Text ]):"

    // Read-only text nodes in the LEFT panel (Not Focusable, value is
    // the element Name, must walk the FULL/raw tree to see them —
    // screenshot 7's window-title bar and the green Patient box show
    // these: "1517 Indian Wells Ct Lawrence, KS", "(620) 506-1330",
    // "10/3/1988"). In the LEFT panel these appear right under the
    // Patient combo and right under the Written By combo.
    public const string EnteredPatientAddressLabel = "Address:";   // first occurrence, under Patient
    public const string EnteredPatientPhoneLabel = "Phone:";       // first occurrence, under Patient address
    public const string EnteredPatientDobLabel = "DOB:";           // shown in title bar AND patient panel
    public const string EnteredPrescriberAddressLabel = "Address:"; // second occurrence, under Written By
    public const string EnteredPrescriberPhoneLabel = "Phone:";     // second occurrence, under prescriber address
    // The prescriber license/NPI number in the LEFT panel had NO visible
    // label in the screenshots (just a bare number, e.g. "1770156416",
    // sitting below the prescriber Phone line) — FieldReader treats it
    // as "the numeric text node immediately following the prescriber
    // Phone value, before Supervisor". If PioneerRx does label it (a
    // locale/version difference), prefer the label match first.

    // ------------------------------------------------------------------
    // CENTER panel (ScriptData / source e-script) — all UIA `text`,
    // read-only, read via full tree walk. Three color-coded boxes seen
    // in the screenshots; label text is followed immediately by its
    // value, either as a sibling to the right (same line) or below.
    // ------------------------------------------------------------------

    // Green box — patient.
    public const string ScriptPatientLabel = "Patient:";
    public const string ScriptPatientAddressLabel = "Address:";
    public const string ScriptPatientGenderLabel = "Gender:";   // not compared by the engine (no field for it); read for display context only
    public const string ScriptPatientPhoneLabel = "Phone:";     // not compared by the engine; display only
    public const string ScriptPatientDobLabel = "DOB:";

    // Yellow box — prescriber.
    public const string ScriptPrescriberLabel = "Prescriber:";
    public const string ScriptPrescriberLocationLabel = "Location:"; // multi-line address, wraps to 2 lines in the screenshot
    public const string ScriptPrescriberAgentNameLabel = "Agent name:"; // display only
    public const string ScriptPrescriberLicensesLabel = "Licenses:"; // TWO numbers shown side by side, e.g. "1770156416   5380389"
    public const string ScriptPrescriberPhoneLabel = "Phone:";
    public const string ScriptPrescriberSupervisorLabel = "Supervisor:"; // display only
    public const string ScriptPrescriberSpiLabel = "SPI:"; // display only

    // Blue box — Rx.
    public const string ScriptWrittenLabel = "Written:";
    public const string ScriptNdcLabel = "NDC:";
    public const string ScriptMedicationLabel = "Medication:";
    public const string ScriptQuantityLabel = "Quantity:"; // e.g. "50.0000 Unspecified" — unit is often "Unspecified" on e-scripts, engine treats missing/unspecified unit as no-unit compare
    public const string ScriptRefillsLabel = "Refills:";   // e.g. "1 (additional refills)" — FieldReader strips the parenthetical
    public const string ScriptDirectionsLabel = "Directions:";
    public const string ScriptDaysSupplyLabel = "DS:";

    /// <summary>
    /// "Licenses:" shows two numbers. Per NPI format (always exactly 10
    /// digits, no letters), we pick the 10-digit one as NPI. If both (or
    /// neither) are 10 digits, FieldReader takes the FIRST number and
    /// flags this field yellow "npi_ambiguous" rather than guessing
    /// silently — confirm the real license-vs-NPI ordering against a
    /// live prescriber record and adjust NpiIsFirstLicenseNumber below
    /// if it's reliably one or the other on Will's workstation.
    /// </summary>
    public const bool NpiIsFirstLicenseNumberWhenAmbiguous = true;
}
