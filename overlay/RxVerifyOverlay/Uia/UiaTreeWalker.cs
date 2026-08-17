using System;
using System.Diagnostics;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using RxVerifyOverlay.Ocr;
using RxVerifyOverlay.Parsing;

namespace RxVerifyOverlay.Uia;

/// <summary>
/// Two independent jobs, both against the FULL UIA tree of a PioneerRx
/// window:
///   1. Find a single element ANYWHERE under the window by AutomationId
///      (used for the ENTERED/RxDetailsPanel fields — see FieldMap.cs) —
///      no fixed ancestor chain, no label/position guessing.
///   2. Walk the Escript tab's Tree control (ux10Dot6Escript) into the
///      UIA-free EscriptNode structure that EscriptTreeParser consumes —
///      this is the one adapter between live FlaUI/UIA and the pure,
///      unit-testable parser.
///
/// Superseded design note: earlier versions of this file paired on-screen
/// text labels with positionally-adjacent values (FindValueForLabel).
/// That was inferred from screenshots and never validated against a live
/// PioneerRx window; it has been removed in favor of the AutomationId-
/// and tree-shape-based approach below, confirmed against two real UIA
/// dumps (see FieldMap.cs header).
/// </summary>
public sealed class UiaTreeWalker
{
    private readonly AutomationElement _root;

    public UiaTreeWalker(AutomationElement root)
    {
        _root = root;
    }

    // ------------------------------------------------------------------
    // ENTERED (AutomationId lookups, anywhere under the window)
    // ------------------------------------------------------------------

    /// <summary>
    /// Reads a read-only Text element's value by AutomationId — .Name IS
    /// the value directly for these controls (confirmed for uxPatientDOB,
    /// uxPatientAddress, uxNpi in both real dumps). Returns null if the
    /// element isn't found or its Name is blank.
    /// </summary>
    public string? ReadTextByAutomationId(string automationId)
    {
        var element = FindDescendantByAutomationId(automationId);
        return element is null ? null : ReadTextValue(element);
    }

    /// <summary>
    /// The read-value half of ReadTextByAutomationId, extracted so a
    /// CACHED element (FieldReader's per-window entered-field cache —
    /// see Uia/EnteredFieldElementCache.cs, latency fix) can be re-read
    /// directly on every refresh without paying for a fresh
    /// FindDescendantByAutomationId walk each time.
    /// </summary>
    public static string? ReadTextValue(AutomationElement element)
    {
        try
        {
            var name = element.Name;
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads an Edit/ComboBox's current value by AutomationId. See
    /// ReadEditOrComboValue for the ValuePattern-then-Name fallback
    /// strategy and its documented caveats.
    /// </summary>
    public string? ReadEditOrComboByAutomationId(string automationId)
    {
        var element = FindDescendantByAutomationId(automationId);
        return element is null ? null : ReadEditOrComboValue(element);
    }

    /// <summary>
    /// Reads a CheckBox's checked state by AutomationId (e.g.
    /// FieldMap.EnteredDawId) via TogglePattern — a CheckBox's .Name is
    /// its static label text ("DAW"), never its checked state, so
    /// ReadEditOrComboByAutomationId's Name-fallback path would be wrong
    /// here; this reads ToggleState directly instead. Returns null (not
    /// "unchecked") if the element isn't found or TogglePattern isn't
    /// supported — the caller/engine treats null as "not provided"
    /// (yellow), never a false "unchecked".
    /// </summary>
    public bool? ReadCheckBoxByAutomationId(string automationId)
    {
        var element = FindDescendantByAutomationId(automationId);
        return element is null ? null : ReadCheckBoxValue(element);
    }

    /// <summary>The read-value half of ReadCheckBoxByAutomationId, extracted for the same reason as ReadTextValue above — lets a cached element be re-read without a fresh find.</summary>
    public static bool? ReadCheckBoxValue(AutomationElement element)
    {
        try
        {
            if (!element.Patterns.Toggle.IsSupported) return null;
            var state = element.Patterns.Toggle.Pattern.ToggleState.ValueOrDefault;
            return state switch
            {
                ToggleState.On => true,
                ToggleState.Off => false,
                _ => null // Indeterminate, or the provider didn't resolve a real state
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Finds the first descendant (anywhere under the window, no fixed
    /// ancestor path) with the given AutomationId. Returns null rather
    /// than throwing if it isn't present — the corresponding tab/panel
    /// may not be rendered yet, or PioneerRx may be mid-redraw.
    /// </summary>
    public AutomationElement? FindDescendantByAutomationId(string automationId)
    {
        try
        {
            return _root.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Finds the first descendant (anywhere under the window, no fixed
    /// ancestor path) of ControlType.TabItem whose Name starts with
    /// <paramref name="namePrefix"/> — used by Uia/CommonTabGate.cs to
    /// locate the OUTER Common TabItem (FieldMap.OuterCommonTabNamePrefix),
    /// which has no confirmed containing Tab control AutomationId to
    /// narrow the search by first (unlike SelectCenterTabByPrefix above,
    /// which already knows FieldMap.CenterTabControlAutomationId). Returns
    /// null if no TabItem matches, or if the search itself throws (window
    /// mid-redraw, or this Pioneer version's tree shape differs) — never
    /// throws.
    ///
    /// SHOULD-FIX (review): more than one TabItem matching the same
    /// prefix is unexpected — Pioneer's outer tab names are assumed
    /// unique — so it's logged via OcrLogger.LogTiming (the existing
    /// non-PHI diagnostic-log-line idiom; this message carries no
    /// patient/prescriber/drug content, just a match count and prefix)
    /// rather than silently resolved. Still deterministic either way:
    /// FindAllDescendants' document order means the FIRST match is always
    /// taken, logged or not.
    /// </summary>
    public AutomationElement? FindDescendantTabItemByNamePrefix(string namePrefix)
    {
        try
        {
            var tabItems = _root.FindAllDescendants(cf => cf.ByControlType(ControlType.TabItem));
            AutomationElement? firstMatch = null;
            var matchCount = 0;

            foreach (var item in tabItems)
            {
                string? name;
                try { name = item.Name; } catch { continue; }
                if (name is null || !name.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase)) continue;

                matchCount++;
                firstMatch ??= item;
            }

            if (matchCount > 1)
            {
                OcrLogger.LogTiming($"UiaTreeWalker.FindDescendantTabItemByNamePrefix: {matchCount} TabItems matched prefix '{namePrefix}' — using the first one found (document order).");
            }

            return firstMatch;
        }
        catch
        {
            // Best-effort only — see method doc.
        }

        return null;
    }

    /// <summary>
    /// Reads a TabItem's SelectionItemPattern.IsSelected — used by
    /// Uia/CommonTabGate.cs's PRIMARY outer-tab signal. Read-only: never
    /// calls Select(). Returns null (not false) if the pattern isn't
    /// supported or the read throws (stale/disconnected element), so the
    /// caller can tell "definitively not selected" apart from "couldn't
    /// tell" and fall through to the SECONDARY pane-presence signal
    /// instead of trusting a false negative. Extracted as a static
    /// element-in/value-out method for the same reason as ReadTextValue/
    /// ReadCheckBoxValue above — lets a CACHED element (CommonTabGate's
    /// own per-attach-session cache) be re-read directly without a fresh
    /// find each tick.
    /// </summary>
    public static bool? ReadIsSelected(AutomationElement element)
    {
        try
        {
            if (!element.Patterns.SelectionItem.IsSupported) return null;
            return element.Patterns.SelectionItem.Pattern.IsSelected.ValueOrDefault;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads an element's IsOffscreen automation property. Used by
    /// Uia/CommonTabGate.cs's SECONDARY pane-presence signal (an outer
    /// tab's content pane that exists in the tree but reads IsOffscreen
    /// true belongs to a tab that isn't actually the one showing) and by
    /// FieldReader.ReadEnteredFieldRects' hardened resolvable-rect proxy
    /// (Ocr/EscriptImageCapture.cs's IsGenuinelyOnscreen guard follows the
    /// same element.Properties.IsOffscreen idiom, non-tri-state). Returns
    /// null (not false) if the property can't be read at all — "unknown"
    /// must never be silently treated as "onscreen", same reasoning as
    /// ReadIsSelected above: the caller needs to tell that apart from a
    /// confirmed answer.
    /// </summary>
    public static bool? ReadIsOffscreen(AutomationElement element)
    {
        try
        {
            return element.Properties.IsOffscreen.ValueOrDefault;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a plausible current value out of an Edit/ComboBox element:
    ///   1. ValuePattern.Value, if the pattern is supported and non-blank
    ///      — this is the correct source of truth for a real typed/
    ///      selected value and is tried first.
    ///   2. Otherwise, the Name of the first focusable Edit DESCENDANT
    ///      (not just direct child) — some WinForms-hosted edit controls
    ///      expose their real text only on a nested child rather than via
    ///      ValuePattern on the outer element.
    ///   3. Otherwise, the element's own Name — for most Edit fields in
    ///      the real dumps (uxPatientQuickSearch, uxPrescriberQuickSearch,
    ///      uxQuantityPrescribed, uxRefills, uxWrittenDate) this is just
    ///      the field's static placeholder label (e.g. "Quantity:"), NOT
    ///      the typed value — those dumps never captured the control in a
    ///      populated state via a static Name read. uxDirections is the
    ///      one confirmed exception where Name already IS the value (see
    ///      FieldMap.EnteredDirectionsId). Will's next live capture should
    ///      confirm whether step 1 (ValuePattern) actually resolves the
    ///      real values for the other fields — if it doesn't, this
    ///      fallback chain will silently return the placeholder text
    ///      instead, which the engine would then compare as if it were a
    ///      real (and wrong) entered value.
    /// </summary>
    public static string? ReadEditOrComboValue(AutomationElement element)
    {
        try
        {
            if (element.Patterns.Value.IsSupported)
            {
                var value = element.Patterns.Value.Pattern.Value.ValueOrDefault;
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
        }
        catch
        {
            // Pattern advertised as supported but the provider threw
            // anyway (seen in practice with some WinForms UIA proxies).
        }

        try
        {
            var innerEdits = element.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit));
            foreach (var inner in innerEdits)
            {
                string? name;
                try { name = inner.Name; } catch { continue; }
                if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
            }
        }
        catch
        {
            // Stale/disconnected element mid-redraw — fall through to
            // the last-resort Name read below.
        }

        try
        {
            var name = element.Name;
            if (string.IsNullOrWhiteSpace(name)) return null;
            var trimmed = name.Trim();

            // LABEL-LEAK GUARD: every confirmed static placeholder label
            // in FieldMap.cs (e.g. "Patient:", "Written By:", "Item:",
            // "Quantity:", "Refills:", "Written:", "Expire:") ends with a
            // trailing colon; uxDirections — the one field where .Name is
            // already the real typed value — never does (a real sig
            // never ends with a bare colon). So a last-resort .Name that
            // ends with ":" is, by construction of every field this
            // reader touches, a leaked label rather than a real value
            // (e.g. uxPrescriberQuickSearch returning "Written By:" as if
            // it were the prescriber's name). Returning it as a real
            // value would read as "a wrong prescriber" rather than "no
            // value available" — return null instead so the field shows
            // as not-provided (yellow), which is the honest state here:
            // neither ValuePattern nor a nested Edit produced anything,
            // so we genuinely don't have the value.
            if (trimmed.EndsWith(':')) return null;

            return trimmed;
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // CENTER TAB SELECT/RESTORE (so the Escript tab's tree exists in the
    // UIA tree even when the pharmacist is viewing a different tab, e.g.
    // Image — see FieldReader.ReadSource for the select->read->restore
    // orchestration and FieldMap.CenterTabControlAutomationId for why
    // matching is by Name PREFIX, not exact Name).
    // ------------------------------------------------------------------

    /// <summary>
    /// Finds the center content Tab control (FieldMap.CenterTabControlAutomationId)
    /// anywhere under the window and returns it as FlaUI's typed Tab
    /// wrapper (.TabItems / .SelectedTabItem). Returns null if not found
    /// rather than throwing — the window may not be fully rendered, or
    /// PioneerRx's screen shape may differ from the two confirmed dumps.
    /// </summary>
    private Tab? FindCenterTabControl()
    {
        var element = FindDescendantByAutomationId(FieldMap.CenterTabControlAutomationId);
        if (element is null) return null;

        try { return element.AsTab(); }
        catch { return null; }
    }

    /// <summary>
    /// Selects the center TabItem whose Name STARTS WITH namePrefix (e.g.
    /// "Escript" matches "Escript [3]" — see FieldMap.CenterTabControlAutomationId
    /// doc for why this must be a prefix match, not exact). Returns the
    /// Name of whichever TabItem was selected BEFORE this call, so the
    /// caller can pass it to RestoreCenterTabByName — or null if the tab
    /// control or a matching item couldn't be found. `selected` is true
    /// only if Select() was actually invoked without throwing.
    ///
    /// UNKNOWN, flag for Will on a real workstation: whether
    /// SelectionItemPattern (which TabItem.Select() uses under the hood)
    /// is actually supported on PioneerRx's TabItem control — neither
    /// real dump captures pattern support, only Name/ControlType/bounds.
    /// If it isn't supported, this degrades to "couldn't switch tabs"
    /// (selected=false) rather than throwing, and ReadSource falls back
    /// to whatever's already in the UIA tree (i.e. behaves exactly like
    /// before this change).
    /// </summary>
    public string? SelectCenterTabByPrefix(string namePrefix, out bool selected)
    {
        selected = false;
        var tab = FindCenterTabControl();
        if (tab is null) return null;

        string? previouslySelectedName = null;
        try { previouslySelectedName = tab.SelectedTabItem?.Name; }
        catch { /* leave null — RestoreCenterTabByName treats that as a no-op, never a crash */ }

        TabItem? target = null;
        try
        {
            foreach (var item in tab.TabItems)
            {
                string? name;
                try { name = item.Name; } catch { continue; }
                if (name is not null && name.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    target = item;
                    break;
                }
            }
        }
        catch { /* TabItems threw — no target found, fall through to "not found" */ }

        if (target is null) return previouslySelectedName;

        try
        {
            target.Select();
            selected = true;
        }
        catch
        {
            selected = false;
        }

        return previouslySelectedName;
    }

    /// <summary>
    /// Restores whichever TabItem was selected before SelectCenterTabByPrefix.
    /// Called from FieldReader.ReadSource's finally block so the
    /// pharmacist's view is ALWAYS returned to exactly where it was, even
    /// if the Escript-tree read in between throws. A no-op if
    /// previousName is null/blank or the tab can no longer be found
    /// (e.g. the PioneerRx window closed mid-read) — this method must
    /// never throw, since it runs inside a finally block.
    /// </summary>
    public void RestoreCenterTabByName(string? previousName)
    {
        if (string.IsNullOrEmpty(previousName)) return;

        var tab = FindCenterTabControl();
        if (tab is null) return;

        try
        {
            foreach (var item in tab.TabItems)
            {
                string? name;
                try { name = item.Name; } catch { continue; }
                if (name == previousName)
                {
                    item.Select();
                    return;
                }
            }
        }
        catch
        {
            // Best-effort restore only.
        }
    }

    // ------------------------------------------------------------------
    // SOURCE (Escript tab Tree -> EscriptNode adapter)
    // ------------------------------------------------------------------

    /// <summary>
    /// Finds the Escript Tree control (AutomationId ux10Dot6Escript)
    /// anywhere under the window and walks its single top-level TreeItem
    /// ("Message" in the real dump) into an EscriptNode tree. Returns
    /// null if the tree control isn't found at all — this is the "Escript
    /// tab was never opened this session" case FieldReader/
    /// IsStructuredSourceAvailable use to show the "open the Escript tab"
    /// message rather than ten yellow fields.
    ///
    /// PERFORMANCE (W-T11 item 2 — "the e-script data read feels slow"):
    /// this used to fully recurse the ENTIRE tree unconditionally. A real
    /// e-script's Escript tree runs to ~150 TreeItems (confirmed against
    /// escript-249.txt), and EscriptTreeParser only ever reads three of
    /// NewRx's children — Patient, Prescriber, MedicationPrescribed (see
    /// FieldMap.NodePatient/NodePrescriber/NodeMedicationPrescribed). The
    /// other ~⅓-½ of the tree (Header, BenefitsCoordination/
    /// PayerIdentification, Observation/vital-sign measurements, Pharmacy,
    /// Supervisor, ...) was being fully walked and converted to
    /// EscriptNodes on every cache miss for no reason — every single one
    /// of those nodes is at least 2-3 cross-process UIA COM calls (.Name,
    /// .ControlType, .FindAllChildren()), which is real, measurable
    /// latency on a live workstation (COM/UIA marshaling, not local
    /// memory access). BuildPrunedMessageNode below walks the
    /// Message -> Body -> NewRx spine BY NAME (cheap — each of those
    /// levels has only 1-2 real children) and then fully recurses ONLY
    /// the three relevant NewRx children, cutting total TreeItem visits
    /// by roughly half on a typical script. See FieldReader.ReadSource
    /// for the complementary per-Rx cache, which already avoids paying
    /// this cost more than once per Rx — this makes that one unavoidable
    /// walk itself cheaper.
    /// </summary>
    public EscriptNode? BuildEscriptTree()
    {
        var treeElement = FindDescendantByAutomationId(FieldMap.EscriptTreeAutomationId);
        if (treeElement is null) return null;

        AutomationElement[] children;
        try { children = treeElement.FindAllChildren(); }
        catch { return null; }

        foreach (var child in children)
        {
            ControlType controlType;
            try { controlType = child.ControlType; }
            catch { continue; }

            // The Tree's real children include a ScrollBar alongside the
            // actual TreeItem(s) — see the real dump (NonClientVerticalScrollBar
            // sibling of the top-level "Message" TreeItem). Skip anything
            // that isn't a TreeItem.
            if (controlType != ControlType.TreeItem) continue;

            return BuildPrunedMessageNode(child);
        }

        return null;
    }

    /// <summary>NewRx's only children EscriptTreeParser ever reads — see BuildEscriptTree's perf doc above.</summary>
    private static readonly string[] RelevantNewRxChildNames =
    {
        FieldMap.NodePatient,
        FieldMap.NodePrescriber,
        FieldMap.NodeMedicationPrescribed
    };

    /// <summary>
    /// Builds the Message -&gt; Body -&gt; (NewRx-or-equivalent) spine by
    /// exact/structural child lookup (not a full recursive walk — each of
    /// these levels only has 1-2 real children in practice), then fully
    /// recurses ONLY the prescription container's
    /// Patient/Prescriber/MedicationPrescribed children via the unchanged
    /// full-recursion BuildEscriptNode. Degrades gracefully (never
    /// throws) at every step exactly like the old full-recursion did for
    /// a missing/differently-shaped message — a message with no Body, or
    /// with a Body that holds neither a "NewRx"-named child nor any child
    /// with a MedicationPrescribed subtree (see
    /// FindPrescriptionContainerElement), still yields a Message node
    /// whose Body/prescription-container simply weren't found, which
    /// EscriptTreeParser.Parse already treats as an empty
    /// PrescriptionRecord.
    /// </summary>
    private static EscriptNode BuildPrunedMessageNode(AutomationElement messageElement)
    {
        var messageNode = new EscriptNode(SafeName(messageElement));

        // Message-level Note (item 6, pharmacy-directed free text) —
        // preserved even though it's a sibling of Body, since
        // EscriptTreeParser.ParseNotes looks for it directly on the
        // Message node. See FieldMap.NodeNote (UNCONFIRMED shape).
        CollectNoteChildrenIfPresent(messageElement, messageNode);

        var bodyElement = FindNamedTreeItemChild(messageElement, FieldMap.NodeBody);
        if (bodyElement is null) return messageNode;

        var bodyNode = new EscriptNode(SafeName(bodyElement));
        messageNode.Children.Add(bodyNode);

        var newRxElement = FindPrescriptionContainerElement(bodyElement);
        if (newRxElement is null) return messageNode;

        var newRxNode = new EscriptNode(SafeName(newRxElement));
        bodyNode.Children.Add(newRxNode);

        // NewRx-level Note (item 6, medication-directed free text) — same
        // UNCONFIRMED caveat as above.
        CollectNoteChildrenIfPresent(newRxElement, newRxNode);

        foreach (var wantedName in RelevantNewRxChildNames)
        {
            var childElement = FindNamedTreeItemChild(newRxElement, wantedName);
            if (childElement is not null)
            {
                newRxNode.Children.Add(BuildEscriptNode(childElement));
            }
        }

        // ROUND 3 FIX (Will's 2026-08-17 live false-yellow, gap (2b) — see
        // Parsing/EscriptTreeParser.cs ParseRefills's doc): a renewal
        // response's "Total Fills: N (...)" summary line is documented
        // UNCONFIRMED as to whether it nests inside MedicationPrescribed
        // (already covered — MedicationPrescribed's whole subtree is
        // fully recursed above, no pruning below that level) or sits as
        // its OWN direct sibling of MedicationPrescribed under a name
        // other than the three RelevantNewRxChildNames this loop prunes
        // down to. Without this, that second shape would be silently
        // dropped here — before EscriptTreeParser.ParseRefills's own
        // newRx.Children fallback ever gets a chance to see it — no
        // matter how that parser searches. Narrow and cheap by
        // construction (Name check only, never recurses into whatever
        // this leaf's own children might be) so it can't reintroduce the
        // W-T11 perf regression this method exists to avoid.
        CollectTotalFillsSiblingIfPresent(newRxElement, newRxNode);

        return messageNode;
    }

    /// <summary>See BuildPrunedMessageNode's ROUND 3 FIX call-site comment.</summary>
    private static void CollectTotalFillsSiblingIfPresent(AutomationElement newRxElement, EscriptNode newRxNode)
    {
        AutomationElement[] children;
        try { children = newRxElement.FindAllChildren(); }
        catch { return; }

        foreach (var child in children)
        {
            ControlType controlType;
            try { controlType = child.ControlType; }
            catch { continue; }
            if (controlType != ControlType.TreeItem) continue;

            var name = SafeName(child);
            if (FieldMap.TotalFillsKeyPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                newRxNode.Children.Add(new EscriptNode(name));
                return;
            }
        }
    }

    /// <summary>
    /// Locates the Body child that holds the prescription data — the
    /// live-UIA counterpart of EscriptTreeParser.FindPrescriptionContainer,
    /// which applies the IDENTICAL decision rule to the already-built
    /// EscriptNode graph. Kept as an independent implementation (this one
    /// walks live FlaUI AutomationElements; that one walks the UIA-free
    /// EscriptNode this method itself produces) rather than a shared
    /// helper, since the two operate on different node types with no
    /// common interface today — see EscriptNode.cs's class doc for why
    /// that split representation exists at all (UIA-free unit
    /// testability for the parser).
    ///
    /// BLOCKER FIX (round 2 reviewer): this is the method that actually
    /// builds the tree EscriptTreeParser.Parse consumes in production via
    /// FieldReader.ReadSource -&gt; BuildEscriptTree -&gt; here. Before this
    /// fix, this method ONLY ever looked for a child literally named
    /// "NewRx" — so for any renewal-shaped message the Body node it
    /// returned had ZERO children, and EscriptTreeParser's own
    /// structural-detection fallback had nothing to search: Parse() still
    /// returned an empty record for every real renewal even after that
    /// parser-side change. This method now applies the same two-step
    /// rule: exact "NewRx" name match first (preserves the W-T11 cheap-
    /// spine perf optimization for the common case — see
    /// BuildEscriptTree's class doc), then structural fallback for
    /// anything else: whichever Body child directly holds a
    /// MedicationPrescribed child (the same anchor RelevantNewRxChildNames
    /// already treats as one of the three children this class reads).
    ///
    /// NOT UNIT TESTED: this method is bound to a live FlaUI
    /// AutomationElement (Windows UI Automation) with no
    /// programmatic/synthetic construction path anywhere in this codebase
    /// (unlike EscriptNode, purpose-built for in-memory test construction
    /// — see EscriptTreeParserTests.cs), and no live PioneerRx workstation
    /// is available in this dev/CI environment to capture a real renewal-
    /// response UIA dump against. The decision rule itself (name-first,
    /// then MedicationPrescribed-subtree fallback) is exercised against
    /// synthetic EscriptNode trees by EscriptTreeParserTests.cs's
    /// FindPrescriptionContainer tests; this method is a thin,
    /// side-effect-free adapter of that same rule onto AutomationElement
    /// and should be spot-checked against a real renewal-response UIA
    /// dump before this ships to a live workstation (see FieldMap.cs
    /// header for how the prior confirmed UIA shapes were captured).
    /// </summary>
    private static AutomationElement? FindPrescriptionContainerElement(AutomationElement bodyElement)
    {
        var namedNewRx = FindNamedTreeItemChild(bodyElement, FieldMap.NodeNewRx);
        if (namedNewRx is not null) return namedNewRx;

        AutomationElement[] children;
        try { children = bodyElement.FindAllChildren(); }
        catch { return null; }

        AutomationElement? found = null;
        var matchCount = 0;
        foreach (var child in children)
        {
            ControlType controlType;
            try { controlType = child.ControlType; }
            catch { continue; }
            if (controlType != ControlType.TreeItem) continue;

            if (FindNamedTreeItemChild(child, FieldMap.NodeMedicationPrescribed) is null) continue;

            matchCount++;
            found ??= child;
        }

        if (matchCount > 1)
        {
            // Real NCPDP renewals can carry more than one section holding
            // a MedicationPrescribed subtree (e.g. alongside a separate
            // MedicationDispensed section). Deterministic — the first
            // match found (in FindAllChildren order) wins, same rule as
            // EscriptTreeParser.FindPrescriptionContainer — but flagged:
            // there is no structured logging anywhere in this codebase
            // (grepped Uia/ViewModels/Integrated/Parsing for
            // ILogger/Trace/Debug — none found), so Debug.WriteLine is
            // the smallest addition that makes an ambiguous pick visible
            // in a debugger/Output window instead of failing silently.
            Debug.WriteLine(
                $"UiaTreeWalker.FindPrescriptionContainerElement: {matchCount} Body children each hold a " +
                $"MedicationPrescribed subtree; using the first one found ('{SafeName(found!)}').");
        }

        return found;
    }

    /// <summary>
    /// Finds any direct TreeItem child of <paramref name="parent"/> that
    /// is either a "Note" CONTAINER (exact name match) or a bare
    /// "Note: &lt;text&gt;" LEAF (name starts with "Note: "), and fully
    /// recurses it (BuildEscriptNode — a Note subtree is small either
    /// way) onto <paramref name="target"/>.Children. Used for the item-6
    /// Note lookups in BuildPrunedMessageNode — see FieldMap.NodeNote
    /// (UNCONFIRMED shape, neither variant has been seen in a real dump)
    /// and EscriptTreeParser.CollectNotesFrom, which handles both shapes
    /// once the node is in the tree.
    /// </summary>
    private static void CollectNoteChildrenIfPresent(AutomationElement parent, EscriptNode target)
    {
        AutomationElement[] children;
        try { children = parent.FindAllChildren(); }
        catch { return; }

        var notePrefix = FieldMap.NodeNote + ": ";
        foreach (var child in children)
        {
            ControlType controlType;
            try { controlType = child.ControlType; }
            catch { continue; }
            if (controlType != ControlType.TreeItem) continue;

            var name = SafeName(child);
            var isNoteContainer = string.Equals(name, FieldMap.NodeNote, StringComparison.Ordinal);
            var isBareNoteLeaf = name.StartsWith(notePrefix, StringComparison.Ordinal);
            if (isNoteContainer || isBareNoteLeaf)
            {
                target.Children.Add(BuildEscriptNode(child));
            }
        }
    }

    /// <summary>Finds a direct TreeItem child of <paramref name="parent"/> with an exact Name match — used only for the cheap Message/Body/NewRx spine (see BuildPrunedMessageNode), never for the relevant subtrees themselves (those still use the general FindAllChildren-based BuildEscriptNode below).</summary>
    private static AutomationElement? FindNamedTreeItemChild(AutomationElement parent, string name)
    {
        AutomationElement[] children;
        try { children = parent.FindAllChildren(); }
        catch { return null; }

        foreach (var child in children)
        {
            ControlType controlType;
            try { controlType = child.ControlType; }
            catch { continue; }

            if (controlType != ControlType.TreeItem) continue;
            if (string.Equals(SafeName(child), name, StringComparison.Ordinal)) return child;
        }

        return null;
    }

    private static string SafeName(AutomationElement element)
    {
        try { return element.Name ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static EscriptNode BuildEscriptNode(AutomationElement element)
    {
        var node = new EscriptNode(SafeName(element));

        AutomationElement[] children;
        try { children = element.FindAllChildren(); }
        catch { return node; }

        foreach (var child in children)
        {
            ControlType controlType;
            try { controlType = child.ControlType; }
            catch { continue; }

            if (controlType != ControlType.TreeItem) continue;

            node.Children.Add(BuildEscriptNode(child));
        }

        return node;
    }

    // ------------------------------------------------------------------
    // DEBUG MODE (unchanged) — dumps the full tree so Will can diff it
    // against FieldMap.cs when a field doesn't read correctly.
    // ------------------------------------------------------------------

    public string DumpTree()
    {
        var sb = new StringBuilder();
        DumpRecursive(_root, 0, sb);
        return sb.ToString();
    }

    private static void DumpRecursive(AutomationElement element, int depth, StringBuilder sb)
    {
        string name = "<name threw>";
        string controlType = "<type threw>";
        bool focusable = false;
        string rect = "<rect threw>";
        string automationId = "<id threw>";

        try { name = element.Name ?? "<null>"; } catch { }
        try { controlType = element.ControlType.ToString(); } catch { }
        try { focusable = element.Properties.IsKeyboardFocusable.ValueOrDefault; } catch { }
        try { rect = element.BoundingRectangle.ToString(); } catch { }
        try { automationId = element.AutomationId ?? "<null>"; } catch { }

        sb.Append(' ', depth * 2)
          .Append(controlType)
          .Append(" name='").Append(name).Append('\'')
          .Append(" id='").Append(automationId).Append('\'')
          .Append(" focusable=").Append(focusable)
          .Append(" rect=").Append(rect)
          .AppendLine();

        AutomationElement[] children;
        try { children = element.FindAllChildren(); }
        catch { return; }

        foreach (var child in children)
        {
            DumpRecursive(child, depth + 1, sb);
        }
    }
}
