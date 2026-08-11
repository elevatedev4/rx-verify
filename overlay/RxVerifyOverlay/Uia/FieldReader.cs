using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using FlaUI.Core.AutomationElements;
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
    private readonly IntPtr _windowHandle;
    private bool _escriptTreeFound;

    // ENTERED-FIELD ELEMENT CACHE (latency fix: FieldReader.ReadEntered
    // was the dominant ~2.5-3s "uia" timing bucket — every one of its
    // ~14 fields did a fresh FindFirstDescendant walk from the window
    // root on EVERY refresh, with no caching at all). Static, same
    // reasoning as the per-Rx source cache below: FieldReader is
    // reconstructed fresh every refresh, so an instance field would
    // never survive between calls. See Uia/EnteredFieldElementCache.cs
    // for the invalidation rule (keyed by window handle — PioneerRx's
    // Pre-Check window layout is static per session, so once an
    // element's found for THIS window instance it stays valid for as
    // long as that window is open; only the VALUE changes refresh to
    // refresh, never re-cached — see ResolveElement/ReadWithRetry/
    // ReadBoolWithRetry below).
    private static readonly EnteredFieldElementCache<AutomationElement> ElementCache = new();

    /// <summary>
    /// Post-review fix: forces every entered field to re-resolve from
    /// scratch on the next ReadEntered() call, for whatever window is
    /// attached next. Called from PioneerRxWindow.TryAttach's self-heal
    /// catch block when the SHARED UIA3Automation session itself gets
    /// disposed and recreated — see EnteredFieldElementCache.Invalidate's
    /// doc for why a same-handle cache hit alone isn't safe to trust
    /// across that boundary. PioneerRxWindow and FieldReader share the
    /// Uia namespace; this is the one intentional coupling between them,
    /// kept to this single narrow static call rather than threading an
    /// automation-session identifier through every cache key.
    /// </summary>
    public static void InvalidateElementCache() => ElementCache.Invalidate();

    /// <summary>Cumulative time this ReadEntered() call spent doing FRESH FindFirstDescendant walks (cache misses only) — see Diagnostics/RefreshTiming.cs UiaFindMs. Reset at the start of every ReadEntered() call.</summary>
    public long LastReadFindMs { get; private set; }

    /// <summary>Cumulative time this ReadEntered() call spent re-reading CURRENT values off elements (cached or freshly found) — see Diagnostics/RefreshTiming.cs UiaReadMs. Reset at the start of every ReadEntered() call.</summary>
    public long LastReadValueMs { get; private set; }

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
        _windowHandle = window.NativeWindowHandle;
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
        // Reset every call — these accumulate DURING this one ReadEntered
        // pass (across all ~14 field reads below) and are read by
        // OverlayViewModel right after this method returns; see
        // LastReadFindMs/LastReadValueMs doc above.
        LastReadFindMs = 0;
        LastReadValueMs = 0;

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
    /// INTEGRATED MODE (Integrated/IntegratedOverlayCoordinator.cs): each
    /// FieldOrder field's on-screen physical-pixel BoundingRectangle,
    /// keyed the same as VerdictRowViewModel.FieldKey, so the integrated
    /// boxes layer can draw a verdict outline directly over the entered
    /// control. Uses FieldMap.EnteredAutomationIdByField — the SAME
    /// AutomationId ReadEntered() reads that field's value from — and
    /// ResolveElement, so a call right after ReadEntered() in the same
    /// refresh pass is effectively free (the elements are already cached;
    /// see ElementCache above). A field is simply absent from the result
    /// (never a default/zero rect) if its element can't be found or its
    /// BoundingRectangle can't be read — callers must treat "no entry" as
    /// "don't draw a box for this field", never as (0,0,0,0).
    /// </summary>
    public IReadOnlyDictionary<string, Rectangle> ReadEnteredFieldRects()
    {
        var rects = new Dictionary<string, Rectangle>();

        foreach (var (field, automationId) in FieldMap.EnteredAutomationIdByField)
        {
            var element = ResolveElement(automationId);
            if (element is null) continue;

            try
            {
                var rect = element.BoundingRectangle;
                if (!rect.IsEmpty) rects[field] = rect;
            }
            catch
            {
                // Stale/disconnected element mid-redraw — skip this field
                // rather than crash the whole box-layer refresh, same
                // "never throw" contract as every other read in this class.
            }
        }

        return rects;
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
    // CACHED READS (latency fix — see ElementCache field doc above).
    // Each of the three public-facing methods below keeps its original
    // signature/behavior (null on any failure, never throws) but now
    // routes through ResolveElement + a retry-on-suspicion re-read
    // instead of always doing a fresh FindFirstDescendant walk. SAFETY
    // (branch brief item 4 — "NEVER serve stale entered-field VALUES"):
    // only the ELEMENT reference is ever cached; the current VALUE is
    // read fresh from it on every single call, cache hit or miss alike.
    // ------------------------------------------------------------------

    private string? ReadText(string automationId)
    {
        try { return ReadWithRetry(automationId, UiaTreeWalker.ReadTextValue); }
        catch
        {
            // Belt-and-suspenders: ReadWithRetry already guards every
            // UIA call it makes, but PioneerRx redrawing mid-read can
            // throw from unexpected places; treat as "not found" rather
            // than crash the whole verification pass, same as before.
            return null;
        }
    }

    private string? ReadEditOrCombo(string automationId)
    {
        try { return ReadWithRetry(automationId, UiaTreeWalker.ReadEditOrComboValue); }
        catch { return null; }
    }

    private bool? ReadCheckBox(string automationId)
    {
        try { return ReadBoolWithRetry(automationId, UiaTreeWalker.ReadCheckBoxValue); }
        catch { return null; }
    }

    /// <summary>
    /// Finds the element for <paramref name="automationId"/> — reusing
    /// ElementCache's reference for this window if one's already cached,
    /// otherwise doing a fresh FindFirstDescendant walk (timed into
    /// LastReadFindMs) and caching whatever it finds for next time. When
    /// _windowHandle couldn't be read at attach time (IntPtr.Zero — a
    /// documented rare edge case, see PioneerRxWindow.PickBestCandidate),
    /// caching is skipped entirely rather than risk keying it wrong —
    /// always resolves fresh in that case, same as before this change.
    /// </summary>
    private AutomationElement? ResolveElement(string automationId)
    {
        if (_windowHandle == IntPtr.Zero)
        {
            return _walker.FindDescendantByAutomationId(automationId);
        }

        if (ElementCache.TryGetElement(_windowHandle, automationId, out var cached) && cached is not null)
        {
            return cached;
        }

        var findStopwatch = Stopwatch.StartNew();
        var found = _walker.FindDescendantByAutomationId(automationId);
        LastReadFindMs += findStopwatch.ElapsedMilliseconds;

        if (found is not null)
        {
            ElementCache.SetElement(_windowHandle, automationId, found);
        }

        return found;
    }

    /// <summary>
    /// Reads a string-valued field through the cache: resolve (cached or
    /// fresh) -&gt; timed read, with the retry-on-suspicion algorithm
    /// itself delegated to Uia/RetryingFieldRead.cs (post-review fix:
    /// that orchestration — the highest-stakes new logic here per branch
    /// brief item 4 — is now a plain, independently xUnit-tested
    /// algorithm rather than only covered by a manual trace; see
    /// RxVerifyOverlay.Tests/RetryingFieldReadTests.cs). MarkNonBlank
    /// bookkeeping stays here, applied to whatever RetryingFieldRead.Read
    /// returns as FINAL — never called mid-retry.
    /// </summary>
    private string? ReadWithRetry(string automationId, Func<AutomationElement, string?> readValue)
    {
        var value = RetryingFieldRead.Read<AutomationElement, string?>(
            resolveElement: () => ResolveElement(automationId),
            readValue: element => TimedRead(element, readValue),
            hasEverReadNonBlank: _windowHandle != IntPtr.Zero && ElementCache.HasEverReadNonBlank(_windowHandle, automationId),
            isBlank: IsBlank,
            onSuspicious: () => InvalidateCachedField(automationId));

        if (!IsBlank(value) && _windowHandle != IntPtr.Zero)
        {
            ElementCache.MarkNonBlank(_windowHandle, automationId);
        }

        return value;
    }

    /// <summary>Same retry-on-suspicion shape as ReadWithRetry, for the one bool? (checkbox) field — see ReadWithRetry's doc.</summary>
    private bool? ReadBoolWithRetry(string automationId, Func<AutomationElement, bool?> readValue)
    {
        var value = RetryingFieldRead.Read<AutomationElement, bool?>(
            resolveElement: () => ResolveElement(automationId),
            readValue: element => TimedReadBool(element, readValue),
            hasEverReadNonBlank: _windowHandle != IntPtr.Zero && ElementCache.HasEverReadNonBlank(_windowHandle, automationId),
            isBlank: static v => v is null,
            onSuspicious: () => InvalidateCachedField(automationId));

        if (value is not null && _windowHandle != IntPtr.Zero)
        {
            ElementCache.MarkNonBlank(_windowHandle, automationId);
        }

        return value;
    }

    private void InvalidateCachedField(string automationId)
    {
        if (_windowHandle != IntPtr.Zero)
        {
            ElementCache.InvalidateField(_windowHandle, automationId);
        }
    }

    private RetryingFieldRead.Attempt<string?> TimedRead(AutomationElement element, Func<AutomationElement, string?> readValue)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var value = readValue(element);
            LastReadValueMs += stopwatch.ElapsedMilliseconds;
            return new RetryingFieldRead.Attempt<string?>(value, Threw: false);
        }
        catch
        {
            LastReadValueMs += stopwatch.ElapsedMilliseconds;
            return new RetryingFieldRead.Attempt<string?>(null, Threw: true);
        }
    }

    private RetryingFieldRead.Attempt<bool?> TimedReadBool(AutomationElement element, Func<AutomationElement, bool?> readValue)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var value = readValue(element);
            LastReadValueMs += stopwatch.ElapsedMilliseconds;
            return new RetryingFieldRead.Attempt<bool?>(value, Threw: false);
        }
        catch
        {
            LastReadValueMs += stopwatch.ElapsedMilliseconds;
            return new RetryingFieldRead.Attempt<bool?>(null, Threw: true);
        }
    }

    private static bool IsBlank(string? value) => string.IsNullOrWhiteSpace(value);

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
