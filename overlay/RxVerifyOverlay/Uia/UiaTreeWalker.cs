using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace RxVerifyOverlay.Uia;

/// <summary>
/// Walks the FULL UIA control-view tree of a window (not just the
/// focusable/tab-order elements) and pairs on-screen labels ("Patient:",
/// "DOB:", etc.) with their values. This is the piece that makes the
/// read-only text nodes readable — the phase-0 spec found those are
/// Not Focusable, so anything that only walks focusable controls (tab
/// order) misses them entirely; the value is exposed as the element's
/// Name, but only reachable by a full control-view walk.
///
/// PAIRING STRATEGY (best-effort, needs live validation — see
/// FieldMap.cs header):
///   1. Collect all elements in the window in DEPTH-FIRST,
///      LEFT-TO-RIGHT / TOP-TO-BOTTOM traversal order (which is how
///      UIA's raw tree naturally walks a form laid out in reading
///      order), find the text/label element whose Name equals the
///      requested label (e.g. "DOB:"), and take the NEXT element in
///      that traversal order as the value — whether that's an edit's
///      Value pattern text or a read-only text node's Name.
///   2. Because several labels repeat (e.g. "Address:" and "Phone:"
///      appear twice — once for patient, once for prescriber — callers
///      pass an `occurrence` index (0-based) to disambiguate, and/or a
///      `withinBounds` rectangle to scope the search to one visual
///      panel (left data-entry vs. center e-script vs. a specific
///      color-coded box) when position is known. See FieldReader.cs
///      for how each panel's bounds are established.
/// </summary>
public sealed class UiaTreeWalker
{
    private readonly AutomationElement _root;

    public UiaTreeWalker(AutomationElement root)
    {
        _root = root;
    }

    /// <summary>
    /// Every element in the window, in document (reading) order, via the
    /// raw view — i.e. NOT filtered down to control-view/content-view,
    /// so Not-Focusable text nodes are included. FlaUI's default
    /// FindAllDescendants uses the control view, which already includes
    /// text elements in practice for most native controls; if a value
    /// turns out to be missing on Will's real window, switch the walker
    /// to `RawViewWalker` (FlaUI.Core.Tools) for that specific call —
    /// left as a documented fallback rather than the default, since the
    /// raw view is much noisier (includes non-semantic scrollbars,
    /// internal panes, etc.) and slower to walk on every refresh.
    /// </summary>
    public List<AutomationElement> GetAllElementsInOrder()
    {
        var result = new List<AutomationElement>();
        CollectDepthFirst(_root, result);
        return result;
    }

    private static void CollectDepthFirst(AutomationElement element, List<AutomationElement> into)
    {
        into.Add(element);
        AutomationElement[] children;
        try
        {
            children = element.FindAllChildren();
        }
        catch
        {
            // Some UIA providers throw on disconnected/stale elements
            // mid-walk (e.g. PioneerRx redraws while we're reading).
            // Skip this branch rather than crash the whole read.
            return;
        }

        foreach (var child in children)
        {
            CollectDepthFirst(child, into);
        }
    }

    /// <summary>
    /// Finds the value for a given on-screen label, e.g. "DOB:".
    /// Returns null if the label isn't found, or if it's found but no
    /// plausible value follows it (caller must treat that as "field not
    /// provided", never as a mismatch — see FieldReader).
    /// </summary>
    /// <param name="label">Exact label text as it appears on screen, e.g. "Phone:".</param>
    /// <param name="occurrence">0-based index when the label repeats (e.g. second "Phone:" = prescriber's).</param>
    /// <param name="searchBounds">
    /// Optional bounding rectangle (screen coordinates) to scope the
    /// search to one panel/box, so repeated labels ("Address:", "Phone:")
    /// resolve to the right occurrence by POSITION instead of by
    /// fragile ordinal counting across the whole window.
    /// </param>
    public string? FindValueForLabel(string label, int occurrence = 0, Rectangle? searchBounds = null)
    {
        var all = GetAllElementsInOrder();
        var candidates = new List<(AutomationElement Element, int Index)>();

        for (var i = 0; i < all.Count; i++)
        {
            var el = all[i];
            string? name;
            try { name = el.Name; }
            catch { continue; }

            if (name is null) continue;
            if (!string.Equals(name.Trim(), label.Trim(), StringComparison.Ordinal)) continue;

            if (searchBounds.HasValue)
            {
                Rectangle bounds;
                try { bounds = el.BoundingRectangle; }
                catch { continue; }

                if (!searchBounds.Value.Contains(bounds)) continue;
            }

            candidates.Add((el, i));
        }

        if (candidates.Count == 0) return null;
        var chosenIndex = occurrence < candidates.Count ? occurrence : candidates.Count - 1;
        var (_, labelPosition) = candidates[chosenIndex];

        // The next element in traversal order after the label is almost
        // always its value in these forms (label static text immediately
        // followed by its edit/pane/text sibling in the same row/column).
        //
        // NOTE: UIA does expose a formal LabeledBy relationship
        // (UIA_LabeledByPropertyId) that would be a more robust primary
        // signal than position — worth trying first if you have FlaUI's
        // exact property-wrapper name in front of you on the live
        // workstation (varies by FlaUI version; check
        // AutomationElement.Properties in IntelliSense). Left as
        // positional-only for v0 so this file doesn't depend on an API
        // shape we couldn't verify without a Windows machine.
        for (var j = labelPosition + 1; j < all.Count; j++)
        {
            var value = ReadValue(all[j]);
            if (value is not null) return value;

            // Don't walk past a plausible next label — if we hit
            // another colon-terminated static text before finding a
            // value, this label had no value (blank field).
            string? nextName;
            try { nextName = all[j].Name; }
            catch { nextName = null; }
            if (nextName is not null && nextName.TrimEnd().EndsWith(':')) break;
        }

        return null;
    }

    /// <summary>
    /// Reads a plausible "value" out of an element: an edit/combo's
    /// ValuePattern text if it has one and it's non-blank, else its Name
    /// if the element is a read-only text control with non-blank Name
    /// and isn't itself another label (heuristic: doesn't end in ':').
    /// </summary>
    private static string? ReadValue(AutomationElement element)
    {
        try
        {
            if (element.Patterns.Value.IsSupported)
            {
                var v = element.Patterns.Value.Pattern.Value.ValueOrDefault;
                if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
            }
        }
        catch { /* pattern not actually available on this provider */ }

        try
        {
            if (element.ControlType == ControlType.Text)
            {
                var name = element.Name;
                if (!string.IsNullOrWhiteSpace(name) && !name.TrimEnd().EndsWith(':'))
                {
                    return name.Trim();
                }
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// DEBUG MODE: dumps the full tree (control type, name, automation
    /// id, focusable, bounding rect, depth) to a plain-text string. Wire
    /// this to a button in the overlay so Will can capture it against
    /// the live PioneerRx window and diff it against FieldMap.cs when a
    /// field doesn't read correctly. Writes NOTHING to disk by itself —
    /// the caller (MainWindow) decides whether/where to save it, so a
    /// dump full of real patient data never gets written without an
    /// explicit, visible action.
    /// </summary>
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
