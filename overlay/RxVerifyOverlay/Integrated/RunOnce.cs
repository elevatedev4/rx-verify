using System;

namespace RxVerifyOverlay.Integrated;

/// <summary>
/// Runs a wrapped action at most once, no matter how many times Fire()
/// is called after the first — pure, no WPF/event dependency, so the
/// "runs exactly once" guarantee itself is directly unit-testable
/// without a live Window/event.
///
/// RXVERIFY-TROUBLESHOOT round 2 review fix: MainWindow.xaml.cs's
/// OpenReportErrorDialog subscribes ReportErrorWindow's Window.DpiChanged
/// to reposition the dialog after a cross-monitor DPI change settles.
/// Left unguarded, that handler re-ran on EVERY subsequent DpiChanged
/// firing — including one Will causes himself by manually dragging the
/// ALREADY-OPEN dialog across a DPI boundary to a second monitor — which
/// snapped the window back toward the ORIGINAL right-click location, the
/// exact "snap-back on drag" failure this fix closes. The actual
/// guarantee is a self-removing DpiChanged handler (unsubscribes itself
/// on first firing — see that call site); this class is the
/// belt-and-suspenders second layer of the same "at most once" property,
/// same "layer 3" defensive-layering posture as
/// IntegratedBoxesWindow.ForceHitTestTransparent elsewhere in this
/// codebase — and gives that guarantee its own WPF-free unit test
/// (RunOnceTests) independent of whatever the real Window/event does.
/// </summary>
public sealed class RunOnce
{
    private readonly Action _action;
    private bool _hasFired;

    public RunOnce(Action action) => _action = action;

    /// <summary>Invokes the wrapped action on the first call; a no-op on every call after that.</summary>
    public void Fire()
    {
        if (_hasFired) return;
        _hasFired = true;
        _action();
    }
}
