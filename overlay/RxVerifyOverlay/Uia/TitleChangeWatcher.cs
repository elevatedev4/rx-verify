using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace RxVerifyOverlay.Uia;

/// <summary>
/// EVENT-DRIVEN half of detection (latency fix — field report: PioneerRx
/// opening a new "Pre-Check Rx - &lt;number&gt; - ..." window wasn't
/// noticed as fast as it should be). Wraps SetWinEventHook to fire
/// almost immediately whenever ANY top-level window's title changes,
/// instead of waiting for the next 250ms poll tick
/// (MainWindow.xaml.cs's _autoRefreshTimer) to notice.
///
/// NOT A REPLACEMENT FOR THE POLL: SetWinEventHook is not a documented
/// guarantee that every title change is observed (some apps update a
/// title through a path that doesn't raise the accessibility
/// notification a screen reader would rely on), and installing the hook
/// itself can fail (e.g. a locked-down security context). TryStart()
/// returning false — or the hook simply never firing — must degrade to
/// exactly the poll-only behavior this app already had; see
/// MainWindow.xaml.cs, which starts the 250ms poll timer unconditionally
/// regardless of whether this hook installs.
///
/// DELEGATE ROOTING: SetWinEventHook only receives a native function
/// pointer marshaled from a managed delegate — nothing keeps the managed
/// delegate OBJECT itself alive afterward. Without holding a reference
/// to it ourselves, the GC could collect it at any point, after which
/// the OS would be calling into freed memory the next time the event
/// fires. _procDelegate exists solely to root it for this watcher's
/// whole lifetime.
/// </summary>
public sealed class TitleChangeWatcher : IDisposable
{
    private const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002; // don't react to this overlay's own window retitling itself (e.g. UpdateMethodBadge) — it's never a PioneerRx transition
    private const int OBJID_WINDOW = 0;
    private const int CHILDID_SELF = 0;

    private delegate void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    // See class doc "DELEGATE ROOTING" — must be a field, not a local,
    // and must be assigned exactly once (the same instance passed to
    // SetWinEventHook must be the one still referenced when the OS calls
    // it later).
    private readonly WinEventProc _procDelegate;

    private readonly DispatcherTimer _debounceTimer;
    private readonly Action _onTitleChanged;

    private IntPtr _hook = IntPtr.Zero;
    private bool _disposed;

    /// <param name="onTitleChanged">
    /// Invoked on the Dispatcher (UI) thread, at most once per ~50ms
    /// burst of title-change notifications (see "DEBOUNCE" below).
    /// Callers should pass the SAME change-detection path the poll timer
    /// already uses (MainWindow.xaml.cs SafeWatchAsync ->
    /// OverlayViewModel.WatchAsync) so a hook-triggered check and a
    /// poll-triggered check can never behave differently — this class
    /// only decides WHEN to check, never what "changed" means.
    /// </param>
    public TitleChangeWatcher(Action onTitleChanged)
    {
        _onTitleChanged = onTitleChanged;
        _procDelegate = OnWinEvent;

        // DEBOUNCE: a single real transition (e.g. PioneerRx opening a
        // new Pre-Check window) commonly raises SEVERAL
        // EVENT_OBJECT_NAMECHANGE notifications in quick succession as
        // the window is constructed/retitled/populated. Restarting this
        // one-shot timer on every notification and acting only once
        // things go quiet for ~50ms collapses a whole burst into a
        // single signature check, instead of hammering
        // PioneerRxWindow.GetScreenSignature() once per notification.
        _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            if (!_disposed) _onTitleChanged();
        };
    }

    /// <summary>
    /// Installs the hook. Never throws — returns false on any failure,
    /// which callers should treat as "fall back to poll-only" (see class
    /// doc "NOT A REPLACEMENT FOR THE POLL"), not as an error to surface
    /// to the pharmacist.
    /// </summary>
    public bool TryStart()
    {
        try
        {
            _hook = SetWinEventHook(
                EVENT_OBJECT_NAMECHANGE, EVENT_OBJECT_NAMECHANGE,
                IntPtr.Zero, _procDelegate,
                idProcess: 0, idThread: 0,
                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            return _hook != IntPtr.Zero;
        }
        catch
        {
            return false;
        }
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        // Only react to a WINDOW's own title changing (idObject ==
        // OBJID_WINDOW, idChild == CHILDID_SELF). EVENT_OBJECT_NAMECHANGE
        // is also raised for a CONTROL's accessible name changing inside
        // some window (e.g. a text field's name updating on every
        // keystroke) — without this filter, typing anywhere on the
        // desktop would debounce-trigger a signature check, turning this
        // into a poll faster than the 250ms one it's meant to improve on.
        if (idObject != OBJID_WINDOW || idChild != CHILDID_SELF || hwnd == IntPtr.Zero) return;

        // MARSHAL TO THE DISPATCHER: this callback arrives via the
        // message loop of whatever thread called SetWinEventHook (the UI
        // thread, in practice — so this is very likely already running
        // on the Dispatcher thread), but that's an implementation detail
        // of WINEVENT_OUTOFCONTEXT's delivery mechanism, not a
        // documented guarantee this code should rely on.
        // _onTitleChanged ultimately touches OverlayViewModel/UI state,
        // which must only ever happen on the UI thread — so this always
        // goes through the Dispatcher explicitly rather than assuming
        // the calling thread is already safe.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        if (dispatcher.CheckAccess())
        {
            RestartDebounce();
        }
        else
        {
            dispatcher.BeginInvoke(RestartDebounce);
        }
    }

    private void RestartDebounce()
    {
        if (_disposed) return;
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _debounceTimer.Stop();

        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
