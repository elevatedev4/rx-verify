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
        // re-confirming it's still current (see the final check right
        // before RepositionPhysical/SetHighlights/Show) — see
        // TickGenerationGate's doc for the exact race this closes. The
        // CTS is real, best-effort cancellation on top of that (see the
        // _tickCts field doc) — using var disposes it the moment this
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
            return;
        }

        var target = scan.Target;

        var overlay = EnsureOverlayWindow();

        // SELF-OCCLUSION GUARD — see class doc. Only hidden if it was
        // actually showing; nothing to hide on the very first tick.
        if (_windowShown)
        {
            overlay.HideAndClear();
            _windowShown = false;
            await Task.Delay(CaptureSettleDelayMs, cts.Token);
        }

        using var bitmap = EscriptImageCapture.CaptureRegion(target.Value.Bounds);
        var ocrResult = await _ocrEngine.RecognizeAsync(bitmap, cts.Token);

        var scale = DpiScaleFor(target.Value.Handle);

        var redBoxesDip = new List<DipRect>();
        CatalogHighlights? catalogHighlights = null;

        switch (target.Value.Kind)
        {
            case OrderAssistWindowKind.CreateRecommendedOrders:
                foreach (var highlight in CreateRecommendedOrdersScanner.FindZeroQuantityHighlights(ocrResult.Words))
                {
                    redBoxesDip.Add(ToDip(highlight.Left, highlight.Top, highlight.Right, highlight.Bottom, scale));
                }
                break;

            case OrderAssistWindowKind.CatalogSubstitution:
                catalogHighlights = ToDip(CatalogSubstitutionScanner.Analyze(ocrResult.Words), scale);
                break;
        }

        if (redBoxesDip.Count == 0 && (catalogHighlights is null || IsEmpty(catalogHighlights)))
        {
            // Nothing to highlight this tick (e.g. no zero quantities, or
            // no substitution meets the 25% bar) — stay hidden rather
            // than show an empty overlay window. Could ALSO mean column
            // resolution itself failed (a bad/partial OCR capture, or the
            // window's actual header text doesn't match what this app
            // expects) — see LogColumnDiagnosticsIfNeeded, which tells
            // the two apart and only logs the latter.
            LogColumnDiagnosticsIfNeeded(target.Value.Kind, ocrResult.Words);
            return;
        }

        // REVIEW FIX (blocking): the actual fix — see TickAsync's own doc
        // above and TickGenerationGate's doc. Everything above this point
        // only computed a result; nothing touched the overlay's visible
        // state yet, so bailing out here (rather than earlier) is always
        // safe. If SetEnabled ran at all since this tick started
        // (enabling OR disabling — either means this result is stale),
        // discard it instead of showing/repositioning a highlight the
        // pharmacist may have already turned off, with no future tick
        // left to correct it.
        if (!TickGenerationGate.IsStillCurrent(tickGeneration, _generation)) return;

        // See OrderModeBottomInsetDip's own doc — the highlight window
        // stops short of the target window's own bottom edge, where
        // Pioneer's own action buttons (e.g. "New") live.
        var overlayHeight = OrderModeOverlayBoundsRule.TrimmedHeightPhysical(target.Value.Bounds.Height, OrderModeBottomInsetDip, scale);
        overlay.RepositionPhysical(target.Value.Bounds.X, target.Value.Bounds.Y, target.Value.Bounds.Width, overlayHeight);
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

    private static bool IsEmpty(CatalogHighlights catalog) =>
        catalog.GreenRowDip is null &&
        catalog.YellowRowDip is null &&
        catalog.BestLargePackageDip is null &&
        catalog.BestSmallPackageDip is null &&
        catalog.SortBadgeAnchorDip is null;

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
    private void LogColumnDiagnosticsIfNeeded(OrderAssistWindowKind kind, IReadOnlyList<OcrWord> words)
    {
        try
        {
            var rows = TableRowGrouper.GroupIntoRows(words);
            var headerRowCount = HeaderBandLocator.CountHeaderRows(rows);
            if (headerRowCount == 0 || headerRowCount >= rows.Count)
            {
                LogColumnFailureOnce("(no header row detected)", Array.Empty<string>());
                return;
            }

            var headerRows = rows.Take(headerRowCount).ToList();
            var bands = ColumnResolver.BuildPartitionedColumnBands(headerRows);
            var labels = bands.Select(b => b.Label).ToList();

            var expectedLabels = kind == OrderAssistWindowKind.CreateRecommendedOrders
                ? new[] { CreateRecommendedOrdersScanner.OrderQuantityHeaderLabel }
                : new[] { CatalogSubstitutionScanner.SupplierHeaderLabel, CatalogSubstitutionScanner.RebateCostPerUnitHeaderLabel };

            var missing = expectedLabels.Where(expected => !labels.Any(label => NormalizeForComparison(label) == NormalizeForComparison(expected))).ToList();
            if (missing.Count == 0) return; // resolution actually succeeded -- an empty highlight result this tick is a legitimate "nothing to flag", not a failure worth logging

            LogColumnFailureOnce(string.Join(", ", missing), labels);
        }
        catch
        {
            // Best-effort diagnostic only — see LogNoMatchDiagnosticsIfNeeded's posture.
        }

        void LogColumnFailureOnce(string missingDescription, IReadOnlyList<string> resolvedLabels)
        {
            var signature = $"{kind}:{string.Join("|", resolvedLabels)}";
            if (signature == _lastLoggedColumnFailureSignature) return;
            _lastLoggedColumnFailureSignature = signature;

            var bandsDisplay = resolvedLabels.Count > 0 ? string.Join(" | ", resolvedLabels) : "(none)";
            OcrLogger.LogTiming($"OrderAssist[{kind}]: column resolution failed for {missingDescription}. Resolved header bands this tick: [{bandsDisplay}]");
        }
    }

    /// <summary>Mirrors ColumnResolver's own (private) NormalizeLabel — whitespace-collapsed, case-insensitive — for this diagnostic's own label comparison only; never used to influence the actual resolution ColumnResolver.ResolveExact performs.</summary>
    private static string NormalizeForComparison(string label) =>
        string.Join(" ", label.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
}
