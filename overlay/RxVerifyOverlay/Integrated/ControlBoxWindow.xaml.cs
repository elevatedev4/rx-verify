using System;
using System.Runtime.InteropServices;
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
    // REVIEW FIX (focus-stealing): same extended-style mechanism as
    // IntegratedBoxesWindow's click-through styles, minus
    // WS_EX_TRANSPARENT/WS_EX_LAYERED — this window DOES need to receive
    // mouse clicks (unlike the boxes layer), it just must never take
    // keyboard focus/activation away from PioneerRx. WS_EX_TOOLWINDOW
    // also keeps it out of Alt-Tab (redundant with ShowInTaskbar="False"
    // for the taskbar itself, but Alt-Tab is a separate list).
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    // ------------------------------------------------------------------
    // ITEM 2: global `\` hotkey, toggling the SAME state as
    // HideOverlayCheckBox. RegisterHotKey is process-wide (not just this
    // window) — the owner has been told `\` becomes system-reserved while
    // the app runs, and accepted that. MOD_NOREPEAT stops a held-down key
    // from firing the toggle repeatedly (Windows 7+; well within this
    // app's existing Win10-2004+ floor). DEGRADE ON FAILURE: if
    // RegisterHotKey returns false (another app already claimed `\` as a
    // global hotkey), _hotkeyRegistered stays false and WM_HOTKEY simply
    // never arrives for our id — the checkbox keeps working completely
    // normally either way, since it never depends on the hotkey having
    // registered. No error surfaced to the pharmacist: a failed global
    // hotkey registration isn't something they can act on, and the
    // checkbox is a full substitute.
    // ------------------------------------------------------------------
    private const int WM_HOTKEY = 0x0312;
    private const int HideOverlayHotkeyId = 1;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkOem5 = 0xDC; // the "\|" key on a US keyboard

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private bool _hotkeyRegistered;

    /// <summary>Held so the Closed handler can explicitly remove the WM_HOTKEY hook (item 8 — "hook removal shouldn't throw on exit"), belt-and-suspenders alongside WPF's own teardown of the HwndSource when the window closes.</summary>
    private HwndSource? _hwndSource;

    // Suppresses the Checked/Unchecked handlers while SetToggleState (or
    // the hotkey handler, for HideOverlayCheckBox specifically) is
    // programmatically syncing these controls to the current state —
    // mirrors MainWindow.xaml.cs's _suppressMethodChangeHandler pattern
    // exactly: only a REAL pharmacist click (or the hotkey, for the
    // checkbox) should ever raise the corresponding *Requested event.
    private bool _suppressToggleHandlers;
    private bool _suppressHideOverlayHandler;

    /// <summary>Same suppression pattern as _suppressHideOverlayHandler, for the Mode dropdown — see SetOrderAssistState.</summary>
    private bool _suppressOrderAssistHandler;

    /// <summary>
    /// Mirrors OverlaySettings.OrderAssistEnabled — set by SetOrderAssistState,
    /// read by SetMaximizedGuardState. Needed there because the
    /// "maximize PioneerRx to use integrated view" note is sized/laid out
    /// for the FULL Verify-mode box; showing it over the tiny Order-mode
    /// CompactOrderPanel would visually break that small box, and Order
    /// mode has no toggles for it to grey out anyway.
    /// </summary>
    private bool _isOrderModeActive;

    private IntPtr _hwnd = IntPtr.Zero;

    public event EventHandler<VerificationMethod>? MethodChangeRequested;
    public event EventHandler<DisplayMode>? DisplayModeChangeRequested;
    // 2026-08-13 (RXVERIFY-TROUBLESHOOT): CopyLogsRequested (backed the
    // PHI-including "Copy" button) removed along with the button itself
    // -- see the XAML. CopyLogsNoHipaaRequested (the sanitized "Copy
    // (safe)" button) is the only copy-logs event now.
    public event EventHandler<Button>? CopyLogsNoHipaaRequested;
    public event EventHandler? OpenSeparateWindowRequested;
    public event EventHandler? RefreshRequested;

    /// <summary>Item 8: the corner X button — MainWindow.xaml.cs handles this by calling its own Close(), routing through its EXISTING Closed cleanup (engine/watcher dispose, IntegratedOverlayCoordinator.Shutdown(), Application.Current.Shutdown()) rather than duplicating any of that here.</summary>
    public event EventHandler? CloseApplicationRequested;

    /// <summary>
    /// Item 2: raised with the NEW hidden state whenever HideOverlayCheckBox
    /// is clicked directly, or the `\` hotkey fires (which flips the
    /// checkbox itself first — see OnHotkeyPressed — so this always
    /// carries the checkbox's own current, authoritative state).
    /// </summary>
    public event EventHandler<bool>? HideOverlayToggleRequested;

    /// <summary>
    /// Raised with the NEW mode state (true = Order, false = Verify)
    /// whenever the pharmacist picks a DIFFERENT item in either Mode
    /// dropdown (ModeComboBoxNormal or ModeComboBoxCompact — never for a
    /// programmatic SetOrderAssistState sync — see _suppressOrderAssistHandler).
    /// Kept as a plain bool (not a new Mode enum) — same event name and
    /// shape as the checkbox this replaced — so IntegratedOverlayCoordinator/
    /// MainWindow.xaml.cs's existing wiring needs no changes:
    /// IntegratedOverlayCoordinator only relays this as a plain bool (see
    /// its own OrderAssistToggleRequested doc) — it never references any
    /// OrderAssist.* type itself, keeping this window's only coupling to
    /// that module a bool in each direction.
    /// </summary>
    public event EventHandler<bool>? OrderAssistToggleRequested;

    public ControlBoxWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
    }

    /// <summary>
    /// Item 8 ("shutdown runs cleanly ... shouldn't throw on exit"):
    /// unregisters the global hotkey and removes the WM_HOTKEY hook.
    /// Wrapped — a P/Invoke or WPF teardown hiccup here must never
    /// prevent the rest of the app's own shutdown sequence (MainWindow's
    /// Closed handler, which is what actually calls this indirectly via
    /// IntegratedOverlayCoordinator.Shutdown() -&gt; _controlBox.Close())
    /// from completing.
    /// </summary>
    private void OnClosed(object? sender, EventArgs e)
    {
        try
        {
            if (_hotkeyRegistered) UnregisterHotKey(_hwnd, HideOverlayHotkeyId);
            _hwndSource?.RemoveHook(WndProc);
        }
        catch
        {
            // Best-effort only — see method doc.
        }
    }

    /// <summary>See the WS_EX_NOACTIVATE/WS_EX_TOOLWINDOW field doc above and ShowActivated="False" in the XAML — together these mean Show()/RepositionPhysical never steal focus from PioneerRx. Also registers the item-2 global hotkey and hooks WM_HOTKEY (see those fields' docs).</summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

        _hotkeyRegistered = RegisterHotKey(_hwnd, HideOverlayHotkeyId, ModNoRepeat, VkOem5);
        // No status surfaced on failure — see the field doc above: the
        // checkbox is a complete substitute either way.

        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HideOverlayHotkeyId)
        {
            OnHotkeyPressed();
            handled = true;
        }

        return IntPtr.Zero;
    }

    /// <summary>Flips HideOverlayCheckBox itself (so it and the hotkey can never visually disagree), then raises HideOverlayToggleRequested with the checkbox's new, authoritative state — same single event OnHideOverlayToggled raises for a direct click.</summary>
    private void OnHotkeyPressed()
    {
        _suppressHideOverlayHandler = true;
        try
        {
            HideOverlayCheckBox.IsChecked = !(HideOverlayCheckBox.IsChecked == true);
        }
        finally
        {
            _suppressHideOverlayHandler = false;
        }

        HideOverlayToggleRequested?.Invoke(this, HideOverlayCheckBox.IsChecked == true);
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

    /// <summary>
    /// Updates the compact status line — a "Checked h:mm:ss" style status/
    /// timing message. Plain text (no ViewModel/DataContext binding — this
    /// window intentionally has neither, per its doc above).
    ///
    /// Owner request (2026-08-13): "Remove the counter showing the
    /// accurate/errors. that is not needed on the top right box." This
    /// used to be SetStatusSummary(summaryText, statusText), also setting
    /// StatusSummaryText to the "N✓ M✗" glyph counter
    /// (IntegratedOverlayCoordinator.BuildStatusSummary, now removed) —
    /// that element and parameter are both gone; this method now only
    /// ever sets the status/timing message.
    /// </summary>
    public void SetStatusMessage(string statusText)
    {
        StatusTimeText.Text = statusText;
    }

    /// <summary>
    /// REVIEW FIX (Will's live test, W-T75, items 2/4 — root cause):
    /// this panel-swap logic used to live directly inside SetOrderAssistState,
    /// which is ONLY ever called from IntegratedOverlayCoordinator.SyncToggles
    /// (construction + DisplayMode changes) and, defensively, from
    /// UpdateControlBox every reposition tick — but NEVER from the actual
    /// live Mode-dropdown-toggle path: MainWindow.xaml.cs's own
    /// OrderAssistToggleRequested handler only persists
    /// OverlaySettings.OrderAssistEnabled and starts/stops the
    /// OrderAssistCoordinator timer — it never calls SyncToggles()/Tick()
    /// back into this window. Result: OverlaySettings.OrderAssistEnabled
    /// (what UpdateControlBox reads for the window's PHYSICAL size) flipped
    /// true the instant the pharmacist picked "Mode: Order", shrinking the
    /// window, while NormalPanel (with its live "Waiting for a
    /// PioneerRx..." status text — see OverlayViewModel.StatusMessage) never
    /// actually hid, since nothing had told THIS window its own mode
    /// changed. Exactly Will's report: "the box shrinks and hides the
    /// content because it's still showing 'Waiting for a prescription to
    /// pre-check'" — both the Mode dropdown and the Verify escape Button
    /// were underneath/behind that still-visible NormalPanel content,
    /// leaving him stuck.
    ///
    /// THE FIX: extracted here as the SOLE place any of this window's own
    /// content elements' mode-dependent visibility gets applied — called
    /// from THREE paths now, none of which can be forgotten without
    /// breaking a build-visible contract:
    ///   1. SetOrderAssistState (external sync — SyncToggles/UpdateControlBox)
    ///   2. OnModeComboBoxChanged (a REAL pharmacist dropdown pick — applied
    ///      IMMEDIATELY, before the event even leaves this window, so the
    ///      swap can never depend on any relay/round-trip elsewhere)
    ///   3. OnVerifyEscapeButtonClick (same immediacy, for the escape Button)
    /// The actual which-panels-visible decision is
    /// ControlBoxModeLayoutRule.Resolve (pure, tested) — this method is
    /// just the WPF-applying wrapper around it.
    /// </summary>
    private void ApplyModeLayout(bool orderModeActive)
    {
        var layout = ControlBoxModeLayoutRule.Resolve(orderModeActive);

        _isOrderModeActive = orderModeActive;

        _suppressOrderAssistHandler = true;
        try
        {
            ModeComboBoxNormal.SelectedIndex = layout.ModeComboBoxSelectedIndex;
            ModeComboBoxCompact.SelectedIndex = layout.ModeComboBoxSelectedIndex;
        }
        finally
        {
            _suppressOrderAssistHandler = false;
        }

        NormalPanel.Visibility = layout.ShowNormalPanel ? Visibility.Visible : Visibility.Collapsed;
        CompactOrderPanel.Visibility = layout.ShowCompactOrderPanel ? Visibility.Visible : Visibility.Collapsed;
        CloseButton.Visibility = layout.ShowCloseButton ? Visibility.Visible : Visibility.Collapsed;

        // The maximized-guard note is sized/laid out for the FULL
        // Verify-mode box and has nothing to grey out in Order mode
        // anyway (CompactOrderPanel has no Method/Display toggles) — see
        // SetMaximizedGuardState, which now also checks _isOrderModeActive
        // directly; this call just makes sure a note left showing from
        // BEFORE Order mode was chosen doesn't linger over the tiny box.
        if (orderModeActive) MaximizeNoteBorder.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// External sync entry point — reflects OverlaySettings.OrderAssistEnabled
    /// via ApplyModeLayout without re-raising OrderAssistToggleRequested.
    /// Called from IntegratedOverlayCoordinator.SyncToggles (construction +
    /// DisplayMode changes) AND, defensively, from UpdateControlBox on
    /// EVERY reposition tick (see that method's own doc) — so even if a
    /// future code path somehow drifts this window's visible mode away
    /// from the persisted setting, it self-heals within one tick. See
    /// ApplyModeLayout's own doc for the W-T75 bug this whole design fixes.
    /// </summary>
    public void SetOrderAssistState(bool enabled) => ApplyModeLayout(enabled);

    /// <summary>
    /// MAXIMIZED-ONLY guard (item 3): when PioneerRx is attached but NOT
    /// maximized, show the "maximize to use integrated view" note and
    /// grey every toggle except the one that switches back to Separate —
    /// the pharmacist must always be able to escape back to the classic
    /// window without maximizing first. Suppressed entirely while Order
    /// mode is active (see _isOrderModeActive's doc) — the tiny
    /// CompactOrderPanel has no room for this note and no toggles to grey.
    /// </summary>
    public void SetMaximizedGuardState(bool isMaximized)
    {
        MaximizeNoteBorder.Visibility = (!isMaximized && !_isOrderModeActive) ? Visibility.Visible : Visibility.Collapsed;
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

    private void OnCopyLogsNoHipaaClick(object sender, RoutedEventArgs e) => CopyLogsNoHipaaRequested?.Invoke(this, (Button)sender);

    private void OnOpenSeparateClick(object sender, RoutedEventArgs e) => OpenSeparateWindowRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Item 1: same Refresh action as the separate window's own Refresh button — MainWindow.xaml.cs handles this identically (SafeRefreshAsync + SafeTickIntegratedOverlay) via _integratedOverlay.RefreshRequested.</summary>
    private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Item 2: a REAL pharmacist click on the checkbox (not the hotkey — see OnHotkeyPressed, and not SetToggleState-style programmatic sync, which none exists for this checkbox since there's only one UI surface for it).</summary>
    private void OnHideOverlayToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressHideOverlayHandler) return;
        HideOverlayToggleRequested?.Invoke(this, HideOverlayCheckBox.IsChecked == true);
    }

    /// <summary>Item 8: the corner X button — see CloseApplicationRequested's doc.</summary>
    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseApplicationRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// A REAL pharmacist selection change on EITHER Mode dropdown (not
    /// ApplyModeLayout's own programmatic re-sync of these SAME combo
    /// boxes — see _suppressOrderAssistHandler) — both ModeComboBoxNormal
    /// (Verify-mode row) and ModeComboBoxCompact (Order-mode's only
    /// content) wire to this SAME handler in XAML, since exactly one of
    /// the two is ever visible/interactive at a time (see ApplyModeLayout's
    /// panel swap) and both mean the same thing: index 0 = Mode: Verify,
    /// index 1 = Mode: Order.
    ///
    /// REVIEW FIX (Will's live test, W-T75 — root cause): applies
    /// ApplyModeLayout to THIS window IMMEDIATELY, before raising
    /// OrderAssistToggleRequested at all — see that method's own doc for
    /// why waiting on the event to round-trip back through
    /// IntegratedOverlayCoordinator/MainWindow.xaml.cs (which never
    /// actually synced it back here) left the pharmacist stuck looking at
    /// a shrunk box still showing full Verify-mode content.
    /// </summary>
    private void OnModeComboBoxChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressOrderAssistHandler) return;
        var isOrderMode = ((ComboBox)sender).SelectedIndex == 1;
        ApplyModeLayout(isOrderMode);
        OrderAssistToggleRequested?.Invoke(this, isOrderMode);
    }

    /// <summary>
    /// REVIEW FIX (pre-merge, 2026-08-14 — escape-hatch hardening): see
    /// VerifyEscapeButton's own XAML doc. Raises the EXACT SAME event
    /// with the EXACT SAME value (false = Verify) as picking "Mode:
    /// Verify" in ModeComboBoxCompact — IntegratedOverlayCoordinator/
    /// MainWindow.xaml.cs can't tell the two apart, and don't need to.
    /// This is a plain Button (the control type already proven reliable
    /// on this WS_EX_NOACTIVATE window), so it works regardless of
    /// whether the ComboBox's own dropdown popup does.
    ///
    /// REVIEW FIX (Will's live test, W-T75 — items 3/4, defensive): also
    /// applies ApplyModeLayout to THIS window IMMEDIATELY, same as
    /// OnModeComboBoxChanged above and for the same reason — this Button
    /// exists specifically to be Will's escape hatch if the ComboBox
    /// itself ever misbehaves, so it must not depend on any relay
    /// round-trip to actually restore this window's own content either.
    /// </summary>
    private void OnVerifyEscapeButtonClick(object sender, RoutedEventArgs e)
    {
        ApplyModeLayout(orderModeActive: false);
        OrderAssistToggleRequested?.Invoke(this, false);
    }
}
