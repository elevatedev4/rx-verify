using System;
using System.Drawing;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace RxVerifyOverlay.Uia;

/// <summary>
/// Finds and attaches to the active PioneerRx Pre-Check/Edit/New-Rx
/// window, using UIA3 (the modern UIA COM API — see FlaUI.UIA3). Also
/// owns the rough panel geometry used to disambiguate repeated labels
/// (see FieldMap.cs — "Address:"/"Phone:" appear once for patient, once
/// for prescriber).
///
/// GEOMETRY NOTE: the split points below (LeftPanelRightEdgeFraction
/// etc.) are estimated from the screenshot layout (left data-entry
/// panel ends around x≈400px of a ~1920px-wide maximized window; center
/// e-script panel runs roughly x≈400-1580). These are FRACTIONS of the
/// window's client width so they hold up across different monitor
/// resolutions/window sizes, but they are still an estimate — if a
/// field reads a value from the wrong panel, that's the first thing to
/// re-measure against a live screenshot (use the "Dump UIA Tree" debug
/// mode, which prints each element's BoundingRectangle, and compare
/// against the window's own BoundingRectangle to compute the real
/// fractions).
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

    /// <summary>
    /// Rough bounds of the LEFT (technician data-entry / EnteredData)
    /// panel, as a fraction of the window width. From the screenshots,
    /// this panel occupies roughly the left ~21% of a maximized window
    /// (Patient/Written By/Written/Item/Quantity/Directions block).
    /// </summary>
    public Rectangle LeftPanelBounds()
    {
        return FractionalBounds(0.0, 0.0, 0.21, 1.0);
    }

    /// <summary>
    /// Rough bounds of the CENTER (parsed e-script / ScriptData) panel —
    /// the "New Prescription" pane with the green/yellow/blue boxes.
    /// From the screenshots this runs roughly x: 21%-82% of the window.
    /// </summary>
    public Rectangle CenterPanelBounds()
    {
        return FractionalBounds(0.21, 0.0, 0.82, 1.0);
    }

    /// <summary>
    /// Sub-region of the center panel for just the green "Patient" box
    /// (roughly the top ~15% of the center panel's height in the
    /// screenshots).
    /// </summary>
    public Rectangle CenterPatientBoxBounds()
    {
        return FractionalBounds(0.21, 0.14, 0.82, 0.27);
    }

    /// <summary>
    /// Sub-region of the center panel for the yellow "Prescriber" box
    /// (roughly the next ~15% below the patient box).
    /// </summary>
    public Rectangle CenterPrescriberBoxBounds()
    {
        return FractionalBounds(0.21, 0.27, 0.82, 0.42);
    }

    /// <summary>
    /// Sub-region of the center panel for the blue "Rx" box (everything
    /// below the prescriber box).
    /// </summary>
    public Rectangle CenterRxBoxBounds()
    {
        return FractionalBounds(0.21, 0.42, 0.82, 0.85);
    }

    private Rectangle FractionalBounds(double xStart, double yStart, double xEnd, double yEnd)
    {
        if (WindowBounds.IsEmpty) return Rectangle.Empty;

        var x = WindowBounds.X + (int)(WindowBounds.Width * xStart);
        var y = WindowBounds.Y + (int)(WindowBounds.Height * yStart);
        var w = (int)(WindowBounds.Width * (xEnd - xStart));
        var h = (int)(WindowBounds.Height * (yEnd - yStart));
        return new Rectangle(x, y, w, h);
    }

    public void Dispose()
    {
        _application?.Dispose();
        _automation.Dispose();
    }
}
