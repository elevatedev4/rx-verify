namespace RxVerifyOverlay.Integrated;

/// <summary>
/// Which of ControlBoxWindow's own content elements should be visible for
/// a given Mode (Verify/Order) — see ControlBoxWindow.ApplyModeLayout,
/// the SOLE place this is applied to the actual WPF elements.
/// </summary>
public readonly record struct ControlBoxModeLayout(bool ShowNormalPanel, bool ShowCompactOrderPanel, bool ShowCloseButton, int ModeComboBoxSelectedIndex);

/// <summary>
/// Pure decision behind the compact Order-mode box's content — see
/// ControlBoxWindow.ApplyModeLayout's own doc for the bug this fixes
/// (Will's live test, W-T75: "the box shrinks and hides the content
/// because it's still showing 'Waiting for a prescription to pre-check'
/// on the order screen"). The compact box's content must be EXACTLY the
/// Mode dropdown + Verify escape Button, nothing else — this rule is the
/// single source of truth for which of ControlBoxWindow's four top-level
/// content elements (NormalPanel, CompactOrderPanel, CloseButton, and —
/// separately, via ControlBoxWindow's own _isOrderModeActive field feeding
/// SetMaximizedGuardState — MaximizeNoteBorder) are visible for a given
/// mode, kept as a plain bool-in/struct-out function so it's covered by
/// fast xUnit tests, same pattern as VerifyModeGate and every other pure
/// decision class in this app (MainWindowAnchorRule, IntegratedVisibilityGate).
/// </summary>
public static class ControlBoxModeLayoutRule
{
    /// <summary>
    /// Order mode: ONLY CompactOrderPanel (Mode dropdown + Verify escape
    /// Button) shows — NormalPanel (which owns the verify status text,
    /// Method/Display toggles, and action buttons) and CloseButton (no
    /// room in the tiny compact box; the escape Button covers "stuck in
    /// Order mode") are both hidden outright, not just visually behind
    /// something else. Verify mode is the exact inverse — the original,
    /// unchanged full layout.
    /// </summary>
    public static ControlBoxModeLayout Resolve(bool orderModeActive) => new(
        ShowNormalPanel: !orderModeActive,
        ShowCompactOrderPanel: orderModeActive,
        ShowCloseButton: !orderModeActive,
        ModeComboBoxSelectedIndex: orderModeActive ? 1 : 0);
}
