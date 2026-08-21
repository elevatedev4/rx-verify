using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using RxVerifyOverlay.Integrated;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.Ocr;
using RxVerifyOverlay.OrderAssist.Geometry;
using RxVerifyOverlay.OrderAssist.Ocr;
using RxVerifyOverlay.OrderAssist.Scanning;
using RxVerifyOverlay.OrderAssist.Windows;

namespace RxVerifyOverlay.OrderAssist;

/// <summary>
/// Owns Order Assist end-to-end and is the ONLY class outside this folder
/// that MainWindow.xaml.cs (the app's composition root) needs to know
/// about. Deliberately decoupled from the verify flow's own composition
/// class (Integrated/IntegratedOverlayCoordinator.cs) — that class never
/// references any OrderAssist.* type at all (see MainWindow.xaml.cs's own
/// wiring doc for how the control box's "Order Assist" toggle reaches
/// SetEnabled below via a plain bool event, not an OrderAssist-typed
/// one), so this whole module can be deleted, disabled, or split into a
/// separate app/sold as its own module later without touching anything
/// in the verify flow.
///
/// Runs its OWN ~1s DispatcherTimer (only while SetEnabled(true) — a
/// disabled Order Assist costs nothing beyond one idle field, per the
/// owner's spec: "we don't have to waste resources scanning" when off),
/// its OWN foreground-window detection (OrderAssistWindowLocator —
/// title-only, no UIA, no shared automation session), its OWN
/// screen-capture + OCR pass, and its OWN highlight window
/// (OrderAssistOverlayWindow). The capture + OCR step REUSES the same
/// generic, verify-agnostic low-level helpers the rest of the app already
/// has (Ocr/EscriptImageCapture.cs's CaptureRegion — a plain GDI screen
/// blit with no Escript-specific knowledge despite its historical name —
/// and Ocr/WindowsMediaOcrEngine.cs) rather than duplicating either.
///
/// SELF-OCCLUSION GUARD: unlike the verify flow (whose OCR pane and
/// verdict boxes never occupy the same screen pixels), Order Assist's own
/// highlight boxes are drawn DIRECTLY ON TOP of the exact cells this
/// module's own capture reads next tick — a red/green box left on screen
/// during a capture would get baked into the OCR'd pixels and could hide
/// the very digit being checked. See TickAsync's hide-before-capture step.
/// </summary>
public sealed class OrderAssistCoordinator
{
    /// <summary>
    /// ROUND 3 (Will verbatim: "Need to have a faster update time"). Was
    /// 1000ms (round 1/2) — halved. A capture+OCR pass genuinely taking
    /// longer than this just skips a tick (see SafeTickAsync's own
    /// reentrancy-guard doc, unchanged), so this is safe to lower without
    /// risking overlapping ticks; it only changes how often a fresh
    /// attempt starts. Combined with HighlightStabilityPolicy's round-3
    /// "clear immediately + Processing" redesign (2 ticks to confirm a
    /// change, now 2 x 500ms = ~1s instead of 2 x 1000ms = ~2s), this is
    /// the other half of "faster update time" — the debounce COUNT didn't
    /// change, the WALL-CLOCK time it costs did.
    /// </summary>
    private const int TimerIntervalMs = 500;

    /// <summary>Same settle delay as the verify flow's own self-occlusion guard (Ocr/IOverlayVisibilityController.cs HideForCaptureAsync) — DWM composition isn't synchronous with a Hide() call.</summary>
    private const int CaptureSettleDelayMs = 30;

    /// <summary>
    /// OWNER FEEDBACK (2026-08-17: "The overlay on order mode is covering
    /// some buttons. Make the bottom of the overlay a little higher so it
    /// doesn't cover the New button"): OrderAssistOverlayWindow is always
    /// repositioned to EXACTLY the target Pioneer window's own bounds (see
    /// TickAsync's RepositionPhysical call) — full width AND full height,
    /// right down to the target window's own bottom edge, which is
    /// exactly where a dialog like "Create Recommended Orders" or the
    /// Catalog Item Substitution Selection window puts its own action
    /// buttons (New/Save/etc.) below the data grid. Trims that much off
    /// ONLY the highlight window's own bottom — a highlight rect whose
    /// OCR-detected position falls in the excluded strip simply won't be
    /// visible (the window's own rendering surface no longer extends that
    /// far), so it can no longer sit on top of a button down there. The
    /// OCR CAPTURE region itself (EscriptImageCapture.CaptureRegion below)
    /// is deliberately UNCHANGED — still reads the full popup — so
    /// zero-quantity/substitution detection accuracy is unaffected; only
    /// where the resulting highlights are allowed to render shrinks.
    ///
    /// ESTIMATE, NOT YET VERIFIED: order-assist-Screenshot 2026-08-13
    /// 175418.png (referenced by Integrated/IntegratedOverlayCoordinator.cs's
    /// own order-mode CONTROL BOX constants for the same reason) isn't
    /// available anywhere on this Mac, so this wasn't measured against a
    /// live Pioneer order-mode window either — 48dip is a starting guess
    /// (roughly one to two button rows) pending Will's own visual check
    /// on a real workstation.
    /// </summary>
    private const double OrderModeBottomInsetDip = 48;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    private readonly OverlaySettings _settings;
    private readonly IOcrEngine _ocrEngine;
    private readonly DispatcherTimer _timer;

    private OrderAssistOverlayWindow? _overlayWindow;
    private bool _enabled;
    private bool _windowShown;
    private bool _tickInProgress;

    /// <summary>
    /// ROUND 4 (Will verbatim: "Updating is too slow. I close the window
    /// and it takes 5+ seconds to clear.") — the HWND of whatever window
    /// this coordinator most recently scanned/targeted, tracked purely so
    /// FastCloseCheck can cheaply notice "that window is gone" on every
    /// ~500ms timer tick WITHOUT waiting for a full capture+OCR pass to
    /// run and complete first (see FastCloseCheck's own doc for the
    /// latency this closes). IntPtr.Zero means "nothing currently
    /// tracked" — cleared alongside every other piece of
    /// per-target-window state (SetEnabled(false), the no-match branch of
    /// TickAsync, FastCloseCheck itself once it fires).
    /// </summary>
    private IntPtr _currentTargetHandle = IntPtr.Zero;

    // ------------------------------------------------------------------
    // HIGHLIGHT STABILITY (ROUND 2, W-T85: "the items are flashing a
    // bunch instead of staying solid"; ROUND 3, Will: "Make sure the
    // highlighted items go away as soon as the screen is closed, or if
    // the order changes. Need to have a faster update time. It's ok to
    // clear it quickly and add a 'Processing' by the sorted by rebate
    // notice if we're waiting on analysis.") — see HighlightStabilityPolicy's
    // own doc for the CLEAR-then-confirm redesign these fields now back:
    // a changed result is cleared from screen the SAME tick it's first
    // noticed (no more holding stale content), and only re-adopted once
    // it repeats. All reset together (ResetHighlightStability) whenever
    // "the window changed" per the branch brief's own wording — target
    // lost entirely, target KIND switched, or Order Assist toggled off —
    // so a stale signature from a completely different screen can never
    // suppress/delay a fresh result on a new one.
    //
    // Round 3 also DROPS the old _displayedRedBoxesDip/_displayedCatalogHighlightsDip
    // retained-geometry fields entirely: round 2 needed them to redraw
    // something after the self-occlusion hide/clear step even when a tick
    // held its PREVIOUS answer; round 3 never holds a previous answer
    // through an unconfirmed tick (Processing draws nothing but the
    // indicator), so the self-occlusion fallback path's post-capture
    // redraw always has THIS tick's own freshly computed geometry to use
    // instead — see TickAsync's Display case.
    // ------------------------------------------------------------------

    /// <summary>HighlightSignature of whatever is CURRENTLY adopted/displayed — "" means nothing is shown. See HighlightStabilityPolicy.Decide's own param docs.</summary>
    private string _displayedSignature = "";

    /// <summary>The signature most recently proposed as a REPLACEMENT for _displayedSignature, still awaiting confirmation — see HighlightStabilityPolicy.Decide's own param docs. "" whenever nothing is pending.</summary>
    private string _pendingSignature = "";

    /// <summary>How many CONSECUTIVE ticks have now proposed _pendingSignature — see HighlightStabilityPolicy.Decide's own param doc.</summary>
    private int _pendingChangeStreak;

    /// <summary>The target window KIND the stability state above was last computed against — a kind switch (Create Recommended Orders &lt;-&gt; Catalog Substitution) means the signature space itself changed meaning, so stability state resets exactly like "target lost entirely" does.</summary>
    private OrderAssistWindowKind? _lastTickKind;

    /// <summary>
    /// REVIEW FIX (blocking — race: disabling mid-tick could re-show a
    /// stale highlight): bumped on EVERY SetEnabled call (both true and
    /// false), so any tick already in flight when a NEW enable/disable
    /// decision is made is provably stale the instant it tries to act on
    /// its result — see TickGenerationGate's own doc for the exact
    /// mechanics of the race this closes.
    /// </summary>
    private int _generation;

    /// <summary>The in-flight tick's own cancellation source, if any — SetEnabled(false) cancels it so a real, in-progress OCR pass is asked to stop rather than only being ignored once it eventually finishes. Best-effort: WindowsMediaOcrEngine only checks this token once, mid-pipeline (see its own doc) — TickGenerationGate is the guarantee that actually matters, this is defense in depth on top of it.</summary>
    private CancellationTokenSource? _tickCts;

    /// <summary>
    /// REMOTE-DEBUGGING INFRASTRUCTURE (owner's live pharmacy report,
    /// 2026-08-14: "make sure that the logic will work with the popup
    /// window because right now nothing works" — branch brief: "next
    /// failure report must be diagnosable from his logs"): the last
    /// "no target window matched" diagnostic signature actually LOGGED
    /// (a delimited join of every visible PioneerRx window title seen
    /// that tick) — see LogNoMatchDiagnosticsIfNeeded. Null until the
    /// first no-match tick logs something. Compared against the CURRENT
    /// tick's own signature so the same still-unresolved title set never
    /// re-logs every ~1s tick it persists for — only a CHANGE (a
    /// different window opened/closed, or Pioneer closed entirely) logs
    /// again. Never reset elsewhere; a stale value surviving a
    /// SetEnabled(false)/(true) cycle just means one possible duplicate
    /// skip, not a correctness issue for a best-effort diagnostic log.
    /// </summary>
    private string? _lastLoggedNoMatchTitlesSignature;

    /// <summary>Same de-dup pattern as _lastLoggedNoMatchTitlesSignature, for LogColumnDiagnosticsIfNeeded's "matched a target window but couldn't resolve its expected column(s)" case — keyed on the resolved header band labels actually seen that tick.</summary>
    private string? _lastLoggedColumnFailureSignature;

    /// <summary>Same de-dup pattern, for LogOrderQuantityColumnCellsIfEmpty (round 4) — keyed on the actual cell-text list read that tick, so an unchanged capture never re-logs every ~500ms, but a genuinely different read (Will scrolled, edited a cell, or the OCR result itself changed) does.</summary>
    private string? _lastLoggedOrderQuantityCellsSignature;

    /// <summary>Same de-dup pattern as _lastLoggedColumnFailureSignature, for LogSelectionBandsIfChanged (ROUND 2, W-T85 bug 2) — keyed on the actual band Y-range list, so a still-selected row never re-logs every ~1s tick, but a genuinely different capture (a different row now selected, or the selection cleared) does.</summary>
    private string? _lastLoggedSelectionBandsSignature;

    public OrderAssistCoordinator(OverlaySettings settings, IOcrEngine? ocrEngine = null)
    {
        _settings = settings;
        _ocrEngine = ocrEngine ?? new WindowsMediaOcrEngine();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TimerIntervalMs) };
        _timer.Tick += async (_, _) =>
        {
            // ROUND 4 — see FastCloseCheck's own doc. Runs SYNCHRONOUSLY,
            // before the await below, so it fires on EVERY timer tick even
            // while a previous tick's slow capture+OCR pass is still in
            // flight (SafeTickAsync's reentrancy guard would otherwise
            // skip this tick entirely, delaying "the window is gone" by
            // however long that in-flight OCR pass takes).
            FastCloseCheck();
            await SafeTickAsync();
        };
    }

    /// <summary>
    /// Single entry point for turning Order Assist on/off — starts/stops
    /// the ~1s timer and, when turning off, immediately clears/hides any
    /// highlight left on screen rather than waiting for a tick that will
    /// never come. See MainWindow.xaml.cs for how the control box's
    /// toggle (relayed as a plain bool through
    /// IntegratedOverlayCoordinator.OrderAssistToggleRequested) and the
    /// persisted OverlaySettings.OrderAssistEnabled flag both route
    /// through here.
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        // REVIEW FIX (blocking): invalidate whatever tick might currently
        // be in flight BEFORE changing _enabled/the timer — see
        // TickGenerationGate's doc. Cancel is best-effort (the CTS from a
        // tick that already finished and disposed its own `using` block
        // throws ObjectDisposedException here, which just means there was
        // nothing in flight to cancel).
        _generation++;
        try { _tickCts?.Cancel(); } catch (ObjectDisposedException) { /* nothing in flight -- fine */ }

        _enabled = enabled;

        if (enabled)
        {
            if (!_timer.IsEnabled) _timer.Start();
        }
        else
        {
            _timer.Stop();
            HideOverlayIfShown();
            ResetHighlightStability();
            _currentTargetHandle = IntPtr.Zero;
        }
    }

    /// <summary>
    /// ROUND 4 (Will verbatim: "Updating is too slow. I close the window
    /// and it takes 5+ seconds to clear.") — ROOT CAUSE: TickAsync's own
    /// "no target window matched" branch already clears immediately, but
    /// it only ever runs at the START of a tick, via OrderAssistWindowLocator.Scan
    /// (a full EnumWindows pass). If Pioneer's window closes WHILE a
    /// capture+OCR pass from an EARLIER tick is still running (Windows.Media.Ocr
    /// can genuinely take over a second — see WindowsMediaOcrEngine's own
    /// OCR-duration doc), that in-flight tick doesn't notice at all: it
    /// finishes its (now-stale) capture/OCR, and SafeTickAsync's reentrancy
    /// guard (`if (_tickInProgress) return;`) skips every NEW tick that
    /// would otherwise re-scan and catch the closed window sooner. Stacked
    /// with HighlightStabilityPolicy's 2-tick confirmation window, the
    /// worst case is: one slow OCR pass (1s+) PLUS up to
    /// RequiredConsecutiveTicksToAdoptChange more ticks before "gone"
    /// finally gets confirmed and cleared — comfortably enough to read as
    /// "5+ seconds" to a pharmacist watching the screen.
    ///
    /// FIX: this is the ONE thing that needs to be cheap enough to run
    /// every ~500ms regardless of what a slower, already-in-flight tick is
    /// doing — two P/Invoke calls (see OrderAssistWindowLocator.IsWindowStillValid),
    /// no capture, no OCR, no full EnumWindows pass. Runs synchronously
    /// from the timer's own Tick handler, ahead of SafeTickAsync's
    /// reentrancy guard, not gated by it. Bypasses HighlightStabilityPolicy's
    /// confirmation debounce entirely on purpose ("gone" is never a false
    /// positive worth debouncing against — a stale HWND can't un-close
    /// itself) and bumps _generation so a slow tick that started against
    /// the now-gone window can never redisplay its late result afterward
    /// (see TickGenerationGate's own doc).
    /// </summary>
    private void FastCloseCheck()
    {
        if (!_windowShown) return;
        if (OrderAssistWindowLocator.IsWindowStillValid(_currentTargetHandle)) return;

        _generation++;
        try { _tickCts?.Cancel(); } catch (ObjectDisposedException) { /* nothing in flight -- fine, same posture as SetEnabled */ }

        HideOverlayIfShown();
        ResetHighlightStability();
        _currentTargetHandle = IntPtr.Zero;
    }

    /// <summary>Releases the highlight window on app shutdown — call from MainWindow's Closed handler alongside IntegratedOverlayCoordinator.Shutdown().</summary>
    public void Shutdown()
    {
        _timer.Stop();
        try { _overlayWindow?.Close(); } catch { /* best-effort only, same posture as IntegratedOverlayCoordinator.Shutdown */ }
    }

    private async Task SafeTickAsync()
    {
        // REENTRANCY GUARD: a capture+OCR pass can occasionally take
        // longer than the 1s tick interval on a slow workstation —
        // without this, a slow tick's continuation could run concurrently
        // with the NEXT tick's Hide()/Show() calls on the same window, an
        // easy source of flicker or a highlight stuck on a stale frame.
        // Simplest fix: skip a tick outright rather than queue/cancel it
        // — Order Assist is a glance-aid, not safety-critical, so one
        // skipped ~1s refresh is a fine trade against that complexity.
        if (_tickInProgress) return;
        _tickInProgress = true;

        try
        {
            await TickAsync();
        }
        catch (Exception)
        {
            HideOverlayIfShown();
        }
        finally
        {
            _tickInProgress = false;
        }
    }

    private async Task TickAsync()
    {
        if (!_enabled) return;

        // REVIEW FIX (blocking): capture THIS tick's generation now, and
        // never mutate the overlay's visible state below without
        // re-confirming it's still current (see the final check below) —
        // see TickGenerationGate's doc for the exact race this closes.
        // The CTS is real, best-effort cancellation on top of that (see
        // the _tickCts field doc) — using var disposes it the moment this
        // tick returns by any path, and SetEnabled's own Cancel() call
        // already tolerates that being disposed by the time it runs.
        var tickGeneration = _generation;
        using var cts = new CancellationTokenSource();
        _tickCts = cts;

        var scan = OrderAssistWindowLocator.Scan();
        if (scan.Target is null)
        {
            LogNoMatchDiagnosticsIfNeeded(scan.VisiblePioneerWindowTitles);
            HideOverlayIfShown();
            // "The window changed" — see ResetHighlightStability's own
            // doc: a held/pending signature from whatever WAS on screen
            // has no business influencing a completely different (or
            // absent) screen once Pioneer navigates away.
            ResetHighlightStability();
            _currentTargetHandle = IntPtr.Zero;
            return;
        }

        var target = scan.Target;
        _currentTargetHandle = target.Value.Handle; // see FastCloseCheck's own doc

        // A held/pending signature only makes sense against the SAME
        // target kind it was computed against — Create Recommended
        // Orders' row-index signatures and Catalog Substitution's are not
        // comparable at all, so a kind switch resets exactly like losing
        // the target entirely does.
        if (_lastTickKind is not null && _lastTickKind != target.Value.Kind)
        {
            ResetHighlightStability();
        }
        _lastTickKind = target.Value.Kind;

        var overlay = EnsureOverlayWindow();

        // SELF-OCCLUSION GUARD — see class doc AND OrderAssistOverlayWindow.
        // IsExcludedFromCapture's own doc (ROUND 2, W-T85 bug 3 fix): once
        // Windows itself omits this window from any GDI capture, there's
        // nothing to hide — skip the round trip (and its visible on/off
        // pulse) entirely, same early-return MainWindow.HideForCaptureAsync
        // already uses for the verify flow's own equivalent window.
        var usingCaptureExclusion = overlay.IsExcludedFromCapture;
        if (!usingCaptureExclusion && _windowShown)
        {
            overlay.HideAndClear();
            _windowShown = false;
            await Task.Delay(CaptureSettleDelayMs, cts.Token);
        }

        using var bitmap = EscriptImageCapture.CaptureRegion(target.Value.Bounds);

        // ROUND 3 (repeat complaint — see Ocr/RowHighlightColorDetector's
        // own ROOT CAUSE doc: round 2's blue-only detector is replaced by a
        // hue-agnostic one covering any genuinely colored row fill, not
        // just one specific unverified blue). Binarizes any highlighted
        // row band to normal dark-text-on-white contrast BEFORE OCR ever
        // sees it, for BOTH target kinds (a no-op when no highlighted band
        // is present this tick) — fixes the Catalog Substitution
        // blue-first-row skip, a McKesson-yellow-flagged row skip, AND
        // (same mechanism) a Create Recommended Orders row put into the
        // same kind of selection/focus highlight while its Order Quantity
        // cell is being edited. Logging is best-effort and throttled the
        // same "log on change" way as every other diagnostic in this class
        // — see LogSelectionBandsIfChanged.
        var highlightResult = RowHighlightNormalizer.NormalizeInPlace(bitmap);
        LogSelectionBandsIfChanged(highlightResult.AcceptedBands, highlightResult.RejectedCandidates);

        var ocrResult = await _ocrEngine.RecognizeAsync(bitmap, cts.Token);

        var scale = DpiScaleFor(target.Value.Handle);

        var redBoxesDip = new List<DipRect>();
        CatalogHighlights? catalogHighlights = null;
        DipRect? processingAnchorDip = null;
        var newSignature = "";

        switch (target.Value.Kind)
        {
            case OrderAssistWindowKind.CreateRecommendedOrders:
                var zeroHighlights = CreateRecommendedOrdersScanner.FindZeroQuantityHighlights(ocrResult.Words);
                foreach (var highlight in zeroHighlights)
                {
                    redBoxesDip.Add(ToDip(highlight.Left, highlight.Top, highlight.Right, highlight.Bottom, scale));
                }
                newSignature = HighlightSignature.ForZeroQuantityHighlights(zeroHighlights);

                // ROUND 4 (Will's SECOND repeat report on the same
                // symptom, this time with a screenshot showing a genuine
                // 0 in Order Quantity that never got flagged) — see
                // LogOrderQuantityColumnCellsIfEmpty's own doc: neither
                // existing diagnostic (LogNoMatchDiagnosticsIfNeeded,
                // LogColumnDiagnosticsIfNeeded) tells the difference
                // between "column resolved but every cell legitimately
                // read non-zero" and "column resolved but OCR silently
                // missed the one cell that actually said 0" — this fills
                // that specific gap.
                if (zeroHighlights.Count == 0)
                {
                    LogOrderQuantityColumnCellsIfEmpty(ocrResult.Words);
                }
                break;

            case OrderAssistWindowKind.CatalogSubstitution:
                var annotations = CatalogSubstitutionScanner.Analyze(ocrResult.Words);
                catalogHighlights = ToDip(annotations, scale);
                if (annotations.CostColumnHeaderAnchor is { } costAnchor)
                {
                    // Round 3's "Processing" indicator anchor — see
                    // CatalogHighlights.ProcessingAnchorDip's own doc.
                    // Deliberately computed from THIS tick's fresh
                    // annotations regardless of the stability decision
                    // below, so it's available even on a tick whose
                    // SAVINGS analysis itself is still debouncing.
                    processingAnchorDip = ToDip(costAnchor.Left, costAnchor.Top, costAnchor.Right, costAnchor.Top, scale);
                }
                newSignature = HighlightSignature.ForCatalogAnnotations(annotations);
                break;
        }

        // REVIEW FIX (blocking): the actual fix — see TickAsync's own doc
        // above and TickGenerationGate's doc. Everything above this point
        // only computed a result; nothing touched the overlay's visible
        // state (or this tick's own stability bookkeeping) yet, so
        // bailing out here is always safe. If SetEnabled ran at all since
        // this tick started (enabling OR disabling — either means this
        // result is stale), discard it instead of showing/repositioning a
        // highlight the pharmacist may have already turned off, with no
        // future tick left to correct it.
        if (!TickGenerationGate.IsStillCurrent(tickGeneration, _generation)) return;

        // HIGHLIGHT STABILITY (ROUND 2, W-T85 bug 3; ROUND 3 CLEAR-then-
        // confirm redesign — see HighlightStabilityPolicy's own doc for
        // the full reasoning). This decision governs WHICH result actually
        // reaches the overlay; note it never affects the diagnostic-
        // logging path below, which still fires on every empty tick
        // exactly as before.
        var outcome = HighlightStabilityPolicy.Decide(newSignature, _displayedSignature, _pendingSignature, _pendingChangeStreak);
        _pendingSignature = outcome.PendingSignature;
        _pendingChangeStreak = outcome.PendingStreak;
        var isNewResultEmpty = string.IsNullOrEmpty(newSignature);

        if (isNewResultEmpty)
        {
            // Nothing NEW to highlight this tick (e.g. no zero
            // quantities, or no substitution row is cheaper than
            // McKesson) — could ALSO mean column resolution itself failed
            // (a bad/partial OCR capture, or the window's actual header
            // text doesn't match what this app expects) — see
            // LogColumnDiagnosticsIfNeeded, which tells the two apart and
            // only logs the latter. Fires regardless of the stability
            // decision below — this diagnostic is about what THIS tick's
            // OCR pass found, independent of what's still on screen.
            LogColumnDiagnosticsIfNeeded(target.Value.Kind, ocrResult.Words);
        }

        switch (outcome.Decision)
        {
            case HighlightStabilityPolicy.Decision.Clear:
                HideOverlayIfShown();
                _displayedSignature = "";
                break;

            case HighlightStabilityPolicy.Decision.Display:
                DrawAndShow(overlay, target.Value, scale, redBoxesDip, catalogHighlights);
                _displayedSignature = newSignature;
                break;

            case HighlightStabilityPolicy.Decision.Processing:
                // ROUND 3 (Will: "It's ok to clear it quickly and add a
                // 'Processing' by the sorted by rebate notice if we're
                // waiting on analysis") — never redraw the PREVIOUS
                // (possibly stale) result here; the overlay was already
                // hidden/cleared above (self-occlusion guard) or is about
                // to show only the Processing indicator, never old
                // content. _displayedSignature is deliberately left
                // unchanged (see HighlightStabilityPolicy.Outcome's own
                // doc) — it still tracks the last ADOPTED result, which a
                // later tick may re-confirm instantly (see that class's
                // own "flicker back" case) even though nothing is drawn
                // right now.
                var processingHighlights = processingAnchorDip is { } anchor
                    ? new CatalogHighlights(
                        Array.Empty<SavingsBadgeDip>(), null, null, null, null, null, null, false,
                        anchor)
                    : null;
                DrawAndShow(overlay, target.Value, scale, Array.Empty<DipRect>(), processingHighlights);
                break;
        }
    }

    /// <summary>Repositions the overlay to the target window's current bounds and draws exactly the given highlights — the ONE place TickAsync actually mutates the overlay's visible content, called either with this tick's freshly computed, CONFIRMED result (Decision.Display) or an empty/Processing-only payload while a change is still debouncing (Decision.Processing) — see HighlightStabilityPolicy's own doc.</summary>
    private void DrawAndShow(OrderAssistOverlayWindow overlay, OrderAssistWindowLocator.TargetWindow target, double scale, IReadOnlyList<DipRect> redBoxesDip, CatalogHighlights? catalogHighlights)
    {
        // See OrderModeBottomInsetDip's own doc — the highlight window
        // stops short of the target window's own bottom edge, where
        // Pioneer's own action buttons (e.g. "New") live.
        var overlayHeight = OrderModeOverlayBoundsRule.TrimmedHeightPhysical(target.Bounds.Height, OrderModeBottomInsetDip, scale);
        overlay.RepositionPhysical(target.Bounds.X, target.Bounds.Y, target.Bounds.Width, overlayHeight);
        overlay.SetHighlights(redBoxesDip, catalogHighlights);
        overlay.ForceHitTestTransparent();
        overlay.Show();

        if (!_windowShown)
        {
            overlay.EnsureTopmost();
        }

        _windowShown = true;
    }

    /// <summary>
    /// OCR word coordinates (and everything derived from them by
    /// Scanning/*) are relative to the CAPTURED REGION — i.e. relative to
    /// the target window's own top-left, per Ocr/WindowsMediaOcrEngine.cs's
    /// "these boxes remain relative to the captured region, not the full
    /// screen" doc. Since OrderAssistOverlayWindow is always repositioned
    /// to exactly <c>target.Bounds</c> (same call site), those LOCAL
    /// pixels are already relative to THIS window's own top-left too — no
    /// separate origin-offset step needed, just the DPI divide. Reuses
    /// Integrated/DpiRectConverter.cs (treating the local rect as if its
    /// own top-left were the "window origin", i.e. Point.Empty) rather
    /// than reimplementing the same division here.
    /// </summary>
    private static DipRect ToDip(double left, double top, double right, double bottom, double scale)
    {
        var localPhysical = new Rectangle(
            (int)Math.Round(left),
            (int)Math.Round(top),
            (int)Math.Round(right - left),
            (int)Math.Round(bottom - top));

        return DpiRectConverter.ToDipRect(localPhysical, Point.Empty, scale, scale);
    }

    /// <summary>
    /// Converts every field of one CatalogSubstitutionScanner.CatalogAnnotations
    /// (plain OCR/capture-region doubles, see RowRect's own doc) into the
    /// DIP-space CatalogHighlights the overlay window actually draws with
    /// — same "ToDip everything, once, right before crossing into the
    /// rendering layer" posture as the red/green conversion above. The
    /// sort badge's anchor is a zero-height rect (Top used for both Y
    /// bounds) since only its own top edge and the column's horizontal
    /// extent matter for placement — see OrderAssistOverlayWindow.AddSortBadge.
    /// ProcessingAnchorDip is DELIBERATELY left at its default (null) here
    /// — TickAsync computes that separately from annotations.CostColumnHeaderAnchor
    /// and only attaches it to a stripped-down CatalogHighlights on an
    /// actual Decision.Processing tick (see that switch case's own doc) —
    /// this conversion is only ever used for a real, confirmed Display.
    /// </summary>
    private static CatalogHighlights ToDip(CatalogSubstitutionScanner.CatalogAnnotations annotations, double scale)
    {
        var savingsBadgesDip = annotations.SavingsBadges
            .Select(b => new SavingsBadgeDip(ToDip(b.Left, b.Top, b.Right, b.Bottom, scale), b.SavingsDisplay, b.MeetsThreshold))
            .ToList();

        DipRect? bestLargeDip = null;
        string? bestLargeLabel = null;
        if (annotations.BestLargePackageMarker is { } bestLarge)
        {
            bestLargeDip = ToDip(bestLarge.Left, bestLarge.Top, bestLarge.Right, bestLarge.Bottom, scale);
            bestLargeLabel = bestLarge.Label;
        }

        DipRect? bestSmallDip = null;
        string? bestSmallLabel = null;
        if (annotations.BestSmallPackageMarker is { } bestSmall)
        {
            bestSmallDip = ToDip(bestSmall.Left, bestSmall.Top, bestSmall.Right, bestSmall.Bottom, scale);
            bestSmallLabel = bestSmall.Label;
        }

        DipRect? sortBadgeAnchorDip = null;
        string? sortBadgeText = null;
        var sortBadgeIsSorted = false;
        if (annotations.SortIndicatorBadge is { } badge)
        {
            sortBadgeAnchorDip = ToDip(badge.Left, badge.Top, badge.Right, badge.Top, scale);
            sortBadgeText = badge.Text;
            sortBadgeIsSorted = badge.IsSorted;
        }

        DipRect? mckessonBaselineDip = null;
        string? mckessonBaselineLabel = null;
        if (annotations.McKessonBaselineMarker is { } mckessonBaseline)
        {
            mckessonBaselineDip = ToDip(mckessonBaseline.Left, mckessonBaseline.Top, mckessonBaseline.Right, mckessonBaseline.Bottom, scale);
            mckessonBaselineLabel = mckessonBaseline.Label;
        }

        return new CatalogHighlights(
            savingsBadgesDip,
            bestLargeDip, bestLargeLabel,
            bestSmallDip, bestSmallLabel,
            sortBadgeAnchorDip, sortBadgeText, sortBadgeIsSorted,
            ProcessingAnchorDip: null,
            McKessonBaselineDip: mckessonBaselineDip, McKessonBaselineLabel: mckessonBaselineLabel);
    }


    private static double DpiScaleFor(IntPtr windowHandle)
    {
        var dpi = GetDpiForWindow(windowHandle);
        return dpi > 0 ? dpi / 96.0 : 1.0;
    }

    private OrderAssistOverlayWindow EnsureOverlayWindow() => _overlayWindow ??= new OrderAssistOverlayWindow();

    private void HideOverlayIfShown()
    {
        if (!_windowShown) return;
        _overlayWindow?.HideAndClear();
        _windowShown = false;
    }

    /// <summary>Clears every HighlightStabilityPolicy bookkeeping field back to "nothing displayed, no history" — call whenever "the window changed" (target lost, target KIND switched, or Order Assist disabled) so a signature from a completely different screen can never suppress or delay a fresh result on a new one. See the fields' own doc block for the full reasoning.</summary>
    private void ResetHighlightStability()
    {
        _displayedSignature = "";
        _pendingSignature = "";
        _pendingChangeStreak = 0;
        _lastTickKind = null;
    }

    /// <summary>
    /// REMOTE-DEBUGGING INFRASTRUCTURE — see _lastLoggedNoMatchTitlesSignature's
    /// doc. Logs (throttled to once per DISTINCT title set, not every
    /// ~1s tick) every visible PioneerRx-owned top-level window's title
    /// on a tick where Order Assist is enabled but OrderAssistWindowLocator.Scan
    /// matched neither target screen — this is exactly the data Will
    /// needs to tell "Pioneer isn't open at all" apart from "Pioneer's
    /// windows are open but none of their titles are what this app
    /// expects" (e.g. a Pioneer version/locale that titles the screen
    /// differently) without reproducing the failure live in front of
    /// anyone. Uses the SAME %TEMP%\VerifyOCR log file as the verify
    /// flow's own OcrLogger — see OrderAssistWindowLocator.ScanResult's
    /// own "PHI CAVEAT" doc for why an arbitrary Pioneer window title
    /// (e.g. a Pre-Check/Edit Rx screen with a patient name in its own
    /// title bar) can land in this same local, never-transmitted file.
    /// </summary>
    private void LogNoMatchDiagnosticsIfNeeded(IReadOnlyList<string> visiblePioneerWindowTitles)
    {
        try
        {
            var signature = string.Join("|", visiblePioneerWindowTitles);
            if (signature == _lastLoggedNoMatchTitlesSignature) return;
            _lastLoggedNoMatchTitlesSignature = signature;

            var titlesDisplay = visiblePioneerWindowTitles.Count > 0
                ? string.Join(" | ", visiblePioneerWindowTitles)
                : "(none -- no visible PioneerRx-owned top-level window found at all)";

            OcrLogger.LogTiming($"OrderAssist: no target window matched this tick. Visible PioneerRx window titles: [{titlesDisplay}]");
        }
        catch
        {
            // Best-effort diagnostic only — see OcrLogger's own posture;
            // a logging hiccup must never affect the actual scan.
        }
    }

    /// <summary>
    /// REMOTE-DEBUGGING INFRASTRUCTURE — companion to
    /// LogNoMatchDiagnosticsIfNeeded for the OTHER silent-failure shape:
    /// a target window WAS matched (redBoxesDip/catalogHighlights came
    /// back empty for this tick regardless), but is that because there
    /// was genuinely nothing to flag (a legitimate empty result — no
    /// zero-quantity rows, or no substitution clearing the 25% bar) or
    /// because ColumnResolver couldn't find the header column(s) this
    /// window's scanner needs at all (a bad/partial OCR capture, or a
    /// Pioneer build/locale whose header text doesn't match this app's
    /// expectations)? Re-runs ONLY the row-grouping/header/column-band
    /// steps (not the full scanner pipeline — this never duplicates or
    /// second-guesses CreateRecommendedOrdersScanner/CatalogSubstitutionScanner's
    /// own already-computed, already-fail-safe result) purely to answer
    /// that question for logging. Throttled the same way as
    /// LogNoMatchDiagnosticsIfNeeded — see _lastLoggedColumnFailureSignature's
    /// doc — keyed on the resolved header band labels themselves, so a
    /// still-unresolved capture doesn't re-log every tick, but a
    /// DIFFERENT bad capture (different bands) does.
    /// </summary>
    /// <summary>
    /// ROUND 2 (W-T85 bug 2), ROUND 3 GENERALIZED — best-effort, throttled
    /// diagnostic for RowHighlightNormalizer's own bitmap preprocessing
    /// (see that class's and RowHighlightColorDetector's own doc for why
    /// this exists: the exact chroma/luminance bounds are still an
    /// UNVERIFIED estimate, and this is what proves or disproves the
    /// detector actually firing — on ANY highlight color now, not just
    /// blue selection — on Will's real screen without another
    /// live-diagnosis round). Logs only the Y-range count/list — never any
    /// OCR'd text, never a screenshot — so there is nothing PHI-adjacent
    /// here at all, unlike the window-title/column-band diagnostics
    /// elsewhere in this class.
    ///
    /// ROUND 5: also logs the REJECTED near-miss bands (measured dominant
    /// chroma/luminance/fill-fraction only — same non-PHI posture, still
    /// never any OCR'd text or pixel data itself) so a still-unread colored
    /// row's next report carries the exact values this detector saw
    /// instead of another guess. See
    /// RowHighlightNormalizer.RejectedHighlightCandidate's own doc.
    /// </summary>
    private void LogSelectionBandsIfChanged(
        IReadOnlyList<(int Top, int Bottom)> bands,
        IReadOnlyList<RowHighlightNormalizer.RejectedHighlightCandidate> rejectedCandidates)
    {
        var signature = bands.Count == 0 ? "" : string.Join(",", bands.Select(b => $"{b.Top}-{b.Bottom}"));

        // ROUND 5: fold the rejected near-miss bands into the same
        // change-signature/throttle so a still-unread pale/colored row logs
        // its measured chroma/luminance/fraction once per distinct
        // capture, not every ~1s tick -- same "log on change" posture as
        // the accepted-band signature above. See
        // RowHighlightNormalizer.RejectedHighlightCandidate's own doc for
        // why this exists (Will's next report on a row that's STILL not
        // reading pinpoints the exact values instead of another guess).
        var candidateSignature = rejectedCandidates.Count == 0
            ? ""
            : string.Join(",", rejectedCandidates.Select(c =>
                $"{c.Top}-{c.Bottom}:chroma={c.Chroma},lum={c.Luminance:F0},frac={c.Fraction:F2}"));

        var combinedSignature = signature + "|" + candidateSignature;
        if (combinedSignature == _lastLoggedSelectionBandsSignature) return;
        _lastLoggedSelectionBandsSignature = combinedSignature;

        OcrLogger.LogTiming(bands.Count == 0
            ? "OrderAssist: row-highlight normalizer found 0 bands this tick"
            : $"OrderAssist: row-highlight normalizer binarized {bands.Count} band(s) this tick: [{signature}]");

        if (rejectedCandidates.Count > 0)
        {
            OcrLogger.LogTiming($"OrderAssist: row-highlight normalizer rejected {rejectedCandidates.Count} near-miss band(s) this tick (measured dominant color, not detected as highlight): [{candidateSignature}]");
        }
    }

    /// <summary>
    /// 2026-08-18 update (W-T76/78/81, "nothing highlights" — Will's real
    /// diagnostic log confirmed the root cause: window chrome — title bar,
    /// menu bar, on Catalog Substitution also a filter/toolbar row — was
    /// eating the old fixed 2-row header search budget before it ever
    /// reached the real grid header; see HeaderRowWindowSelector's own
    /// root-cause doc). Re-runs HeaderRowWindowSelector.EnumerateCandidates
    /// (the SAME search CreateRecommendedOrdersScanner/CatalogSubstitutionScanner's
    /// own already-computed, already-fail-safe result used) purely for
    /// logging — never second-guesses or duplicates their result.
    ///
    /// Branch brief item 4 ("log ALL candidate bands ... not just the
    /// winning one"): on a genuine failure, EVERY row-window candidate this
    /// tick considered is logged, each with its own y-range and resolved
    /// band labels — not just whichever one happened to win (or the single
    /// fixed header slot the old code always used) — so a future failure
    /// paste pinpoints exactly which vertical band of the capture actually
    /// held the real header, instantly, instead of needing another
    /// live-diagnosis round.
    /// </summary>
    private void LogColumnDiagnosticsIfNeeded(OrderAssistWindowKind kind, IReadOnlyList<OcrWord> words)
    {
        try
        {
            var rows = TableRowGrouper.GroupIntoRows(words);

            var expectedLabels = kind == OrderAssistWindowKind.CreateRecommendedOrders
                ? new[] { CreateRecommendedOrdersScanner.OrderQuantityHeaderLabel }
                : new[] { CatalogSubstitutionScanner.SupplierHeaderLabel, CatalogSubstitutionScanner.RebateCostPerUnitHeaderLabel };

            var candidates = HeaderRowWindowSelector.EnumerateCandidates(rows, expectedLabels);
            var winner = HeaderRowWindowSelector.PickBest(candidates);

            // Resolution only actually SUCCEEDED if every expected label
            // resolves EXACTLY off the winning candidate's own bands — the
            // scoring above tolerates a near-miss OCR misread (see
            // HeaderRowWindowSelector.LabelsAreCloseMatch's own doc) purely
            // to pick the right ROW WINDOW; ColumnResolver.ResolveExact's
            // strict equality is still what actually gates a real result,
            // same substring-trap-safe contract as before.
            var resolvedOk = winner is not null &&
                expectedLabels.All(label => ColumnResolver.ResolveExact(winner.Bands, label) is not null);

            if (resolvedOk) return; // an empty highlight result this tick is a legitimate "nothing to flag", not a failure worth logging

            IReadOnlyList<string> missing = winner is null
                ? expectedLabels
                : expectedLabels.Where(label => ColumnResolver.ResolveExact(winner.Bands, label) is null).ToList();

            LogColumnFailureOnce(kind, missing, candidates);
        }
        catch
        {
            // Best-effort diagnostic only — see LogNoMatchDiagnosticsIfNeeded's posture.
        }
    }

    private void LogColumnFailureOnce(OrderAssistWindowKind kind, IReadOnlyList<string> missing, IReadOnlyList<HeaderRowWindowSelector.Candidate> candidates)
    {
        // Signature covers every candidate's start/span/labels — a tick
        // whose candidate SET is unchanged (still the same bad capture)
        // never re-logs; any genuine change (different OCR read, Pioneer
        // moved to a different Rx/screen) does.
        var signature = $"{kind}:" + string.Join(";", candidates.Select(c => $"{c.StartRowIndex}/{c.RowCount}:{string.Join(",", c.Bands.Select(b => b.Label))}"));
        if (signature == _lastLoggedColumnFailureSignature) return;
        _lastLoggedColumnFailureSignature = signature;

        var missingDescription = missing.Count > 0 ? string.Join(", ", missing) : "(unknown)";

        var candidatesDisplay = candidates.Count == 0
            ? "(none -- header search found no non-data leading rows at all)"
            : string.Join(" ", candidates.Select(c =>
            {
                var bandsDisplay = c.Bands.Count > 0 ? string.Join(" | ", c.Bands.Select(b => b.Label)) : "(no bands)";
                return $"[rows={c.StartRowIndex}-{c.StartRowIndex + c.RowCount - 1} y={c.Top:F0}-{c.Bottom:F0} score={c.Score} bands=({bandsDisplay})]";
            }));

        OcrLogger.LogTiming($"OrderAssist[{kind}]: column resolution failed for {missingDescription}. {candidates.Count} candidate row-window(s) scanned: {candidatesDisplay}");
    }

    /// <summary>
    /// ROUND 4 (Will's SECOND repeat report on Order Quantity
    /// zero-flagging, this time WITH a screenshot showing a genuine 0
    /// that never got flagged) — fills a diagnostic gap neither existing
    /// logger covers: an EMPTY zeroHighlights result is only a genuine
    /// "nothing to flag" when every Order Quantity cell this tick
    /// actually read a non-zero number. If OCR silently missed the ONE
    /// cell that said "0" (a lone, single-character digit is
    /// architecturally the hardest possible OCR target — see
    /// WindowsMediaOcrEngine's own UPSCALE doc for the last time a
    /// small-text OCR miss was diagnosed this exact way), that cell reads
    /// back as "" (CellValueBucketizer's "blank cell -> Unknown, never
    /// flagged" contract — see ZeroQuantityDetector), which is
    /// INDISTINGUISHABLE from a legitimately empty tick anywhere else in
    /// this pipeline. Logs the RAW per-row cell text this tick actually
    /// read for the Order Quantity column — no PHI risk (drug/inventory
    /// quantities only, same posture as every other Order Assist
    /// diagnostic — see OrderAssistWindowLocator.ScanResult's own PHI
    /// CAVEAT contrasting this with OTHER Pioneer windows) — so the NEXT
    /// capture where Will sees a real, unflagged 0 on screen shows
    /// definitively whether this app read "0" and failed to classify it
    /// (a real bug elsewhere in this file) or read "" / something else
    /// entirely (an OCR miss on that one glyph — the likelier explanation
    /// given this round's header-gap/column-resolution re-measurement
    /// against Will's own screenshot came back clean, see the branch
    /// report). Re-runs the SAME row-grouping/header/column steps
    /// CreateRecommendedOrdersScanner's own already-computed,
    /// already-fail-safe result used — never second-guesses it, purely
    /// for this extra logging. Silently does nothing if the column
    /// itself didn't resolve this tick — LogColumnDiagnosticsIfNeeded
    /// (called separately) already owns that failure shape.
    /// </summary>
    private void LogOrderQuantityColumnCellsIfEmpty(IReadOnlyList<OcrWord> words)
    {
        try
        {
            var rows = TableRowGrouper.GroupIntoRows(words);
            var winner = HeaderRowWindowSelector.SelectBest(rows, new[] { CreateRecommendedOrdersScanner.OrderQuantityHeaderLabel });
            if (winner is null) return; // LogColumnDiagnosticsIfNeeded already logs this failure shape

            var orderQuantityColumn = ColumnResolver.ResolveExact(winner.Bands, CreateRecommendedOrdersScanner.OrderQuantityHeaderLabel);
            if (orderQuantityColumn is null) return; // ditto

            var bodyRows = rows.Skip(winner.StartRowIndex + winner.RowCount).ToList();
            var cells = CellValueBucketizer.BucketColumn(bodyRows, orderQuantityColumn);
            var cellTexts = cells.Select(c => string.IsNullOrWhiteSpace(c.Text) ? "(blank)" : c.Text).ToList();

            var signature = string.Join("|", cellTexts);
            if (signature == _lastLoggedOrderQuantityCellsSignature) return;
            _lastLoggedOrderQuantityCellsSignature = signature;

            OcrLogger.LogTiming($"OrderAssist[CreateRecommendedOrders]: 0 zero-quantity highlights this tick. Order Quantity column read {cellTexts.Count} row(s): [{string.Join(", ", cellTexts)}]");
        }
        catch
        {
            // Best-effort diagnostic only — see LogNoMatchDiagnosticsIfNeeded's posture.
        }
    }
}
