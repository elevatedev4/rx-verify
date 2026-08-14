namespace RxVerifyOverlay.OrderAssist;

/// <summary>
/// Pure "is this tick's result still current" check — see
/// OrderAssistCoordinator.TickAsync's REVIEW FIX doc for the race this
/// closes. SetEnabled bumps a generation counter on every call
/// (including SetEnabled(false)); a tick captures the generation once at
/// its own start. WPF's Dispatcher SynchronizationContext keeps pumping
/// other queued UI work — including the very checkbox click that calls
/// SetEnabled(false) — while TickAsync is suspended on an await (the
/// capture-settle delay, or the OCR pass), so a pharmacist unchecking
/// "Order Assist" mid-tick can run to completion BEFORE that tick's
/// continuation resumes. Without re-checking the generation immediately
/// before ever mutating the overlay's visible state, that stale
/// continuation would still call Show()/SetHighlights() and re-display a
/// highlight the pharmacist just turned off — with no future tick left
/// to self-correct, since SetEnabled(false) already stopped the timer.
/// </summary>
public static class TickGenerationGate
{
    /// <summary>True only if <paramref name="capturedGeneration"/> (read at the start of a tick) still equals <paramref name="currentGeneration"/> (read again right before acting on that tick's result) — false means at least one SetEnabled call ran in between, so the tick's result must be discarded, never shown.</summary>
    public static bool IsStillCurrent(int capturedGeneration, int currentGeneration) => capturedGeneration == currentGeneration;
}
