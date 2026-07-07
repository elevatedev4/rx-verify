using System;
using System.Collections.Generic;
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
/// DRUG COMPARISON: the SOURCE (Escript tree) record carries both a drug
/// NAME (DrugDescription) and an NDC (DrugCoded/ProductCode/Code)
/// straight from the e-script — see EscriptTreeParser.ParseDrug. The
/// ENTERED record's Drug.Ndc is always null: uxPrescribedItemQuickSearch
/// (the only entered drug field) only exposes a typed item NAME, and
/// neither real dump shows any other entered-side control carrying an
/// NDC. This is RESOLVED as of the engine's drug-identity-by-name pass
/// (rx-verify src/drug/index.ts compareDrugs): a normalized exact match
/// on drug NAME/description is now the PRIMARY comparison and returns
/// GREEN regardless of whether either side's NDC is present or whether
/// the NDCs agree — NDC is lookup-only (behind-the-scenes ingredient/
/// strength/form resolution), never required for a green verdict. So a
/// null EnteredDrug.Ndc here is expected and does NOT prevent a real
/// drug-identity match from showing green.
/// </summary>
public sealed class FieldReader
{
    private readonly UiaTreeWalker _walker;
    private bool _escriptTreeFound;

    /// <summary>
    /// Rx number parsed from the window title ("Edit Rx - 1234567 - ..."
    /// -&gt; "1234567", confirmed in both real dumps — see FieldMap.cs
    /// TargetWindowTitlePrefixes). Null if the title doesn't match the
    /// expected "&lt;screen&gt; - &lt;rx number&gt; - ..." shape, in which
    /// case the per-Rx source cache below is simply never used (falls
    /// back to reading fresh every time, the pre-cache behavior).
    /// </summary>
    private readonly string? _rxNumber;

    // PER-RX SOURCE CACHE (see ReadSource doc comment below for why this
    // exists). Static + a lock rather than an instance field: FieldReader
    // itself is re-constructed fresh on every OverlayViewModel.RefreshAsync
    // call (see FieldReader's only call site), so an instance-level cache
    // would never survive between refreshes. A single cached slot (not a
    // dictionary of all Rx numbers ever seen) is enough — only the
    // CURRENTLY open Rx's source is ever needed, and this deliberately
    // avoids accumulating stale entries for Rx's the pharmacist closed.
    private static readonly object CacheLock = new();
    private static string? _cachedRxNumber;
    private static PrescriptionRecord? _cachedSource;
    private static IReadOnlyList<string> _cachedNotes = Array.Empty<string>();

    public FieldReader(PioneerRxWindow window)
    {
        _walker = new UiaTreeWalker(window.WindowElement);
        _rxNumber = ExtractRxNumber(SafeWindowName(window));
    }

    private static string? SafeWindowName(PioneerRxWindow window)
    {
        try { return window.WindowElement.Name; }
        catch { return null; }
    }

    private static string? ExtractRxNumber(string? windowTitle)
    {
        if (string.IsNullOrWhiteSpace(windowTitle)) return null;
        // "Edit Rx - 1234567 - Clindamycin ... " -> "1234567".
        var parts = windowTitle.Split(" - ", StringSplitOptions.None);
        return parts.Length >= 2 ? parts[1].Trim() : null;
    }

    /// <summary>
    /// Set by the most recent ReadSource() call: null when a structured
    /// source is available, otherwise a message explaining why (e.g. the
    /// Escript tab was never opened this session) — see
    /// IsStructuredSourceAvailable and ViewModels/OverlayViewModel.cs.
    /// </summary>
    public string? SourceUnavailableReason { get; private set; }

    /// <summary>
    /// Free-text notes found on the most recent ReadSource() call (item
    /// 6) — see EscriptTreeParser.ParseNotes. Empty (never null) when
    /// none were found, which is the common case (see FieldMap.NodeNote
    /// doc: UNCONFIRMED against a real dump). Set alongside the per-Rx
    /// source cache so a cache hit doesn't lose the notes that came with
    /// the cached source.
    /// </summary>
    public IReadOnlyList<string> SourceNotes { get; private set; } = Array.Empty<string>();

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
                Npi = ReadText(FieldMap.EnteredPrescriberNpiId),
                // Phone/address added per Will's live-test feedback so
                // the engine can compare them as their own fields (see
                // Models/EngineModels.cs FieldOrder) instead of only
                // ever comparing name+NPI.
                Phone = ReadText(FieldMap.EnteredPrescriberPhoneId),
                Address = ParseAddress(ReadText(FieldMap.EnteredPrescriberAddressId))
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
            // DaysSupply removed entirely per Will's live-test feedback —
            // no longer read, compared, or displayed (see
            // Models/EngineModels.cs PrescriptionRecord/FieldOrder).
            Refills = ReadEditOrCombo(FieldMap.EnteredRefillsId),
            // DAW checkbox (item 5) — confirmed AutomationId uxDawCode
            // (CheckBox, see FieldMap.EnteredDawId). Read via TogglePattern,
            // not the Edit/ComboBox Name-fallback path (see
            // UiaTreeWalker.ReadCheckBoxByAutomationId).
            Daw = ReadCheckBox(FieldMap.EnteredDawId)
        };
    }

    /// <summary>
    /// The parsed inbound e-script (Escript tab's UIA Tree,
    /// AutomationId ux10Dot6Escript). Only meaningful when that tree is
    /// actually present (the Escript tab has been opened/rendered this
    /// session) — see IsStructuredSourceAvailable.
    ///
    /// PER-RX CACHE (Will's live-test feedback: the tab switch below was
    /// visibly flickering on every Refresh/auto-refresh tick, which read
    /// as a bug). If this Rx's number (parsed from the window title, see
    /// _rxNumber) matches the last one we successfully parsed, the cached
    /// PrescriptionRecord is returned directly — NO tab switch happens at
    /// all on a cache hit. The cache is invalidated the moment the Rx
    /// number changes (a different Rx is open), so the tab switch below
    /// still happens, but at most ONCE per prescription instead of once
    /// per Refresh.
    ///
    /// FULLY ZERO tab switches is very likely NOT achievable without a
    /// different data-access path: an unselected WPF/WinForms TabItem's
    /// content is generally not present in the UIA tree at all (confirmed
    /// by the real dumps — the Image-tab-active dump has ZERO Escript
    /// content under the Tab control, not just a hidden/collapsed node),
    /// so THE FIRST read for a given Rx has no way to see the Escript
    /// tree without selecting that tab at least once. Flagging this for
    /// Will explicitly: if even one switch per Rx is unacceptable, the
    /// only way around it that we're aware of would be a different
    /// UIA/PioneerRx integration point entirely (e.g. reading the e-script
    /// message from wherever PioneerRx itself parses it, if that's ever
    /// exposed) — out of scope for this pass.
    ///
    /// INTENTIONAL TAB SWITCH (on a cache miss): the ux10Dot6Escript tree
    /// only exists in the UIA tree while the Escript tab is the selected/
    /// visible center tab (confirmed against both real dumps). So on a
    /// cache miss this method: (1) records whichever center tab is
    /// currently selected, (2) selects Escript via
    /// UiaTreeWalker.SelectCenterTabByPrefix, (3) reads the tree, then (4)
    /// ALWAYS restores the original tab in a finally block — even if the
    /// read throws — so the pharmacist's view snaps back to where it was.
    /// </summary>
    public PrescriptionRecord ReadSource()
    {
        if (_rxNumber is not null)
        {
            lock (CacheLock)
            {
                if (_cachedRxNumber == _rxNumber && _cachedSource is not null)
                {
                    _escriptTreeFound = true;
                    SourceUnavailableReason = null;
                    SourceNotes = _cachedNotes;
                    return _cachedSource;
                }
            }
        }

        string? previouslySelectedTab = null;
        bool switchedTab = false;
        try
        {
            previouslySelectedTab = _walker.SelectCenterTabByPrefix(FieldMap.EscriptTabNamePrefix, out switchedTab);

            var messageNode = _walker.BuildEscriptTree();
            _escriptTreeFound = messageNode is not null;

            if (messageNode is null)
            {
                // Covers two real cases: this Rx has no Escript tab at all
                // (not an e-script — the tab strip itself has no "Escript"
                // item, as in the Image-tab-active dump), or the tab
                // exists but we couldn't select it (see
                // SelectCenterTabByPrefix's SelectionItemPattern caveat).
                // Deliberately NOT cached — there's nothing useful to
                // reuse, and the next Refresh should try again (e.g. once
                // the pharmacist actually opens the Escript tab).
                SourceUnavailableReason = switchedTab
                    ? "Escript tab opened, but no e-script tree was found under it."
                    : "No e-script source found for this Rx — it may not be an e-script, or the Escript tab couldn't be selected.";
                return new PrescriptionRecord();
            }

            var record = EscriptTreeParser.Parse(messageNode);
            var notes = EscriptTreeParser.ParseNotes(messageNode);
            SourceNotes = notes;

            SourceUnavailableReason =
                string.IsNullOrWhiteSpace(record.PatientName) && string.IsNullOrWhiteSpace(record.Drug?.Name)
                    ? "Escript tab is open, but its e-script tree didn't parse a patient or drug — confirm the tree shows a NewRx message before trusting this check."
                    : null;

            if (_rxNumber is not null)
            {
                lock (CacheLock)
                {
                    _cachedRxNumber = _rxNumber;
                    _cachedSource = record;
                    _cachedNotes = notes;
                }
            }

            return record;
        }
        finally
        {
            // ALWAYS restore, success or exception — the pharmacist must
            // never be left looking at the Escript tab because a read
            // failed partway through.
            if (switchedTab)
            {
                _walker.RestoreCenterTabByName(previouslySelectedTab);
            }
        }
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

    private bool? ReadCheckBox(string automationId)
    {
        try { return _walker.ReadCheckBoxByAutomationId(automationId); }
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
