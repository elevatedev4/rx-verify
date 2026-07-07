using System;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
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
        if (element is null) return null;

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

            return BuildEscriptNode(child);
        }

        return null;
    }

    private static EscriptNode BuildEscriptNode(AutomationElement element)
    {
        string name;
        try { name = element.Name ?? string.Empty; }
        catch { name = string.Empty; }

        var node = new EscriptNode(name);

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
