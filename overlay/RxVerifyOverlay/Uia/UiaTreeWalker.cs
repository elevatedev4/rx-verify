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
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }
        catch
        {
            return null;
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
