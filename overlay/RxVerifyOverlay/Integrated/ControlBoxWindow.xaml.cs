using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using RxVerifyOverlay.Models;

namespace RxVerifyOverlay.Integrated;

/// <summary>
/// The small interactive control panel for INTEGRATED display mode (item
/// 2) — status summary, Method/Display-mode toggles, copy-logs buttons,
/// and an "open full view" escape hatch back to MainWindow. UNLIKE
/// IntegratedBoxesWindow this is a normal (non-click-through) window: it
/// must receive clicks. Purely event-driven — it never touches
/// OverlaySettings/OverlayViewModel/EngineClient directly; every action
/// is raised as an event and handled by
/// Integrated/IntegratedOverlayCoordinator.cs (Method/DisplayMode
/// changes) or forwarded further to MainWindow.xaml.cs (copy-logs
/// clipboard work, opening the separate window) — same separation
/// OverlayViewModel already keeps from the UI layer.
/// </summary>
public sealed partial class ControlBoxWindow : Window
{
    // Suppresses the Checked handlers while SetToggleState is
    // programmatically syncing these radio buttons to the current
    // settings — mirrors MainWindow.xaml.cs's _suppressMethodChangeHandler
    // pattern exactly: only a REAL pharmacist click should ever raise
    // MethodChangeRequested/DisplayModeChangeRequested.
    private bool _suppressToggleHandlers;

    private IntPtr _hwnd = IntPtr.Zero;

    public event EventHandler<VerificationMethod>? MethodChangeRequested;
    public event EventHandler<DisplayMode>? DisplayModeChangeRequested;
    public event EventHandler<Button>? CopyLogsRequested;
    public event EventHandler<Button>? CopyLogsNoHipaaRequested;
    public event EventHandler? OpenSeparateWindowRequested;

    public ControlBoxWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => _hwnd = new WindowInteropHelper(this).Handle;
    }

    /// <summary>See NativeWindowPositioning.Reposition — physical pixels, anchored relative to PioneerRx's window bounds (see IntegratedOverlayCoordinator's ControlBoxRightInsetDip/ControlBoxTopOffsetDip).</summary>
    public void RepositionPhysical(int x, int y, int width, int height) => NativeWindowPositioning.Reposition(_hwnd, x, y, width, height);

    /// <summary>Reflects current settings in the toggles without re-raising the *ChangeRequested events — called whenever the coordinator syncs this window's UI to OverlaySettings (e.g. right after MainWindow's own toggle changed the same setting).</summary>
    public void SetToggleState(VerificationMethod method, DisplayMode displayMode)
    {
        _suppressToggleHandlers = true;
        try
        {
            MethodOcrRadioButton.IsChecked = method == VerificationMethod.Ocr;
            MethodUiaRadioButton.IsChecked = method == VerificationMethod.Uia;
            DisplayIntegratedRadioButton.IsChecked = displayMode == DisplayMode.Integrated;
            DisplaySeparateRadioButton.IsChecked = displayMode == DisplayMode.Separate;
        }
        finally
        {
            _suppressToggleHandlers = false;
        }
    }

    /// <summary>Updates the compact status line — e.g. "11✓ 2✗" plus a "Checked h:mm:ss" style status message. Plain text (no ViewModel/DataContext binding — this window intentionally has neither, per its doc above).</summary>
    public void SetStatusSummary(string summaryText, string statusText)
    {
        StatusSummaryText.Text = summaryText;
        StatusTimeText.Text = statusText;
    }

    /// <summary>
    /// MAXIMIZED-ONLY guard (item 3): when PioneerRx is attached but NOT
    /// maximized, show the "maximize to use integrated view" note and
    /// grey every toggle except the one that switches back to Separate —
    /// the pharmacist must always be able to escape back to the classic
    /// window without maximizing first.
    /// </summary>
    public void SetMaximizedGuardState(bool isMaximized)
    {
        MaximizeNoteBorder.Visibility = isMaximized ? Visibility.Collapsed : Visibility.Visible;
        MethodOcrRadioButton.IsEnabled = isMaximized;
        MethodUiaRadioButton.IsEnabled = isMaximized;
        DisplayIntegratedRadioButton.IsEnabled = isMaximized;
        DisplaySeparateRadioButton.IsEnabled = true;
    }

    private void OnMethodChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressToggleHandlers) return;
        var method = MethodUiaRadioButton.IsChecked == true ? VerificationMethod.Uia : VerificationMethod.Ocr;
        MethodChangeRequested?.Invoke(this, method);
    }

    private void OnDisplayModeChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressToggleHandlers) return;
        var mode = DisplayIntegratedRadioButton.IsChecked == true ? DisplayMode.Integrated : DisplayMode.Separate;
        DisplayModeChangeRequested?.Invoke(this, mode);
    }

    private void OnCopyLogsClick(object sender, RoutedEventArgs e) => CopyLogsRequested?.Invoke(this, (Button)sender);

    private void OnCopyLogsNoHipaaClick(object sender, RoutedEventArgs e) => CopyLogsNoHipaaRequested?.Invoke(this, (Button)sender);

    private void OnOpenSeparateClick(object sender, RoutedEventArgs e) => OpenSeparateWindowRequested?.Invoke(this, EventArgs.Empty);
}
