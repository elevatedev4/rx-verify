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
}
