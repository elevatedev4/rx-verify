using System;
using System.Drawing;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace RxVerifyOverlay.Uia;

/// <summary>
/// Finds and attaches to the active PioneerRx Pre-Check/Edit/New-Rx
/// window, using UIA3 (the modern UIA COM API — see FlaUI.UIA3).
///
/// This used to also own rough fractional-panel-bounds geometry
/// (LeftPanelBounds/CenterPanelBounds/CenterPatientBoxBounds/etc.) to
/// disambiguate repeated labels like "Address:"/"Phone:" by screen
/// position. That was inferred from screenshots, never validated, and
/// has been removed entirely: both the ENTERED fields (FieldReader.
/// ReadEntered) and the SOURCE e-script (FieldReader.ReadSource) are now
/// found by AutomationId / Escript-tree-node-name (see FieldMap.cs and
/// UiaTreeWalker.cs), which needs no panel geometry at all — see
/// FieldMap.cs header for the two real UIA dumps this was confirmed
/// against.
/// </summary>
public sealed class PioneerRxWindow : IDisposable
{
    private readonly Application? _application;
    private readonly AutomationBase _automation;

    public AutomationElement WindowElement { get; }
    public Rectangle WindowBounds { get; }

    private PioneerRxWindow(AutomationBase automation, AutomationElement windowElement, Application? application)
    {
        _automation = automation;
        WindowElement = windowElement;
        _application = application;
        WindowBounds = SafeBounds(windowElement);
    }

    private static Rectangle SafeBounds(AutomationElement el)
    {
        try { return el.BoundingRectangle; }
        catch { return Rectangle.Empty; }
    }

    /// <summary>
    /// Attempts to find a top-level window whose title starts with one
    /// of FieldMap.TargetWindowTitlePrefixes. Returns null (does not
    /// throw) if none is currently open — callers should show "waiting
    /// for PioneerRx..." rather than crash, since the pharmacist may be
    /// on an unrelated screen at any given moment.
    /// </summary>
    public static PioneerRxWindow? TryAttach()
    {
        var automation = new UIA3Automation();
        try
        {
            var desktop = automation.GetDesktop();
            var allTopLevel = desktop.FindAllChildren();

            foreach (var window in allTopLevel)
            {
                string? name;
                try { name = window.Name; }
                catch { continue; }

                if (name is null) continue;

                foreach (var prefix in FieldMap.TargetWindowTitlePrefixes)
                {
                    if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return new PioneerRxWindow(automation, window, application: null);
                    }
                }
            }

            return null;
        }
        catch
        {
            automation.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _application?.Dispose();
        _automation.Dispose();
    }

    /// <summary>
    /// Cheap snapshot of "is the pre-check/edit/new-rx screen open, and
    /// which Rx is it showing" — just Present + an opaque RxNumber
    /// string parsed from the window title. Used by
    /// OverlayViewModel.WatchAsync (W-T9 item 5, auto-watch) to detect
    /// when a full refresh is warranted WITHOUT doing a full refresh's
    /// work every tick.
    /// </summary>
    public readonly record struct ScreenSignature(bool Present, string? RxNumber)
    {
        public static readonly ScreenSignature NotPresent = new(false, null);
    }

    /// <summary>
    /// Attaches only long enough to read the window's title (.Name), then
    /// immediately disposes — no FieldReader panel walk, no Escript tree
    /// read, no engine subprocess call. This is the whole point: it costs
    /// roughly what TryAttach's own top-level-window scan costs, so it's
    /// safe to call on a short (~1s) timer for change-detection, unlike a
    /// full RefreshAsync which also reads both UIA panels and calls the
    /// TS engine.
    ///
    /// PioneerRx window titles always start with the screen name followed
    /// by the Rx number ("Edit Rx - &lt;rx number&gt; - ...", confirmed
    /// in real UIA dumps — see FieldMap.cs doc). RxNumber is parsed as
    /// the segment between the first two " - " separators; if the title
    /// doesn't have that shape (e.g. a fresh "New Rx" screen with no
    /// number assigned yet), the RxNumber falls back to the full title
    /// text itself, so a change in title (e.g. a different Rx opened, or
    /// New Rx -> a saved/numbered Rx) still trips change-detection even
    /// without a parseable number.
    /// </summary>
    public static ScreenSignature GetScreenSignature()
    {
        using var window = TryAttach();
        if (window is null) return ScreenSignature.NotPresent;

        string name;
        try { name = window.WindowElement.Name ?? ""; }
        catch { name = ""; }

        return new ScreenSignature(true, ExtractRxNumber(name));
    }

    /// <summary>
    /// Parses the Rx number segment out of a title of shape
    /// "&lt;Screen Name&gt; - &lt;rx number&gt; - ...". Splits on " - "
    /// and returns the second segment if there are at least 3 segments;
    /// otherwise returns the whole title unchanged (see
    /// GetScreenSignature doc for why that fallback still works for
    /// change-detection).
    /// </summary>
    private static string? ExtractRxNumber(string title)
    {
        if (string.IsNullOrEmpty(title)) return null;

        var parts = title.Split(" - ", StringSplitOptions.None);
        return parts.Length >= 3 ? parts[1].Trim() : title;
    }
}
