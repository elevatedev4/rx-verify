using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RxVerifyOverlay.Integrated;

/// <summary>
/// Custom always-on-top hover-detail popup — see the XAML header doc for
/// why this replaces WPF's own ToolTipService entirely (fix/hover-popup-live
/// branch). Owned/driven exclusively by IntegratedBoxesWindow's poll timer
/// (via ShowFor/HidePopup, called from HoverStateMachine's Show/Hide
/// actions) — nothing in this class reacts to mouse/keyboard input itself,
/// which is exactly the point: it can never depend on the same WPF
/// input-event plumbing that turned out unreliable for the tooltip/context
/// menu it replaces.
/// </summary>
public sealed partial class HoverPopupWindow : Window
{
    // Same click-through/no-activate/no-alt-tab styles as
    // IntegratedBoxesWindow — see that class's own doc for what each
    // flag does. UNLIKE IntegratedBoxesWindow, WS_EX_TRANSPARENT here is
    // applied ONCE and never toggled: this window has no interactive
    // content, ever, so there's nothing that would need it temporarily
    // cleared.
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    // ------------------------------------------------------------------
    // SCREEN-BOUNDS CLAMPING (owner follow-up, RXVERIFY-TROUBLESHOOT
    // 2026-08): ShowFor originally positioned the popup at cursor+offset
    // with no bounds check — hovering a verdict bar near a monitor's
    // right/bottom edge could push the popup partially or entirely
    // off-screen. MonitorGeometry (shared with MainWindow's
    // ReportErrorWindow positioning — extracted here rather than each
    // duplicating its own MonitorFromPoint/GetMonitorInfo P/Invokes)
    // finds whichever monitor the CURSOR is currently on; the actual
    // clamp math is PopupBoundsClamp (pure, unit-tested, no Win32
    // dependency).
    // ------------------------------------------------------------------

    /// <summary>How far (in PHYSICAL pixels) the popup's top-left sits from the cursor position ShowFor was given — offset down-and-right so the popup is never directly under the cursor itself (owner's ask: "positioned near the cursor, never under it").</summary>
    private const int CursorOffsetPhysicalPx = 18;

    private IntPtr _hwnd = IntPtr.Zero;

    public HoverPopupWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    /// <summary>
    /// Populates the field content and positions/shows the popup near
    /// <paramref name="cursorPhysical"/> (offset by CursorOffsetPhysicalPx
    /// so it never sits directly under the cursor). PHI CAUTION: values
    /// are shown VERBATIM, same as the ToolTip content this replaces —
    /// on-screen only, never leaves the workstation, see
    /// VerdictFieldInfo's own PHI CAUTION doc.
    ///
    /// Show() first, THEN UpdateLayout(), THEN read ActualWidth/Height —
    /// SizeToContent only resolves a real size once the window has
    /// actually been through a layout pass; reading those properties
    /// before Show()/UpdateLayout() would see stale (often zero) values
    /// from before this call's content change. <paramref name="dpiScaleX"/>/
    /// <paramref name="dpiScaleY"/> are the SAME scale IntegratedBoxesWindow
    /// already has on hand for the monitor Pioneer (and the cursor) is
    /// currently on — reused here rather than re-querying, since the
    /// popup is always shown right next to a cursor position that's
    /// already known to be on that monitor.
    /// </summary>
    public void ShowFor(VerdictFieldInfo field, System.Drawing.Point cursorPhysical, double dpiScaleX, double dpiScaleY)
    {
        FieldNameText.Text = field.DisplayName;
        StatusText.Text = $"Status: {field.Status}";
        SourceText.Text = $"Source: {field.SourceValue}";
        EnteredText.Text = $"Entered: {field.EnteredValue}";
        ExplanationText.Text = field.Explanation;
        ExplanationText.Visibility = string.IsNullOrEmpty(field.Explanation) ? Visibility.Collapsed : Visibility.Visible;

        if (!IsVisible)
        {
            Show();
        }

        UpdateLayout();

        var physicalX = cursorPhysical.X + CursorOffsetPhysicalPx;
        var physicalY = cursorPhysical.Y + CursorOffsetPhysicalPx;
        var physicalWidth = (int)Math.Ceiling(ActualWidth * dpiScaleX);
        var physicalHeight = (int)Math.Ceiling(ActualHeight * dpiScaleY);

        var proposed = new System.Drawing.Rectangle(physicalX, physicalY, physicalWidth, physicalHeight);
        // Full monitor bounds (not just work area) — this popup has no
        // buttons/controls to protect from a taskbar overlap, unlike
        // ReportErrorWindow's positioning (MainWindow.xaml.cs), which
        // asks MonitorGeometry.GetWorkArea instead.
        var monitorBounds = MonitorGeometry.GetMonitorBounds(cursorPhysical);
        var clamped = monitorBounds is { } bounds ? PopupBoundsClamp.Clamp(proposed, bounds) : new System.Drawing.Point(physicalX, physicalY);

        NativeWindowPositioning.Reposition(_hwnd, clamped.X, clamped.Y, physicalWidth, physicalHeight);
    }

    /// <summary>Hides the popup — safe to call even when it's already hidden (WPF's own Hide() is a no-op in that case), which is how IntegratedBoxesWindow calls this on every early-out path rather than tracking its own "is the popup currently shown" flag.</summary>
    public void HidePopup() => Hide();
}
