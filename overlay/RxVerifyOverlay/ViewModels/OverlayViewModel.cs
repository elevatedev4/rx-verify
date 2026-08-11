using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using RxVerifyOverlay.Diagnostics;
using RxVerifyOverlay.Engine;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.Ocr;
using RxVerifyOverlay.Uia;

namespace RxVerifyOverlay.ViewModels;

/// <summary>
/// One row in the compact table (leading status icon | Field | Source |
/// Entered), in the FIXED field order within its category — never
/// re-sorted. Per the researched Twinlist/WCAG conventions (see
/// MainWindow.xaml row DataTemplate): the status icon leads the row (not
/// a trailing dot), the Source cell stays neutral, and the Entered
/// cell's whole background tints by match state with the icon as the
/// PRIMARY signal and color as reinforcement only.
/// </summary>
public sealed class VerdictRowViewModel
{
    public string FieldKey { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public VerdictStatus Status { get; init; }
    public string Explanation { get; init; } = "";
    public string ReasonCode { get; init; } = "";
    public string SourceValue { get; init; } = "";
    public string EnteredValue { get; init; } = "";

    /// <summary>
    /// INTEGRATED MODE (Integrated/IntegratedOverlayCoordinator.cs): the
    /// entered field's on-screen physical-pixel bounds, from
    /// FieldReader.ReadEnteredFieldRects — null (never a zero rect) when
    /// no rect was captured for this field this refresh (element not
    /// found, PioneerRx mid-redraw, etc.). The boxes layer only draws a
    /// verdict outline for a row where this is non-null.
    /// </summary>
    public Rectangle? ScreenRect { get; init; }

    /// <summary>
    /// Fields whose values are digit sequences where transposed/dropped
    /// digits are the realistic error mode (NPI, phone, quantity,
    /// refills) — these get a monospace/tabular-figure font in the row
    /// template so a transposition is visible as a column-misalignment,
    /// not just a string diff. Everything else (names, addresses, dates,
    /// drug, sig) keeps the default proportional font.
    /// </summary>
    private static readonly HashSet<string> TabularFieldKeys = new()
    {
        "prescriberNpi",
        "prescriberPhone",
        "quantity",
        "refills"
    };

    public bool IsTabularField => TabularFieldKeys.Contains(FieldKey);

    /// <summary>
    /// True only for the drug row, only while its real verdict is still
    /// being looked up in the background (see OverlayViewModel.RefreshAsync's
    /// two-phase refresh + EngineClient.VerifyAsync's skipDrugLookup).
    /// MainWindow.xaml swaps the row's leading glyph for a spinner
    /// (indeterminate ProgressBar) while this is true, instead of
    /// showing the placeholder "!" yellow glyph as if it were a real
    /// "needs a look" verdict — the drug NAME itself (SourceValue/
    /// EnteredValue) is already showing at this point, only the
    /// comparison judgment is still pending.
    /// </summary>
    public bool IsPending => ReasonCode == Models.ReasonCodes.PendingDrugLookup;

    /// <summary>
    /// Glyph for the status — WCAG requires color never be the ONLY
    /// signal, so this is the PRIMARY indicator and cell color is
    /// reinforcement. "!" (not "?") for yellow/uncertain: a question
    /// mark reads as "unknown meaning to the pharmacist", where "!" reads
    /// as "needs a look", which matches the yellow verdict's actual
    /// intent (not_provided/unverified, not necessarily unknown).
    /// </summary>
    public string Glyph => Status switch
    {
        VerdictStatus.Green => "✓",  // ✓
        VerdictStatus.Yellow => "!",
        VerdictStatus.Red => "✗",    // ✗
        _ => "!"
    };

    /// <summary>Row hover/tooltip text — the reason code + explanation move here instead of being always-visible, to keep the compact table small (see MainWindow.xaml).</summary>
    public string TooltipText => string.IsNullOrEmpty(ReasonCode) ? Explanation : $"[{ReasonCode}] {Explanation}";
}

/// <summary>
/// One of the 3 compact-table categories (Patient / Prescriber / Rx —
/// see Models/EngineModels.cs FieldCategories). Status is the
/// worst-status-wins rollup of its Rows (CategoryRollup.RollUp),
/// recomputed by OverlayViewModel every refresh.
/// </summary>
public sealed class CategoryViewModel : INotifyPropertyChanged
{
    public string Name { get; init; } = "";
    public ObservableCollection<VerdictRowViewModel> Rows { get; } = new();

    private VerdictStatus _status = VerdictStatus.Green;
    public VerdictStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Glyph));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(HeaderStatusText));
        }
    }

    /// <summary>
    /// True once this category has at least one row with real data
    /// (i.e. RefreshAsync populated it from a verify() result) — false
    /// while waiting for PioneerRx / before the first successful read.
    /// Per Will's live-test feedback: a category with NO data must render
    /// GRAY, not green — green means "data present AND matches", not
    /// "nothing to complain about yet". See MainWindow.xaml's category
    /// header/box background triggers, which check this BEFORE Status.
    /// </summary>
    private bool _hasData;
    public bool HasData
    {
        get => _hasData;
        set
        {
            if (_hasData == value) return;
            _hasData = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Glyph));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(HeaderStatusText));
        }
    }

    /// <summary>Glyph for the rolled-up category status — same mapping as VerdictRowViewModel.Glyph. Shows a neutral dash when there's no data at all (see HasData).</summary>
    public string Glyph => !HasData
        ? "–"
        : Status switch
        {
            VerdictStatus.Green => "✓",
            VerdictStatus.Yellow => "?",
            VerdictStatus.Red => "✗",
            _ => "?"
        };

    /// <summary>
    /// Text label for the category header spelling out the same
    /// worst-status-wins rollup the glyph/color already convey — per
    /// Will's live-test feedback, the header should show words, not just
    /// a symbol/color. "Match" only when every row is green; "Partial
    /// match" when at least one row is yellow and none are red; "Verify"
    /// when at least one row is red (W-T10 item 3: renamed from "Exact
    /// match"/"Likely Error" respectively). "No data" when the category
    /// has nothing to roll up yet (see HasData).
    /// </summary>
    public string StatusText => !HasData
        ? "No data"
        : Status switch
        {
            VerdictStatus.Green => "Match",
            VerdictStatus.Yellow => "Partial match",
            VerdictStatus.Red => "Verify",
            _ => "Partial match"
        };

    /// <summary>
    /// "— StatusText", for rendering immediately to the right of the
    /// category title on the header row (e.g. "Patient — Exact match") —
    /// per Will's W-T9 item 4 feedback, this must sit right next to the
    /// title (not pinned to the box's right edge) and must NOT be
    /// italic. Kept as its own bindable property (rather than a XAML
    /// StringFormat/MultiBinding) so MainWindow.xaml can bind one
    /// TextBlock directly next to Name with no extra markup.
    /// </summary>
    public string HeaderStatusText => $"— {StatusText}";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Pure worst-status-wins rollup: Red beats Yellow beats Green. Kept as a
/// standalone static method (no ViewModel/UIA dependencies) so it's
/// directly unit-testable — see Tests/CategoryRollupTests.cs.
/// </summary>
public static class CategoryRollup
{
    public static VerdictStatus RollUp(IEnumerable<VerdictStatus> statuses)
    {
        var hasRed = false;
        var hasYellow = false;
        foreach (var status in statuses)
        {
            if (status == VerdictStatus.Red) hasRed = true;
            else if (status == VerdictStatus.Yellow) hasYellow = true;
        }

        if (hasRed) return VerdictStatus.Red;
        if (hasYellow) return VerdictStatus.Yellow;
        return VerdictStatus.Green;
    }
}

/// <summary>
/// Orchestrates: attach to PioneerRx -> read both panels via FieldReader
/// -> call the engine via EngineClient -> expose the 3 rolled-up
/// categories (Patient/Prescriber/Rx — see Models/EngineModels.cs
/// FieldCategories) for MainWindow's compact table to bind to. This is
/// the only place that combines all three pieces, so the overlay UI
/// itself stays a thin renderer.
/// </summary>
public sealed class OverlayViewModel : INotifyPropertyChanged
{
    private readonly EngineClient _engineClient;
    private readonly OverlaySettings _settings;
    private readonly OcrFieldReader _ocrFieldReader;
    private readonly IOverlayVisibilityController? _overlayVisibilityController;

    /// <summary>
    /// Post-review diagnostic-visibility fix: whether SetWindowDisplayAffinity
    /// (WDA_EXCLUDEFROMCAPTURE) is actually active on the live overlay
    /// window right now — see IOverlayVisibilityController.
    /// IsExcludedFromCapture. Read live (not cached) so it always
    /// reflects MainWindow's current state; null only when no controller
    /// is wired up at all (e.g. a test host with no live WPF window).
    /// Threaded into every Timing: log line (RxLogFormatter.
    /// FormatTimingLine) and the "Copy logs" blob so a troubleshoot
    /// report always says which capture path (OS-level exclusion vs.
    /// hide/show fallback) was actually live for that read.
    /// </summary>
    private bool? CurrentCaptureExclusionActive => _overlayVisibilityController?.IsExcludedFromCapture;

    /// <summary>The 3 categories, always in FieldCategories.Order — MainWindow.xaml binds directly to this.</summary>
    public ObservableCollection<CategoryViewModel> Categories { get; } = new();

    /// <summary>
    /// VerifyOCR headline diagnostic (branch brief item 3): "OCR: &lt;total&gt;ms
    /// · &lt;chars&gt; chars" after every OCR source read, or an error
    /// summary on failure — always visible in MainWindow.xaml so Will can
    /// judge speed/quality on his real screen without digging into the
    /// log file (Ocr/OcrLogger.cs has the same numbers plus the full raw
    /// text, for a permanent record).
    /// </summary>
    private string _ocrStatusText = "OCR: not read yet.";
    public string OcrStatusText
    {
        get => _ocrStatusText;
        private set { _ocrStatusText = value; OnPropertyChanged(); }
    }

    /// <summary>Full raw OCR text from the most recent source read — bound to the "Raw OCR text" expander in MainWindow.xaml so Will can eyeball text quality live, not just in the log file.</summary>
    private string _lastOcrRawText = "";
    public string LastOcrRawText
    {
        get => _lastOcrRawText;
        private set { _lastOcrRawText = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// True from immediately before RefreshFromOcrAsync awaits
    /// OcrFieldReader.ReadSourceFromOcrAsync until that await returns
    /// (success or failure — always reset in a finally). Purely
    /// cosmetic: MainWindow.xaml binds a small indeterminate spinner to
    /// this next to OcrStatusText so the pharmacist sees "reading in
    /// progress" instead of a static line during the capture+OCR window.
    /// A property set either side of an already-running await can never
    /// slow down the capture/OCR pipeline itself — see Owner's request
    /// ("NOT if this will substantially slow down the program"). Always
    /// false on the Uia path (no OCR read happens there at all).
    /// </summary>
    private bool _isOcrReading;
    public bool IsOcrReading
    {
        get => _isOcrReading;
        private set { if (_isOcrReading == value) return; _isOcrReading = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// "Rx is not an escript" banner (Task 1b) — set only when the OCR
    /// path's OcrSourceUsability.Evaluate returns NotAnEscript (healthy
    /// word count, but no "Escript" tab-label marker anywhere in the
    /// capture — see Ocr/EscriptMarkerDetector). Reset to empty at the
    /// top of every OCR/UIA refresh pass so a stale banner from a
    /// previous Rx never lingers once the pharmacist moves on. Bound in
    /// MainWindow.xaml via HasNonEscriptMessage, reusing the same red
    /// banner STYLE as the e-script Notes banner (not the same
    /// collection — Notes carries actual NCPDP note text, this carries
    /// an app-level status message, and conflating the two would make
    /// BuildCurrentLogBlob's "Notes" section misleading).
    /// </summary>
    private string _nonEscriptMessage = "";
    public string NonEscriptMessage
    {
        get => _nonEscriptMessage;
        private set { if (_nonEscriptMessage == value) return; _nonEscriptMessage = value; OnPropertyChanged(); HasNonEscriptMessage = !string.IsNullOrEmpty(value); }
    }

    private bool _hasNonEscriptMessage;
    public bool HasNonEscriptMessage
    {
        get => _hasNonEscriptMessage;
        private set { if (_hasNonEscriptMessage == value) return; _hasNonEscriptMessage = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// E-script free-text notes (item 6, NCPDP Note element — see
    /// Parsing/EscriptTreeParser.ParseNotes), rendered in red at the
    /// bottom of the overlay only when non-empty (see MainWindow.xaml's
    /// HasNotes-gated notes Border). Populated from FieldReader.SourceNotes
    /// on every RefreshAsync; cleared alongside the categories on every
    /// early-return branch.
    /// </summary>
    public ObservableCollection<string> Notes { get; } = new();

    /// <summary>True once Notes has at least one entry — drives the notes Border's visibility in MainWindow.xaml (collapsed/no-op when there's nothing to show).</summary>
    private bool _hasNotes;
    public bool HasNotes
    {
        get => _hasNotes;
        private set { if (_hasNotes == value) return; _hasNotes = value; OnPropertyChanged(); }
    }

    private string _statusMessage = "Not attached to PioneerRx yet.";
    public string StatusMessage
    {
        get => _statusMessage;
        private set { _statusMessage = value; OnPropertyChanged(); }
    }

    private int _greenCount;
    public int GreenCount { get => _greenCount; private set { _greenCount = value; OnPropertyChanged(); } }

    private int _yellowCount;
    public int YellowCount { get => _yellowCount; private set { _yellowCount = value; OnPropertyChanged(); } }

    private int _redCount;
    public int RedCount { get => _redCount; private set { _redCount = value; OnPropertyChanged(); } }

    /// <summary>
    /// Monotonic counter, bumped at the START of every RefreshAsync call.
    /// The background drug-lookup phase (RefreshDrugFieldAsync) captures
    /// the value current at its own start and checks it again before
    /// applying its result — if a NEWER refresh (e.g. the pharmacist hit
    /// Refresh again, or PioneerRx moved to a different Rx) has started
    /// in the meantime, the stale drug result is silently dropped instead
    /// of being written into rows that no longer belong to it.
    /// </summary>
    private int _refreshGeneration;

    /// <summary>
    /// The PioneerRx window title (e.g. "Edit Rx - 1234567 - ...") as of
    /// the last RefreshAsync attach, or null if no window was attached
    /// (window not found / attach failed). Not bound to any UI element —
    /// it only exists so BuildCurrentLogBlob can label the copied log with
    /// which Rx it came from. Overwritten (not accumulated) on every
    /// RefreshAsync, same "current Rx only" scoping as everything else
    /// BuildCurrentLogBlob reads.
    /// </summary>
    private string? _lastRxWindowTitle;

    /// <summary>
    /// ADDENDUM item 7 (round 4 — stale-box false-assurance hazard): the
    /// Rx identity (PioneerRxWindow.RxNumber — the parsed value, not the
    /// full title, so it compares apples-to-apples with a fresh attach
    /// elsewhere) captured at ATTACH time for whichever RefreshAsync call
    /// is currently in flight — see _pendingRxIdentity below, which this
    /// gets copied from once PopulateRows actually applies that refresh's
    /// rows. Null before the first successful populate, or after
    /// ClearCategories.
    /// </summary>
    public string? CurrentVerdictsRxIdentity { get; private set; }

    /// <summary>
    /// The Rx identity captured at the TOP of RefreshAsync (right after a
    /// successful attach), copied into CurrentVerdictsRxIdentity by
    /// PopulateRows — kept as a separate field (not written directly to
    /// CurrentVerdictsRxIdentity at attach time) so
    /// CurrentVerdictsRxIdentity only ever changes at the SAME moment the
    /// displayed rows themselves change, never a moment earlier while the
    /// OLD rows are still on screen (that earlier-update race is exactly
    /// what let Integrated/RxIdentityGate see a false "already matches,
    /// nothing stale" during the gap between attach and the new rows
    /// actually rendering).
    /// </summary>
    private string? _pendingRxIdentity;

    /// <summary>
    /// The structured OCR word+bounding-box list from the most recent OCR
    /// source read (see RefreshFromOcrAsync) — same data OcrLogger writes
    /// to the %TEMP% log file, kept here too so BuildCurrentLogBlob can
    /// include it in the "Copy logs" blob without re-reading the screen.
    /// Empty (not the previous Rx's words) whenever the method is Uia or
    /// no OCR read has happened yet.
    /// </summary>
    private IReadOnlyList<OcrWord> _lastOcrWords = Array.Empty<OcrWord>();

    /// <summary>
    /// INTEGRATED MODE: the most recent ReadEnteredFieldRects() result
    /// (see FieldReader), captured once per RefreshAsync call right after
    /// ReadEntered() and consumed by BuildRow to populate each row's
    /// ScreenRect. Empty (never null) before the first successful read or
    /// after ClearCategories — see that method's doc for why every
    /// per-refresh field like this one is reset on every early-return
    /// branch.
    /// </summary>
    private IReadOnlyDictionary<string, Rectangle> _lastEnteredRects = new Dictionary<string, Rectangle>();

    /// <summary>
    /// Latency fix instrumentation (see Diagnostics/RefreshTiming.cs) for
    /// the most recent refresh — null before the first successful
    /// refresh, or after ClearCategories resets it. Read by
    /// BuildCurrentLogBlob for the "Copy logs" blob; the OcrLogger.
    /// LogTiming call happens as each phase completes (see
    /// RefreshFromOcrAsync/RefreshFromUiaAsync/ApplyDrugResult) rather
    /// than reading this field, since the persistent log is append-only
    /// and this field mutates in place (Phase 2 fills in Phase2Ms on the
    /// SAME instance Phase 1 already logged from).
    /// </summary>
    private RefreshTiming? _lastTiming;

    public OverlayViewModel(EngineClient engineClient, OverlaySettings settings, IOverlayVisibilityController? overlayVisibilityController = null, OcrFieldReader? ocrFieldReader = null)
    {
        _engineClient = engineClient;
        _settings = settings;
        _overlayVisibilityController = overlayVisibilityController;
        _ocrFieldReader = ocrFieldReader ?? new OcrFieldReader();

        // Categories are created once, in fixed display order, and their
        // Rows are cleared/repopulated on every refresh below — never
        // recreated, so MainWindow's binding to Categories itself never
        // needs to change, only the items inside it.
        foreach (var name in FieldCategories.Order)
        {
            Categories.Add(new CategoryViewModel { Name = name });
        }
    }

    /// <summary>
    /// One full refresh pass: find the window, read both panels, call
    /// the engine, update the bound rows. Safe to call repeatedly (e.g.
    /// on a timer or a manual "Refresh" button) — every failure mode
    /// (window not found, UIA read error, engine error) becomes a
    /// StatusMessage rather than an exception, so the overlay never
    /// crashes mid-shift.
    ///
    /// TWO-PHASE (per Will's live-test feedback: a noticeable delay after
    /// clicking Refresh before ANYTHING updated). Name/DOB/address/
    /// prescriber/sig/quantity/refills comparisons are cheap string/date/
    /// number logic; only the drug field's identity lookup is slow (it
    /// consults the bundled ~130k-concept local NDC dataset — see
    /// rx-verify src/drug/index.ts LocalNdcProvider). So:
    ///   Phase 1 (awaited, blocks this method briefly): call the engine
    ///     with skipDrugLookup=true. This never touches the NDC dataset
    ///     at all (see EngineClient.VerifyAsync / src/cli.ts), so it
    ///     returns fast. Every field except drug gets its real verdict;
    ///     the drug row shows its name/value immediately with a PENDING
    ///     indicator (spinner) instead of a verdict glyph.
    ///   Phase 2 (fire-and-forget, does NOT block this method or the UI
    ///     thread): the real drug lookup, via RefreshDrugFieldAsync. When
    ///     it resolves, only the drug row (and its category's rollup +
    ///     the overall summary) is updated in place.
    /// </summary>
    public async Task RefreshAsync()
    {
        var generation = ++_refreshGeneration;

        // Latency fix: end-to-end timing breakdown for this refresh (see
        // Diagnostics/RefreshTiming.cs). One instance per refresh,
        // mutated in place as each stage completes, and later mutated
        // AGAIN (Phase2Ms) by ApplyDrugResult once the background drug
        // lookup resolves — see that method's doc.
        var timing = new RefreshTiming();
        _lastTiming = timing;

        var attachStopwatch = Stopwatch.StartNew();
        using var window = PioneerRxWindow.TryAttach();
        timing.AttachMs = attachStopwatch.ElapsedMilliseconds;

        if (window is null)
        {
            _lastRxWindowTitle = null;
            StatusMessage = "Waiting for a PioneerRx Pre-Check/Edit/New Rx window...";
            ClearCategories();
            UpdateSummary(null);
            return;
        }

        // Latency-fix diagnosis (uia-read-latency branch): whether the
        // above TryAttach hit its fast path — see
        // PioneerRxWindow.WasAttachCacheHit / RefreshTiming.AttachCacheHit.
        timing.AttachCacheHit = window.WasAttachCacheHit;

        try { _lastRxWindowTitle = window.WindowElement.Name; } catch { _lastRxWindowTitle = null; }

        // ADDENDUM item 7: captured here (attach time), applied to
        // CurrentVerdictsRxIdentity only once PopulateRows actually
        // renders this refresh's rows — see _pendingRxIdentity's doc for
        // why those two moments are deliberately kept separate.
        _pendingRxIdentity = window.RxNumber;

        FieldReader reader;
        PrescriptionRecord entered;
        try
        {
            // ENTERED side unchanged regardless of Method: still
            // UIA/AutomationId via FieldReader (see Uia/FieldReader.cs
            // ReadEntered). FieldReader now times its own find-vs-read
            // split internally (LastReadFindMs/LastReadValueMs) — read
            // those directly into timing's sub-parts instead of wrapping
            // the whole call in one opaque stopwatch, so UiaMs (computed
            // from those two sub-parts, see RefreshTiming.cs) can't drift.
            reader = new FieldReader(window);
            entered = reader.ReadEntered();
            timing.UiaFindMs = reader.LastReadFindMs;
            timing.UiaReadMs = reader.LastReadValueMs;

            // INTEGRATED MODE: capture each entered field's on-screen
            // rect right alongside its value, while the elements ReadEntered()
            // just resolved are still cached (see FieldReader.ElementCache) —
            // effectively free here, vs. a fresh find-by-AutomationId walk
            // later. Populated regardless of Method (Ocr/Uia only affects
            // the SOURCE side below) since entered-field rects never
            // depend on which source-reading path is active.
            _lastEnteredRects = reader.ReadEnteredFieldRects();
        }
        catch (Exception ex)
        {
            StatusMessage = $"UIA read failed: {ex.Message}. Try 'Dump UIA Tree' to diagnose.";
            return;
        }

        // SOURCE side branches on _settings.Method (runtime-selectable —
        // see MainWindow.xaml's method toggle and Models/OverlaySettings.
        // cs VerificationMethod). Ocr is the default: screen-region
        // capture + local OCR, no tab switch. Uia reads the Escript
        // tab's structured UIA tree directly (the original "Verify"
        // behavior, pre-VerifyOCR).
        if (_settings.Method == VerificationMethod.Uia)
        {
            await RefreshFromUiaAsync(reader, entered, generation, timing);
        }
        else
        {
            await RefreshFromOcrAsync(window, entered, generation, timing);
        }
    }

    /// <summary>OCR source path — screen-region capture + local OCR, NO tab switch. Replaces FieldReader.ReadSource()'s Escript-tab UIA-tree walk entirely. See Uia/OcrFieldReader.cs.</summary>
    private async Task RefreshFromOcrAsync(PioneerRxWindow window, PrescriptionRecord entered, int generation, RefreshTiming timing)
    {
        NonEscriptMessage = ""; // reset before this pass decides fresh — see property doc

        OcrCaptureResult ocrResult;
        IsOcrReading = true;
        try
        {
            ocrResult = await _ocrFieldReader.ReadSourceFromOcrAsync(window, _settings, _overlayVisibilityController);
        }
        finally
        {
            IsOcrReading = false;
        }

        if (generation != _refreshGeneration) return; // superseded by a newer refresh while we were awaiting OCR

        // ocrResult already carries Stopwatch-timed capture/OCR splits
        // (see Uia/OcrFieldReader.cs ReadSourceFromOcrAsync) — feed them
        // into this refresh's timing breakdown rather than re-timing.
        timing.CaptureRegionResolveMs = ocrResult.CaptureRegionResolveMs;
        timing.CaptureHideWaitMs = ocrResult.CaptureHideWaitMs;
        timing.CaptureBlitMs = ocrResult.CaptureBlitMs;
        timing.OcrMs = ocrResult.OcrMs;

        LastOcrRawText = ocrResult.RawText;
        OcrStatusText = ocrResult.Error is not null
            ? $"OCR: error — {ocrResult.Error}"
            : $"OCR: {ocrResult.TotalMs}ms (capture {ocrResult.CaptureMs}ms + ocr {ocrResult.OcrMs}ms) · {ocrResult.CharCount} chars · scale {ocrResult.OcrScaleFactor:0.#}x";

        if (ocrResult.Error is not null)
        {
            StatusMessage = ocrResult.Error;
            ClearCategories();
            UpdateSummary(null);
            return;
        }

        var ocrWords = ocrResult.Words;
        _lastOcrWords = ocrWords;

        // OCR path has no notes extraction (v0 or v1 — see
        // src/ocr/parseEscriptOcr.ts's documented "notes" gap: no
        // PrescriptionRecord.notes field exists to extract into) — always
        // empty here, vs. the UIA path's SourceNotes.
        UpdateNotes(Array.Empty<string>());

        // Task 1 (non-escript blank state): OcrSourceUsability.Evaluate
        // combines the existing word-count pre-gate with
        // EscriptMarkerDetector's fuzzy "Escript" tab-label check, in
        // that priority order — a sparse capture (window mid-load) can't
        // reliably prove the marker is ABSENT, so TooSparse always wins
        // over NotAnEscript even if the marker also happens to be
        // missing from those few words. See Ocr/OcrSourceUsability.cs.
        var usability = OcrSourceUsability.Evaluate(ocrWords);
        if (usability == OcrSourceUsabilityDecision.TooSparse)
        {
            // v1: a cheap word-count pre-gate on the raw OCR output, NOT
            // a check of a parsed record (parsing now happens inside the
            // TS engine call below) — see Uia/OcrFieldReader.cs
            // IsSourceUsable doc for why.
            StatusMessage = "OCR didn't find enough text on the captured e-script image to attempt a comparison. " +
                             "Check the capture region (Engine settings) and the raw OCR text below.";
            ClearCategories();
            UpdateSummary(null);
            return;
        }

        if (usability == OcrSourceUsabilityDecision.NotAnEscript)
        {
            // Owner's request: when the current Rx isn't an e-script at
            // all (no "Escript" tab-label marker in the capture — a
            // transfer, a faxed image, etc.), don't run the engine
            // compare and blank out the verdict table instead of
            // showing per-field comparisons against data that was never
            // meant to line up. NonEscriptMessage drives the red banner
            // in MainWindow.xaml (see property doc — reuses the Notes
            // banner's STYLE, not its collection).
            StatusMessage = "Rx is not an escript.";
            ClearCategories(); // must run BEFORE setting NonEscriptMessage below — ClearCategories resets it too, for every other early-return path that isn't this one
            NonEscriptMessage = "Rx is not an escript";
            UpdateSummary(null);
            return;
        }

        // Phase 1: fast pass, skips the drug lookup entirely. Sends the
        // raw OCR words straight to the engine (v1) — see
        // Engine/EngineClient.cs VerifyAsync(IReadOnlyList&lt;OcrWord&gt;, ...).
        var engineStopwatch = Stopwatch.StartNew();
        var fastResult = await _engineClient.VerifyAsync(ocrWords, entered, skipDrugLookup: true);
        timing.EngineMs = engineStopwatch.ElapsedMilliseconds;

        if (generation != _refreshGeneration) return; // superseded by a newer refresh while we were awaiting

        if (!string.IsNullOrEmpty(fastResult.Error))
        {
            StatusMessage = fastResult.Error;
            return;
        }

        var renderStopwatch = Stopwatch.StartNew();
        PopulateRows(fastResult);
        UpdateSummary(fastResult.Summary);
        timing.RenderMs = renderStopwatch.ElapsedMilliseconds;

        StatusMessage = $"Checked {DateTime.Now:h:mm:ss tt} ({timing.Phase1TotalMs}ms). Drug lookup running…";
        OcrLogger.LogTiming(RxLogFormatter.FormatTimingLine(timing, CurrentCaptureExclusionActive));

        // Phase 2: NOT awaited — runs in the background so this method
        // (and whatever caller triggered it, e.g. the Refresh button
        // click handler) returns immediately. See RefreshDrugFieldAsync
        // for the staleness guard against a newer refresh superseding
        // this one before it resolves.
        _ = RefreshDrugFieldAsync(ocrWords, entered, generation, timing);
    }

    /// <summary>UIA source path — reads the Escript tab's structured UIA tree directly via FieldReader.ReadSource() (the original "Verify" behavior). No OCR/screen capture involved.</summary>
    private async Task RefreshFromUiaAsync(FieldReader reader, PrescriptionRecord entered, int generation, RefreshTiming timing)
    {
        // OCR is inert in this mode — no capture/OCR ever runs, so this
        // just tells the pharmacist which mode is active instead of
        // showing stale/misleading OCR timing text.
        OcrStatusText = "OCR off — reading Escript tab directly";
        LastOcrRawText = "";
        _lastOcrWords = Array.Empty<OcrWord>();

        // The OCR-only NotAnEscript banner (Task 1) never applies on this
        // path — reset it so it can't linger on screen after the
        // pharmacist switches from OCR to Escript-tab-direct mode
        // mid-session. The Uia path's own SourceUnavailableReason flow
        // (below) is the equivalent surface here.
        NonEscriptMessage = "";

        PrescriptionRecord source;
        try
        {
            source = reader.ReadSource();
            UpdateNotes(reader.SourceNotes);
        }
        catch (Exception ex)
        {
            StatusMessage = $"UIA source read failed: {ex.Message}. Try 'Dump UIA Tree' to diagnose.";
            ClearCategories();
            UpdateSummary(null);
            return;
        }

        if (!reader.IsStructuredSourceAvailable(source))
        {
            // Mirrors the OCR path's "not usable" branch above: a clear
            // status message + cleared categories rather than a
            // half-populated/misleading table.
            StatusMessage = reader.SourceUnavailableReason ?? "Open the Escript tab to verify this e-script.";
            ClearCategories();
            UpdateSummary(null);
            return;
        }

        // Phase 1: fast pass, skips the drug lookup entirely — same
        // two-phase shape as the OCR path, just with a PrescriptionRecord
        // source instead of raw OCR words (see Engine/EngineClient.cs
        // VerifyAsync(PrescriptionRecord, ...)).
        var engineStopwatch = Stopwatch.StartNew();
        var fastResult = await _engineClient.VerifyAsync(source, entered, skipDrugLookup: true);
        timing.EngineMs = engineStopwatch.ElapsedMilliseconds;

        if (generation != _refreshGeneration) return; // superseded by a newer refresh while we were awaiting

        if (!string.IsNullOrEmpty(fastResult.Error))
        {
            StatusMessage = fastResult.Error;
            return;
        }

        var renderStopwatch = Stopwatch.StartNew();
        PopulateRows(fastResult);
        UpdateSummary(fastResult.Summary);
        timing.RenderMs = renderStopwatch.ElapsedMilliseconds;

        StatusMessage = $"Checked {DateTime.Now:h:mm:ss tt} ({timing.Phase1TotalMs}ms). Drug lookup running…";
        OcrLogger.LogTiming(RxLogFormatter.FormatTimingLine(timing, CurrentCaptureExclusionActive));

        // Phase 2: NOT awaited — see RefreshFromOcrAsync's identical note.
        _ = RefreshDrugFieldAsync(source, entered, generation, timing);
    }

    /// <summary>
    /// Last screen signature observed by WatchAsync (see below) — null
    /// until the first watch tick. Compared against the current tick's
    /// signature to decide whether a full RefreshAsync is warranted.
    /// </summary>
    private PioneerRxWindow.ScreenSignature? _lastWatchedSignature;

    /// <summary>
    /// AUTO-WATCH (W-T9 item 5): replaces the old fixed-interval "just
    /// re-run the full verify every 5s" polling with cheap
    /// change-detection. Call this on a short timer (MainWindow.xaml.cs
    /// uses ~1s) instead of RefreshAsync directly.
    ///
    /// Each tick calls PioneerRxWindow.GetScreenSignature(), which is
    /// drastically cheaper than a full RefreshAsync: it only enumerates
    /// top-level desktop windows and reads ONE window's title text (no
    /// FieldReader panel walk, no Escript tree read, no engine subprocess
    /// call) — see PioneerRxWindow.GetScreenSignature for how the Rx
    /// number is parsed straight out of the title
    /// ("Edit Rx - &lt;rx number&gt; - ...").
    ///
    /// A full RefreshAsync only actually runs when:
    ///   - the pre-check/edit/new-rx screen just appeared (wasn't present
    ///     last tick), or
    ///   - it's present but the Rx number/title changed since last tick
    ///     (pharmacist moved to a different Rx).
    /// If the screen disappeared since last tick, the categories are
    /// cleared (mirrors RefreshAsync's own "window not found" branch)
    /// without needing a full refresh. If nothing changed, this is a
    /// no-op beyond the cheap signature read — no engine call, no UIA
    /// panel read, so it's safe to poll frequently without hammering
    /// PioneerRx or the machine.
    /// </summary>
    public async Task WatchAsync()
    {
        var signature = PioneerRxWindow.GetScreenSignature();
        var previous = _lastWatchedSignature;
        _lastWatchedSignature = signature;

        if (!signature.Present)
        {
            if (previous is { Present: true })
            {
                StatusMessage = "Waiting for a PioneerRx Pre-Check/Edit/New Rx window...";
                ClearCategories();
                UpdateSummary(null);
            }
            return;
        }

        var changed = previous is null || !previous.Value.Present || previous.Value.RxNumber != signature.RxNumber;
        if (!changed) return;

        // ADDENDUM item 7 (round 4 — same staleness hazard the integrated
        // boxes layer fixes via RxIdentityGate, applied here trivially:
        // GetScreenSignature's own RxNumber comparison above already IS
        // the identity check, no separate tracking needed): a different
        // Rx just appeared. Clear the displayed rows IMMEDIATELY, before
        // the (relatively slow) UIA+engine refresh below produces fresh
        // ones, so the pharmacist is never shown the PREVIOUS Rx's green/
        // red verdicts superimposed on a new prescription's screen, even
        // briefly. Harmless when the previous branch already cleared
        // (screen just appeared from nothing) — only meaningfully new
        // behavior for the "still present, but a DIFFERENT Rx" case.
        ClearCategories();
        UpdateSummary(null);

        await RefreshAsync();
    }

    /// <summary>
    /// Rebuilds every category's Rows from a VerifyResult, in
    /// FieldOrder.Fields order within each category. Used by Phase 1 of
    /// RefreshAsync to populate every row; Phase 2 (RefreshDrugFieldAsync)
    /// only ever replaces the single "drug" row, but goes through the
    /// same BuildRow helper below so the two phases can never drift apart
    /// on how a FieldVerdict becomes a VerdictRowViewModel.
    /// </summary>
    private void PopulateRows(VerifyResult result)
    {
        // ADDENDUM item 7: the displayed rows are about to change to
        // whatever this refresh found — CurrentVerdictsRxIdentity moves to
        // match at this EXACT moment, not any earlier (see
        // _pendingRxIdentity's doc).
        CurrentVerdictsRxIdentity = _pendingRxIdentity;

        foreach (var category in Categories) category.Rows.Clear();

        foreach (var field in FieldOrder.Fields)
        {
            var verdict = result.Verdicts.FirstOrDefault(v => v.Field == field);
            if (verdict is null) continue; // defensive: engine contract guarantees all 13 fields, but never crash the UI on a contract drift

            var categoryName = FieldCategories.CategoryByField.TryGetValue(field, out var mapped)
                ? mapped
                : FieldCategories.Rx; // defensive fallback for a future field the engine adds that this map hasn't been updated for yet
            var category = Categories.First(c => c.Name == categoryName);

            category.Rows.Add(BuildRow(field, verdict));
        }

        foreach (var category in Categories)
        {
            RollUpCategory(category);
            category.HasData = category.Rows.Count > 0;
        }
    }

    /// <summary>
    /// Recomputes one category's rolled-up Status from its current Rows,
    /// excluding any row whose field is in FieldCategories.
    /// RollupExcludedFields (currently patientAddress/prescriberAddress)
    /// from the rollup INPUT — those rows stay visible in the table, they
    /// just can never move the category's header status. Shared by
    /// PopulateRows (full refresh) and RefreshDrugFieldAsync (drug-only
    /// refresh) so the exclusion rule can't drift between the two.
    /// </summary>
    private static void RollUpCategory(CategoryViewModel category)
    {
        var rollupStatuses = category.Rows
            .Where(r => !FieldCategories.RollupExcludedFields.Contains(r.FieldKey))
            .Select(r => r.Status);
        category.Status = CategoryRollup.RollUp(rollupStatuses);
    }

    private VerdictRowViewModel BuildRow(string field, FieldVerdict verdict) => new()
    {
        FieldKey = field,
        DisplayName = FieldOrder.DisplayNames[field],
        Status = verdict.Status,
        Explanation = verdict.Explanation,
        ReasonCode = verdict.ReasonCode,
        SourceValue = verdict.SourceValue ?? "(not provided)",
        EnteredValue = verdict.EnteredValue ?? "(not provided)",
        // INTEGRATED MODE — see _lastEnteredRects doc. Not present
        // (rather than default) when this field's rect wasn't captured
        // this refresh.
        ScreenRect = _lastEnteredRects.TryGetValue(field, out var rect) ? rect : null
    };

    /// <summary>
    /// Phase 2 of RefreshAsync: the real (slow) drug-identity lookup,
    /// run in the background. Re-runs a full verify() (skipDrugLookup
    /// false/omitted) rather than adding a third "drug-only" CLI mode —
    /// the non-drug comparisons are cheap enough that recomputing them
    /// costs nothing measurable, and reusing the exact same engine call
    /// shape keeps this file's engine contract to just the one
    /// skipDrugLookup flag. Only the "drug" row (and its category
    /// rollup + overall summary) from this second result is ever
    /// applied — every other row already rendered in Phase 1 is left
    /// untouched, so the pharmacist never sees the rest of the panel
    /// flicker or reset while this runs.
    /// </summary>
    private async Task RefreshDrugFieldAsync(IReadOnlyList<OcrWord> ocr, PrescriptionRecord entered, int generation, RefreshTiming timing)
    {
        var phase2Stopwatch = Stopwatch.StartNew();
        VerifyResult result;
        try
        {
            result = await _engineClient.VerifyAsync(ocr, entered, skipDrugLookup: false);
        }
        catch (Exception ex)
        {
            if (generation == _refreshGeneration) StatusMessage = $"Drug lookup failed: {ex.Message}";
            return;
        }

        ApplyDrugResult(result, generation, timing, phase2Stopwatch.ElapsedMilliseconds);
    }

    /// <summary>
    /// UIA-mode counterpart to the OcrWord overload above — same
    /// two-phase drug lookup, just re-running verify() with the
    /// structured PrescriptionRecord source instead of raw OCR words
    /// (see Engine/EngineClient.cs VerifyAsync(PrescriptionRecord, ...)).
    /// Shares ApplyDrugResult with the OCR overload so the two phase-2
    /// paths can never drift on how a VerifyResult becomes the updated
    /// drug row.
    /// </summary>
    private async Task RefreshDrugFieldAsync(PrescriptionRecord source, PrescriptionRecord entered, int generation, RefreshTiming timing)
    {
        var phase2Stopwatch = Stopwatch.StartNew();
        VerifyResult result;
        try
        {
            result = await _engineClient.VerifyAsync(source, entered, skipDrugLookup: false);
        }
        catch (Exception ex)
        {
            if (generation == _refreshGeneration) StatusMessage = $"Drug lookup failed: {ex.Message}";
            return;
        }

        ApplyDrugResult(result, generation, timing, phase2Stopwatch.ElapsedMilliseconds);
    }

    /// <summary>
    /// Common Phase-2 apply logic for both RefreshDrugFieldAsync
    /// overloads: replaces only the "drug" row (and its category rollup
    /// + overall summary) from a freshly-resolved VerifyResult, dropping
    /// the result entirely if a newer refresh has since superseded this
    /// one (see _refreshGeneration doc).
    /// </summary>
    private void ApplyDrugResult(VerifyResult result, int generation, RefreshTiming timing, long phase2Ms)
    {
        if (generation != _refreshGeneration) return; // a newer refresh superseded this one — drop the stale result

        if (!string.IsNullOrEmpty(result.Error))
        {
            StatusMessage = result.Error;
            return;
        }

        var drugVerdict = result.Verdicts.FirstOrDefault(v => v.Field == "drug");
        if (drugVerdict is null) return;

        var drugCategoryName = FieldCategories.CategoryByField.TryGetValue("drug", out var mapped) ? mapped : FieldCategories.Rx;
        var drugCategory = Categories.FirstOrDefault(c => c.Name == drugCategoryName);
        if (drugCategory is null) return;

        var existingIndex = -1;
        for (var i = 0; i < drugCategory.Rows.Count; i++)
        {
            if (drugCategory.Rows[i].FieldKey == "drug") { existingIndex = i; break; }
        }
        if (existingIndex < 0) return; // the panel was cleared (e.g. window lost) before this resolved — nothing to update

        // Replacing (not mutating) the row is required: VerdictRowViewModel's
        // properties are init-only / not INotifyPropertyChanged, so a
        // fresh instance in the ObservableCollection is what actually
        // triggers the DataTemplate to re-render this row's glyph/colors.
        drugCategory.Rows[existingIndex] = BuildRow("drug", drugVerdict);

        RollUpCategory(drugCategory);
        UpdateSummary(result.Summary);

        // Same RefreshTiming instance Phase 1 already logged from (see
        // RefreshAsync) — mutate it in place and log a follow-up line
        // carrying the full breakdown PLUS the phase2 add-on (see
        // RxLogFormatter.FormatTimingLine's "- phase2 +Nms" suffix).
        timing.Phase2Ms = phase2Ms;
        StatusMessage = $"Checked {DateTime.Now:h:mm:ss tt}.";
        OcrLogger.LogTiming(RxLogFormatter.FormatTimingLine(timing, CurrentCaptureExclusionActive));
    }

    /// <summary>
    /// Clears every category's rows (leaving the 3 category shells in
    /// place) — used by every early-return branch of RefreshAsync/
    /// WatchAsync (window not found, screen disappeared, source
    /// unusable). MUST also reset OcrStatusText/LastOcrRawText (in
    /// addition to _lastOcrWords) — otherwise a previous Rx's raw OCR
    /// text/PHI would keep sitting in those bound properties after the
    /// PioneerRx window closes/changes, and BuildCurrentLogBlob would
    /// still emit it into the "Copy logs" blob even though RxWindowTitle
    /// and every row/category had already gone empty. This is the same
    /// "current Rx only, never accumulated" scoping as everything else
    /// BuildCurrentLogBlob reads.
    /// </summary>
    private void ClearCategories()
    {
        foreach (var category in Categories)
        {
            category.Rows.Clear();
            category.Status = VerdictStatus.Green; // Status is meaningless with no data; HasData=false is what actually drives the gray "no data" display (see MainWindow.xaml).
            category.HasData = false;
        }

        UpdateNotes(Array.Empty<string>());
        _lastOcrWords = Array.Empty<OcrWord>();
        _lastEnteredRects = new Dictionary<string, Rectangle>();
        OcrStatusText = "OCR: not read yet.";
        LastOcrRawText = "";
        _lastTiming = null;
        NonEscriptMessage = ""; // NotAnEscript's own branch (RefreshFromOcrAsync) re-sets this AFTER calling ClearCategories — every other caller wants it cleared

        // ADDENDUM item 7: no verdicts currently displayed for ANY Rx —
        // see CurrentVerdictsRxIdentity's doc. _pendingRxIdentity too, so
        // a stale attach-time value can't leak into some LATER refresh's
        // PopulateRows if this ClearCategories call happens to run
        // between that later refresh's attach and its own PopulateRows
        // (shouldn't happen given the current call graph, but costs
        // nothing to keep both in lockstep defensively).
        CurrentVerdictsRxIdentity = null;
        _pendingRxIdentity = null;
    }

    /// <summary>Replaces Notes' contents and recomputes HasNotes — shared by RefreshAsync's ReadSource call and every ClearCategories early-return.</summary>
    private void UpdateNotes(IReadOnlyList<string> notes)
    {
        Notes.Clear();
        foreach (var note in notes) Notes.Add(note);
        HasNotes = Notes.Count > 0;
    }

    /// <summary>
    /// Debug helper: dumps the full UIA tree of the currently-attached
    /// PioneerRx window as plain text, for Will to diff against
    /// FieldMap.cs. Returns null (with a StatusMessage explaining why)
    /// if no window is currently attached.
    /// </summary>
    public string? DumpCurrentWindowTree()
    {
        using var window = PioneerRxWindow.TryAttach();
        if (window is null)
        {
            StatusMessage = "No PioneerRx window found to dump.";
            return null;
        }

        var walker = new UiaTreeWalker(window.WindowElement);
        return walker.DumpTree();
    }

    /// <summary>
    /// "Copy logs" button (MainWindow.xaml/.cs OnCopyLogsClick): builds the
    /// single text blob to put on the clipboard, entirely from whatever is
    /// ALREADY bound to the overlay UI right now (Categories/Rows,
    /// OcrStatusText/LastOcrRawText/_lastOcrWords, Notes, StatusMessage,
    /// summary counts) plus the app version/commit and current method/Rx
    /// window title. Nothing here is accumulated across calls or across
    /// Rx's — every field read is the SAME state the compact table is
    /// currently rendering, so the blob always reflects only the Rx
    /// currently under review (see RxLogSnapshot doc). The actual text
    /// formatting is a pure function (RxLogFormatter.BuildLogBlob) so it's
    /// unit-testable without a live OverlayViewModel.
    ///
    /// <paramref name="redactPatient"/> backs the "Copy logs (no HIPAA)"
    /// button (MainWindow.xaml.cs OnCopyLogsNoHipaaClick): same blob, but
    /// with patient identifiers stripped — see RxLogFormatter.BuildLogBlob's
    /// doc for exactly what is/isn't redacted.
    /// </summary>
    public string BuildCurrentLogBlob(bool redactPatient = false)
    {
        var snapshot = new RxLogSnapshot
        {
            CapturedAt = DateTime.Now,
            AppVersion = AppDiagnostics.GetAppVersion(),
            CommitSha = AppDiagnostics.GetCommitSha(),
            Method = _settings.Method == VerificationMethod.Uia ? "Escript tab (direct UIA read)" : "OCR",
            RxWindowTitle = _lastRxWindowTitle,
            StatusMessage = StatusMessage,
            OcrStatusText = OcrStatusText,
            RawOcrText = LastOcrRawText,
            OcrWords = _lastOcrWords,
            Timing = _lastTiming,
            CaptureExclusionActive = CurrentCaptureExclusionActive,
            Categories = Categories
                .Select(c => new RxLogCategorySnapshot(
                    c.Name,
                    c.StatusText,
                    c.Rows.Select(r => new RxLogFieldSnapshot(
                        r.FieldKey,
                        r.DisplayName,
                        r.Status.ToString(),
                        r.SourceValue,
                        r.EnteredValue,
                        r.ReasonCode,
                        r.Explanation)).ToList()))
                .ToList(),
            Notes = Notes.ToList(),
            GreenCount = GreenCount,
            YellowCount = YellowCount,
            RedCount = RedCount
        };

        return RxLogFormatter.BuildLogBlob(snapshot, redactPatient);
    }

    private void UpdateSummary(VerifySummary? summary)
    {
        GreenCount = summary?.Green ?? 0;
        YellowCount = summary?.Yellow ?? 0;
        RedCount = summary?.Red ?? 0;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
