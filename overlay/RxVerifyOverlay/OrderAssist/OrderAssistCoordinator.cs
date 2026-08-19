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
    private const int TimerIntervalMs = 1000;

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

    // ------------------------------------------------------------------
    // HIGHLIGHT STABILITY (ROUND 2, W-T85: "the items are flashing a
    // bunch instead of staying solid") — see HighlightStabilityPolicy's
    // own doc for the two jobs these fields back: holding a displayed
    // result through a transient empty tick, and requiring a genuinely
    // different result to repeat before adopting it. All reset together
    // (ResetHighlightStability) whenever "the window changed" per the
    // branch brief's own wording — target lost entirely, target KIND
    // switched, or Order Assist toggled off — so a stale signature from a
    // completely different screen can never suppress/delay a fresh
    // result on a new one.
    // ------------------------------------------------------------------

    /// <summary>HighlightSignature of whatever is CURRENTLY displayed — "" means nothing is shown. See HighlightStabilityPolicy.Decide's own param docs.</summary>
    private string _displayedSignature = "";

    /// <summary>The actual DIP-space geometry currently displayed — retained so a KeepDisplayed decision can redraw it after the self-occlusion hide/clear step (only needed on the fallback path where OrderAssistOverlayWindow.IsExcludedFromCapture is false; see TickAsync).</summary>
    private IReadOnlyList<DipRect> _displayedRedBoxesDip = Array.Empty<DipRect>();

    private CatalogHighlights? _displayedCatalogHighlightsDip;

    /// <summary>How many CONSECUTIVE ticks immediately before this one computed an empty result — see HighlightStabilityPolicy.Decide's own param doc.</summary>
    private int _consecutiveEmptyTicks;

    /// <summary>The signature most recently proposed as a REPLACEMENT for _displayedSignature (non-empty, different from it) — reset to "" the instant a tick proposes something else, or matches what's displayed.</summary>
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

    /// <summary>Same de-dup pattern as _lastLoggedColumnFailureSignature, for LogSelectionBandsIfChanged (ROUND 2, W-T85 bug 2) — keyed on the actual band Y-range list, so a still-selected row never re-logs every ~1s tick, but a genuinely different capture (a different row now selected, or the selection cleared) does.</summary>
    private string? _lastLoggedSelectionBandsSignature;

    public OrderAssistCoordinator(OverlaySettings settings, IOcrEngine? ocrEngine = null)
    {
        _settings = settings;
        _ocrEngine = ocrEngine ?? new WindowsMediaOcrEngine();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TimerIntervalMs) };
        _timer.Tick += async (_, _) => await SafeTickAsync();
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
        }
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
            return;
        }

        var target = scan.Target;

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

        // ROUND 2 (W-T85 bug 2, Will verbatim: "The analysis is also
        // skipping whatever row is highlighted (usually the first row
        // starts highlighted, as a dark blue)") — see
        // SelectionRowColorDetector's own ROOT CAUSE doc. Normalizes
        // white-on-selection-blue rows to normal dark-on-light contrast
        // BEFORE OCR ever sees them, for BOTH target kinds (a no-op when
        // no selection band is present this tick). Logging is best-effort
        // and throttled the same "log on change" way as every other
        // diagnostic in this class — see LogSelectionBandsIfChanged.
        var selectionBands = SelectionRowNormalizer.NormalizeInPlace(bitmap);
        LogSelectionBandsIfChanged(selectionBands);

        var ocrResult = await _ocrEngine.RecognizeAsync(bitmap, cts.Token);

        var scale = DpiScaleFor(target.Value.Handle);

        var redBoxesDip = new List<DipRect>();
        CatalogHighlights? catalogHighlights = null;
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
                break;

            case OrderAssistWindowKind.CatalogSubstitution:
                var annotations = CatalogSubstitutionScanner.Analyze(ocrResult.Words);
                catalogHighlights = ToDip(annotations, scale);
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

        // HIGHLIGHT STABILITY (ROUND 2, W-T85 bug 3: "the items are
        // flashing a bunch instead of staying solid") — see
        // HighlightStabilityPolicy's own doc for the full reasoning. This
        // decision governs WHICH result actually reaches the overlay;
        // note it never affects the diagnostic-logging path below, which
        // still fires on every empty tick exactly as before.
        var decision = HighlightStabilityPolicy.Decide(newSignature, _displayedSignature, _consecutiveEmptyTicks, _pendingChangeStreak);
        var isNewResultEmpty = string.IsNullOrEmpty(newSignature);

        if (isNewResultEmpty)
        {
            _consecutiveEmptyTicks++;
            _pendingSignature = "";
            _pendingChangeStreak = 0;
        }
        else
        {
            _consecutiveEmptyTicks = 0;
            if (newSignature == _displayedSignature)
            {
                _pendingSignature = "";
                _pendingChangeStreak = 0;
            }
            else if (newSignature == _pendingSignature)
            {
                _pendingChangeStreak++;
            }
            else
            {
                _pendingSignature = newSignature;
                _pendingChangeStreak = 1;
            }
        }

        if (isNewResultEmpty)
        {
            // Nothing NEW to highlight this tick (e.g. no zero
            // quantities, or no substitution meets the 25% bar) — could
            // ALSO mean column resolution itself failed (a bad/partial
            // OCR capture, or the window's actual header text doesn't
            // match what this app expects) — see LogColumnDiagnosticsIfNeeded,
            // which tells the two apart and only logs the latter. Fires
            // regardless of the stability decision below (KeepDisplayed
            // vs. Clear) — this diagnostic is about what THIS tick's OCR
            // pass found, independent of what's still on screen from a
            // held previous result.
            LogColumnDiagnosticsIfNeeded(target.Value.Kind, ocrResult.Words);
        }

        switch (decision)
        {
            case HighlightStabilityPolicy.Decision.Clear:
                HideOverlayIfShown();
                _displayedSignature = "";
                _displayedRedBoxesDip = Array.Empty<DipRect>();
                _displayedCatalogHighlightsDip = null;
                break;

            case HighlightStabilityPolicy.Decision.Display:
                DrawAndShow(overlay, target.Value, scale, redBoxesDip, catalogHighlights);
                _displayedSignature = newSignature;
                _displayedRedBoxesDip = redBoxesDip;
                _displayedCatalogHighlightsDip = catalogHighlights;
                break;

            case HighlightStabilityPolicy.Decision.KeepDisplayed:
                // Nothing NEW to draw — but the fallback (non-exclusion)
                // hide/clear step above already blanked this window's
                // content for the capture, so it must be explicitly
                // redrawn with whatever was ALREADY displayed, or the
                // held highlight would just vanish instead of staying
                // solid (defeating the entire point of this policy).
                // Under capture exclusion, the window was never touched
                // this tick, so there's nothing to redo.
                if (!usingCaptureExclusion && _displayedSignature.Length > 0)
                {
                    DrawAndShow(overlay, target.Value, scale, _displayedRedBoxesDip, _displayedCatalogHighlightsDip);
                }
                break;
        }
    }

    /// <summary>Repositions the overlay to the target window's current bounds and draws exactly the given highlights — the ONE place TickAsync actually mutates the overlay's visible content, called either with this tick's freshly computed result (Decision.Display) or a retained previous one being redrawn after a fallback-path hide/clear (Decision.KeepDisplayed) — see HighlightStabilityPolicy's own doc.</summary>
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
    /// </summary>
    private static CatalogHighlights ToDip(CatalogSubstitutionScanner.CatalogAnnotations annotations, double scale)
    {
        DipRect? greenRowDip = null;
        string? savingsLabel = null;
        if (annotations.GreenHighlight is { } green)
        {
            greenRowDip = ToDip(green.Left, green.Top, green.Right, green.Bottom, scale);
            savingsLabel = green.SavingsDisplay;
        }

        DipRect? yellowRowDip = null;
        if (annotations.YellowHighlight is { } yellow)
        {
            yellowRowDip = ToDip(yellow.Left, yellow.Top, yellow.Right, yellow.Bottom, scale);
        }

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

        return new CatalogHighlights(
            greenRowDip, savingsLabel,
            yellowRowDip,
            bestLargeDip, bestLargeLabel,
            bestSmallDip, bestSmallLabel,
            sortBadgeAnchorDip, sortBadgeText, sortBadgeIsSorted);
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
        _displayedRedBoxesDip = Array.Empty<DipRect>();
        _displayedCatalogHighlightsDip = null;
        _consecutiveEmptyTicks = 0;
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
    /// ROUND 2 (W-T85 bug 2) — best-effort, throttled diagnostic for
    /// SelectionRowNormalizer's own bitmap preprocessing (see that class's
    /// and SelectionRowColorDetector's own doc for why this exists: the
    /// exact color bounds are an UNVERIFIED estimate, and this is what
    /// proves or disproves the detector actually firing on Will's real
    /// screen without another live-diagnosis round). Logs only the Y-range
    /// count/list — never any OCR'd text, never a screenshot — so there is
    /// nothing PHI-adjacent here at all, unlike the window-title/column-band
    /// diagnostics elsewhere in this class.
    /// </summary>
    private void LogSelectionBandsIfChanged(IReadOnlyList<(int Top, int Bottom)> bands)
    {
        var signature = bands.Count == 0 ? "" : string.Join(",", bands.Select(b => $"{b.Top}-{b.Bottom}"));
        if (signature == _lastLoggedSelectionBandsSignature) return;
        _lastLoggedSelectionBandsSignature = signature;

        OcrLogger.LogTiming(bands.Count == 0
            ? "OrderAssist: selection-band normalizer found 0 bands this tick"
            : $"OrderAssist: selection-band normalizer inverted {bands.Count} band(s) this tick: [{signature}]");
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
}
