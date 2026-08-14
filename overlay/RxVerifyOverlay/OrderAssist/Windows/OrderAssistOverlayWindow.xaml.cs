using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using RxVerifyOverlay.Integrated;

namespace RxVerifyOverlay.OrderAssist.Windows;

/// <summary>
/// Order Assist's own click-through highlight layer — red boxes over
/// zero-quantity cells (Create Recommended Orders window) OR a single
/// green box + "% savings" label over the recommended substitution row
/// (Catalog Item Substitution Selection window). The two target windows
/// are never open at the same time (see OrderAssistWindowClassifier), so
/// in practice only one of those two modes is ever populated at once —
/// but nothing here assumes that; SetHighlights just draws whatever it's
/// given.
///
/// DELIBERATELY its own window, entirely separate from Integrated/
/// IntegratedBoxesWindow — this module must stay fully decoupled from the
/// verify flow (see OrderAssistCoordinator's class doc). It reuses only
/// the same GENERIC, verify-agnostic positioning helper
/// (Integrated/NativeWindowPositioning.cs — plain SetWindowPos wrappers
/// with no field/verdict-specific knowledge at all) rather than any of
/// IntegratedBoxesWindow's own field/hover/report-error code.
///
/// CLICK-THROUGH: unlike IntegratedBoxesWindow, this window has NO hover
/// affordance at all — always fully click-through (WS_EX_TRANSPARENT
/// stays set permanently, never toggled), no poll timer, no hotspots.
/// The spec only calls for a passive glance-highlight while the
/// pharmacist works the order screens, never an interactive one. Same
/// WS_EX discipline as IntegratedBoxesWindow otherwise (WS_EX_LAYERED so
/// WS_EX_TRANSPARENT takes effect, WS_EX_NOACTIVATE so Show()/reposition
/// never steals focus from PioneerRx, WS_EX_TOOLWINDOW to stay out of
/// Alt-Tab) and the same "force-transparent before every Show(), clear
/// all state on every Hide()" lessons that window's own doc calls out.
/// </summary>
public sealed partial class OrderAssistOverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    // Filled (not just outlined) boxes at low opacity — legible at a
    // glance without obscuring the OCR'd text/numbers underneath. The
    // green row gets a solid border on top of its fill so it reads as
    // "this whole row" distinctly from a plain red cell fill.
    private static readonly SolidColorBrush RedFillBrush = new(Color.FromArgb(0x55, 0xC6, 0x28, 0x28));
    private static readonly SolidColorBrush GreenFillBrush = new(Color.FromArgb(0x33, 0x2E, 0x7D, 0x32));
    private static readonly SolidColorBrush GreenBorderBrush = new(Color.FromRgb(0x2E, 0x7D, 0x32));
    private const double GreenBorderThickness = 3;

    private IntPtr _hwnd = IntPtr.Zero;

    public OrderAssistOverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        ForceHitTestTransparent();
    }

    /// <summary>Always click-through — see class doc. A no-op until the native HWND exists; called again right before every Show() (same belt-and-suspenders posture as IntegratedBoxesWindow.ForceHitTestTransparent) so this window can never surface non-transparent regardless of state history.</summary>
    public void ForceHitTestTransparent()
    {
        if (_hwnd == IntPtr.Zero) return;
        var exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    /// <summary>See NativeWindowPositioning.Reposition — physical pixels, matching the target Pioneer window's own bounds exactly.</summary>
    public void RepositionPhysical(int x, int y, int width, int height) => NativeWindowPositioning.Reposition(_hwnd, x, y, width, height);

    /// <summary>See NativeWindowPositioning.MakeTopmost — call once, right after the first Show().</summary>
    public void EnsureTopmost() => NativeWindowPositioning.MakeTopmost(_hwnd);

    /// <summary>
    /// Rebuilds the highlight layer from scratch every call (small element
    /// count every tick — at most a handful of zero-cells or a single row
    /// — so a full rebuild is simplest-correct, same posture as
    /// IntegratedBoxesWindow.SetBoxes). <paramref name="redBoxesDip"/> is
    /// every zero-quantity cell this tick; <paramref name="greenRowDip"/>/
    /// <paramref name="savingsLabel"/> are the recommended substitution
    /// row and its "% savings" text. Both DIP rects are already relative
    /// to THIS window's own top-left (see OrderAssistCoordinator's
    /// coordinate-conversion doc) — ready to assign straight to
    /// Canvas.Left/Top.
    /// </summary>
    public void SetHighlights(IReadOnlyList<DipRect> redBoxesDip, DipRect? greenRowDip, string? savingsLabel)
    {
        HighlightCanvas.Children.Clear();

        foreach (var box in redBoxesDip)
        {
            AddRect(box, RedFillBrush, borderBrush: null);
        }

        if (greenRowDip is { } row)
        {
            AddRect(row, GreenFillBrush, GreenBorderBrush);

            if (!string.IsNullOrWhiteSpace(savingsLabel))
            {
                AddSavingsLabel(row, savingsLabel!);
            }
        }
    }

    /// <summary>Clears every highlight, THEN hides — call instead of a bare Hide() everywhere this window is hidden, so a stale highlight from the previous tick/target-window never lingers into whatever this window gets repositioned over next (same "clear state on hide" lesson as IntegratedBoxesWindow.HideAndResetHover).</summary>
    public void HideAndClear()
    {
        HighlightCanvas.Children.Clear();
        Hide();
    }

    private void AddRect(DipRect dip, Brush fill, Brush? borderBrush)
    {
        var border = new Border
        {
            Width = Math.Max(0, dip.Width),
            Height = Math.Max(0, dip.Height),
            Background = fill,
            BorderBrush = borderBrush,
            BorderThickness = borderBrush is null ? new Thickness(0) : new Thickness(GreenBorderThickness),
            IsHitTestVisible = false
        };

        Canvas.SetLeft(border, dip.X);
        Canvas.SetTop(border, dip.Y);
        HighlightCanvas.Children.Add(border);
    }

    private void AddSavingsLabel(DipRect rowDip, string text)
    {
        var label = new Border
        {
            Background = GreenBorderBrush,
            Padding = new Thickness(4, 1, 4, 1),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                FontSize = 11,
                FontWeight = FontWeights.Bold
            }
        };

        // A small badge anchored just to the right of the highlighted
        // row, vertically centered on it — not another full-width bar.
        Canvas.SetLeft(label, rowDip.X + rowDip.Width + 4);
        Canvas.SetTop(label, rowDip.Y + Math.Max(0, (rowDip.Height - 16) / 2.0));
        HighlightCanvas.Children.Add(label);
    }
}
