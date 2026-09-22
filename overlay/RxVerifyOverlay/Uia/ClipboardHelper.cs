using System;
using System.Threading;
using System.Windows;

namespace RxVerifyOverlay.Uia;

/// <summary>
/// Shared "set clipboard text, with retries" helper.
///
/// Hoisted out of MainWindow.xaml.cs's OnCopyLogsNoHipaaClick path
/// (originally TrySetClipboardText, private to that file) so
/// PioneerReportDriver.ReplayReportParameterKeyPlan can reuse the exact
/// same retry behavior for the Round 5 W-T92 follow-up fix: Report
/// Parameters date entry now pastes (Ctrl+V) instead of typing digit
/// keystrokes, because FlaUI's Keyboard.Type of plain digits was landing
/// as numpad/scan-code input in Pioneer's masked date field (numpad 2 ==
/// Down when NumLock is off) rather than real top-row digits.
///
/// WPF's System.Windows.Clipboard requires an STA thread.
/// MainWindow.xaml.cs's own callers always run on the WPF UI thread
/// already (itself STA), so this just runs inline there. But
/// PioneerReportDriver's report loop (ReportsCoordinator.RunReportsAsync
/// awaits every driver call with ConfigureAwait(false)) can land
/// ReplayReportParameterKeyPlan on an ordinary thread-pool (MTA) thread —
/// TrySetText below detects that case and marshals the actual
/// Clipboard.SetText call onto a dedicated one-shot STA thread rather than
/// assuming a WPF Dispatcher is reachable from here (this class doesn't
/// hold a reference to any Window/Application, and Application.Current can
/// legitimately be null, e.g. in the xunit test host).
/// </summary>
public static class ClipboardHelper
{
    /// <summary>Round 5 review fix (blocking issue): the dedicated STA thread's Join below is bounded by this - a wedged Clipboard.SetText (e.g. another process holding the clipboard open indefinitely) must not hang the whole report run forever. TrySetTextCore's own 3-attempt retry loop is at most ~100ms of Thread.Sleep plus however long the underlying COM calls take, so 3s is generous headroom, not a tight race.</summary>
    private static readonly TimeSpan StaJoinTimeout = TimeSpan.FromSeconds(3);

    public static bool TrySetText(string text)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            return TrySetTextCore(text);
        }

        var result = false;
        var staThread = new Thread(() => result = TrySetTextCore(text))
        {
            IsBackground = true
        };
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();

        // Round 5 review fix: an unbounded Join here meant a wedged
        // Clipboard.SetText could hang the entire report run with no
        // bail-out. A timed-out join is treated as a TrySetText failure -
        // ReplayReportParameterKeyPlan's PasteText case already aborts
        // cleanly (never sends F12) when this returns false. IsBackground
        // above means an actually-stuck thread can't keep the process
        // alive either way.
        if (!staThread.Join(StaJoinTimeout))
        {
            return false;
        }

        return result;
    }

    /// <summary>
    /// Clipboard.SetText occasionally throws COMException/"clipboard could
    /// not be opened" when another process (clipboard manager, etc.) is
    /// briefly holding it — a well-known WPF clipboard gotcha, not specific
    /// to this app. A few short retries clears the vast majority of those
    /// transient failures without the pharmacist (or, here, the report
    /// automation) ever noticing.
    /// </summary>
    private static bool TrySetTextCore(string text)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (Exception) when (attempt < 2)
            {
                Thread.Sleep(50);
            }
            catch (Exception)
            {
                return false;
            }
        }

        return false;
    }
}
