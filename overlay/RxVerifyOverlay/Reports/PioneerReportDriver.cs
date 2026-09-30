using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.Ocr;
using RxVerifyOverlay.Uia;

namespace RxVerifyOverlay.Reports;

/// <summary>
/// Real IPioneerReportDriver implementation — drives PioneerRx's ribbon
/// (Analysis &gt; Financial Reports, Third Party &gt; Payments) with FlaUI/
/// UIA3 the same way Uia/PioneerRxWindow.cs and Uia/UiaTreeWalker.cs
/// already read it, plus FlaUI.Core.Input.Keyboard for the keystroke
/// fallbacks Will's own Macro Express macros use (see Reports/recipes/
/// README-macro-strings.txt).
///
/// UNVERIFIED AGAINST THE LIVE APP (flagged per this branch's own risk
/// posture — see RxVerifyOverlay.csproj's TFM comment for the established
/// pattern of calling out exactly this kind of gap): this repo has no
/// confirmed UIA dump of PioneerRx's Financial Reports / Payments
/// screens (unlike Uia/FieldMap.cs's Pre-Check fields, which cite two
/// real dumps) and there is no Windows machine or dotnet SDK available
/// to build/run this file while writing it. Every AutomationId this
/// class WOULD prefer is therefore unknown, so every lookup below goes
/// by control TYPE + visible NAME/text (row text, button text, tab text
/// — all taken verbatim from the GOAL brief's recorded walkthrough and
/// the macro strings) with a keyboard fallback modeled on the macros.
/// EXPECT TO RE-TUNE the exact Name strings/ribbon access keys once this
/// runs against Will's real PioneerRx — nothing here has been UI-tested,
/// only reasoned from the two sources available (his recording + his
/// macros).
///
/// SAFETY (GOAL brief step 5): every wait below is bounded
/// (WaitUntilAsync's timeout); EnsurePioneerForeground is called
/// immediately before every keystroke/click; RunFinancialReport/
/// RunPaymentsExport never throw for an ordinary automation problem —
/// they catch broadly and return ReportRunResult.Failed so
/// ReportsCoordinator moves on to the next report (see that class's own
/// policy doc) — only an already-cancelled CancellationToken surfaces as
/// OperationCanceledException, matching IPioneerReportDriver's contract.
/// </summary>
public sealed class PioneerReportDriver : IPioneerReportDriver
{
    private const string PioneerProcessName = "PioneerPharmacy";
    private static readonly TimeSpan DefaultReportTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DialogTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SaveVerifyTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Round 7 (W-T92, Will 2026-09-30: "it needs to wait until it is run
    /// ... not try to save the report until that has happened, otherwise
    /// the report will save empty"). How long
    /// WaitForReportCompletionStatus polls Pioneer's own bottom-left
    /// report-generation status text (every PollInterval, ~250ms) for "the
    /// report has completed" before aborting the save step — see
    /// ReportCompletionStatus's own doc for the full decision. Deliberately
    /// its own fixed timeout, independent of ReportTimeoutPlan.CalculateTimeout
    /// (that one gates the PREVIEW WINDOW/toolbar appearing at all, via
    /// IsPreviewReady, and is sized per-report off each report's own
    /// MacroRunTime); this one gates the save step specifically, after the
    /// preview window is already up, and applies identically to every
    /// report that reaches it.
    /// </summary>
    private static readonly TimeSpan ReportCompletionStatusTimeout = TimeSpan.FromSeconds(180);

    /// <summary>
    /// Round 2 fix (Will's first real run - foreground re-check used a
    /// single fixed 150ms sleep before giving up): how long
    /// EnsurePioneerForeground will keep polling after BringToForeground
    /// before concluding focus truly didn't move - Pioneer is slow, a
    /// single short sleep isn't a fair test.
    /// </summary>
    private static readonly TimeSpan ForegroundSettleTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Round 2 (GOAL brief step 2ii): settle time after each ribbon keyboard keystroke/combo, up from the old 200/300/500ms fixed sleeps - Pioneer is slow to react to key-tip input.</summary>
    private static readonly TimeSpan KeyboardStepWait = TimeSpan.FromMilliseconds(1500);

    /// <summary>Round 2 (GOAL brief step 2iii): how long OpenRibbonScreen polls (every PollInterval) for the target screen to actually appear after each navigation strategy, before trying the next one.</summary>
    private static readonly TimeSpan RibbonConfirmationTimeout = TimeSpan.FromSeconds(6);

    /// <summary>Round 2 (GOAL brief step 1): cap on how many element lines a diagnostic dump ever writes to the log, so a huge Pioneer tree can't flood the run log.</summary>
    private const int MaxDiagnosticDumpLines = 150;

    /// <summary>Round 2 (GOAL brief step 1): "descendants to depth 3" - how many levels below the main window the diagnostic dump walks.</summary>
    private const int MaxDiagnosticDumpDepth = 3;

    /// <summary>Round 3 (GOAL brief fix 1): FindMainWindow keeps re-enumerating the desktop for up to this long before giving up, rather than a single snapshot attempt - Pioneer's real shell window may not have appeared yet the instant this is called.</summary>
    private static readonly TimeSpan MainWindowFindTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Round 3 (GOAL brief fix 2): "success = a new top-level window owned by the Pioneer pid ... within 5 s" - the outer confirmation every row-selection strategy is checked against. Round 6: no longer requires a title match at all - see WaitForNewPioneerWindow's own doc.</summary>
    private static readonly TimeSpan ReportParametersConfirmationTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Round 4 (W-T92 follow-up, GOAL brief step 1): "wait (&lt;=10 s) for a NEW Pioneer-owned top-level window" - the explicit locate step RunFinancialReport runs right after row selection, before any date keystroke goes out. Deliberately longer than ReportParametersConfirmationTimeout (that one is polled three times, once per row-selection strategy - this one only needs to run once, after row selection has already succeeded). Round 6: used only as WaitForReportParametersWindow's fallback-search timeout now that the normal path resolves a cached handle instead of re-polling.</summary>
    private static readonly TimeSpan ReportParametersWindowLocateTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Round 4 (W-T92 follow-up, GOAL brief step 2): "Small (~100 ms) settles between keys" - the per-action pause ReplayReportParameterKeyPlan uses after every Tab/TypeText/ArrowDown/F12 step (TypeText's own per-character settle is NativeInput.CharSettleDelay, applied inside NativeInput.TypeUnicodeText).</summary>
    private static readonly TimeSpan KeyEntrySettleDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>Round 6 (W-T92 follow-up, GOAL brief step 4: "wait the macro's settle delay ... use the existing MacroRunTime/settle constant in ReportCatalog if there is one, else 1000ms"). No such constant exists on ReportCatalogEntry — MacroRunTime there feeds ReportTimeoutPlan's PREVIEW-wait timeout, not a popup settle delay — so this is the dedicated one: the pause ForegroundAndSettleReportParametersWindow takes after bringing the "Report Parameters" popup to the foreground and before the first keystroke, matching how Will's own macro waits before typing.</summary>
    private static readonly TimeSpan ReportParametersSettleDelay = TimeSpan.FromMilliseconds(1000);

    /// <summary>Round 3 (GOAL brief fix 2b): grid keyboard strategy's own inner wait after Enter, before falling back to a direct double-click.</summary>
    private static readonly TimeSpan GridKeyboardEnterConfirmationTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Round 3 (GOAL brief fix 2a): "cap node count ~5,000" for the unlimited-depth RawViewWalker search inside FinancialReportsWorkArea.</summary>
    private const int MaxRowSearchNodes = 5000;

    /// <summary>Round 3 (GOAL brief fix 2b): "step Down one row at a time (max 60)".</summary>
    private const int MaxGridKeyboardSteps = 60;

    /// <summary>Round 3 (GOAL brief fix 2, diagnostic dump extension): "walk the RAW view under FinancialReportsWorkArea to depth 8".</summary>
    private const int MaxRowDiagnosticDumpDepth = 8;

    /// <summary>Same spirit as MaxDiagnosticDumpLines*2 elsewhere in this file - caps the raw-view walk itself well above what will actually be printed, so a huge grid can't make this best-effort walk slow.</summary>
    private const int MaxRowDiagnosticDumpNodes = 3000;

    /// <summary>
    /// Reviewer round 7 non-blocking fix: WaitForReportCompletionStatus's
    /// FindReportCompletionStatusElement polls every ~250ms for up to 180s
    /// - an unbounded FindAllDescendants over a whole window on every tick
    /// (especially the _mainWindow fallback, the full ribbon window) would
    /// be needlessly expensive over that many ticks. Same idea as
    /// MaxDiagnosticDumpDepth/MaxRowDiagnosticDumpDepth elsewhere in this
    /// file, sized like the deeper row-dump cap since a status/progress
    /// label can plausibly sit several panes deep in either window.
    /// </summary>
    private const int ReportStatusSearchMaxDepth = 8;

    /// <summary>
    /// Round 3 (GOAL brief fix 2b): "click once inside the grid (work-area
    /// pane rect, ~40% across, first data row)". Owner's screenshot: the
    /// grid starts roughly 130px below the WINDOW top at 100% scaling
    /// with ~12px rows - approximated here relative to the work-area
    /// pane's own top (not the window's) since the pane's bounding
    /// rectangle already excludes whatever header/toolbar sits above it;
    /// a small fixed offset into the first visible row is the standard
    /// "click somewhere safely inside row 1" heuristic FlaUI grids don't
    /// otherwise expose a row-0 element for without already having found
    /// it (the very thing this click is trying to bootstrap).
    /// </summary>
    private const int FirstDataRowOffsetFromWorkAreaTop = 20;

    private const string FinancialReportsWorkAreaAutomationId = "FinancialReportsWorkArea";

    private static readonly HashSet<ControlType> RowSearchControlTypes = new()
    {
        ControlType.DataItem, ControlType.ListItem, ControlType.Custom, ControlType.Edit, ControlType.Text
    };

    private const uint WM_CLOSE = 0x0010;
    private const int SW_RESTORE = 9;

    private UIA3Automation? _automation;
    private AutomationElement? _mainWindow;
    private int _pioneerProcessId;

    /// <summary>
    /// Round 3 (GOAL brief fix 2c): the same IOcrEngine/WindowsMediaOcrEngine
    /// already used by the Verify-mode OCR path (Ocr/WindowsMediaOcrEngine.cs)
    /// - no new OCR dependency. Injectable (see the internal constructor
    /// below) purely so a future test could supply a fake IOcrEngine;
    /// PioneerReportDriver itself still can't be unit tested end-to-end
    /// off Windows (FlaUI/UIA throughout), so today's tests exercise the
    /// pure matching logic this engine's output feeds (ReportGridOcrMatcher)
    /// instead - see Reports/ReportGridOcr.cs.
    /// </summary>
    private readonly IOcrEngine _ocrEngine;

    public PioneerReportDriver() : this(new WindowsMediaOcrEngine())
    {
    }

    internal PioneerReportDriver(IOcrEngine ocrEngine)
    {
        _ocrEngine = ocrEngine;
    }

    /// <summary>
    /// The report-preview window's own AutomationElement/native handle,
    /// captured the moment IsPreviewReady finds it (see FindPreviewWindow)
    /// — review fix (safety blocker 2): ClosePreview must target THIS
    /// specific window, never "whatever is currently foreground", so a
    /// focus change after Save As closes can never make it close
    /// Pioneer's main window instead. Cleared back to null/Zero at the
    /// end of ClosePreview.
    /// </summary>
    private AutomationElement? _previewWindowElement;
    private IntPtr _previewWindowHandle = IntPtr.Zero;

    /// <summary>
    /// Round 6 (W-T92 follow-up): the "Report Parameters" popup's native
    /// handle, set by TrySelectAndOpenReportRow the moment its own
    /// title-agnostic native-window-inventory diff (WaitForNewPioneerWindow)
    /// sees a new Pioneer-owned top-level window appear — WaitForReportParametersWindow
    /// then resolves THIS exact handle to an AutomationElement instead of
    /// re-searching, so it can never land on a different window than the
    /// one row selection actually confirmed opened. Reset to Zero at the
    /// top of every RunFinancialReport/TestDateEntry call so a stale
    /// handle from a previous report in the same batch is never reused.
    /// </summary>
    private IntPtr _reportParametersWindowHandle = IntPtr.Zero;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    // Round 6 (W-T92 follow-up, GOAL brief step 1): raw Win32 top-level
    // window enumeration for EnumerateVisibleTopLevelWindowsForProcess -
    // deliberately independent of FlaUI/UIA3Automation.GetDesktop() (the
    // basis of EnumeratePioneerOwnedTopLevelWindows below), which is what
    // missed the "Report Parameters" popup in Will's build da39140 run:
    // that popup either never exposed a UIA Name at all, or FlaUI's own
    // desktop-children snapshot simply hadn't picked it up yet by the
    // time the old title match polled it. GetWindowTextLength (not
    // GetWindowText) is used everywhere a title's LENGTH is logged, so
    // this enumeration can never capture or leak the title text itself.
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    private const uint GW_OWNER = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>Round 5 (W-T92 follow-up, belt-and-braces item): reads a key's toggle state (bit 0 of the return value) - used only to LOG whether NumLock is off before the first Report Parameters keystroke goes out, per the owner's numpad/scan-code theory. Never used to toggle NumLock - see ReplayReportParameterKeyPlan's doc for why.</summary>
    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    private const int VK_NUMLOCK = 0x90;

    // Round 4 reviewer fix (blocking finding 1): the raw SendInput/
    // KEYEVENTF_UNICODE P/Invoke for Unicode date-text entry now lives in
    // Reports/NativeInput.cs (NativeInput.TypeUnicodeText) instead of here —
    // extracted so its INPUT/KEYBDINPUT/MOUSEINPUT/HARDWAREINPUT struct
    // layout (the union originally declared only KEYBDINPUT, so
    // Marshal.SizeOf<INPUT>() was 32 bytes instead of the real Win32
    // struct's 40 on x64/28 on x86 — SendInput silently sent nothing
    // against that wrong cbSize) can be unit-tested without any FlaUI/WPF
    // dependency. See that file's own doc for the full story.

    /// <summary>Plain Win32 POINT (screen coordinates) for WindowFromPoint - review fix (blocker 2), see TryActivateRibbonElement/IsPointOwnedByPioneer.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private UIA3Automation GetOrCreateAutomation() => _automation ??= new UIA3Automation();

    /// <summary>
    /// Round 3 rewrite (owner's first real run: this resolved to pid
    /// 24828, handle=0x0, name='&lt;untitled&gt;' - a Pioneer helper process
    /// with no visible UI at all - while the real shell was pid 20664;
    /// the old loop returned the FIRST desktop-enumeration match with no
    /// ranking whatsoever). Enumerates ALL top-level desktop windows every
    /// attempt, ranks them via the pure MainWindowSelector (prefers the
    /// largest visible WindowsForms10.* window with a non-zero handle and
    /// non-empty title), and retries for up to MainWindowFindTimeout
    /// before giving up. Logs every candidate on the first attempt and
    /// (if it never succeeds) the last attempt, plus a one-line "still
    /// looking" note on attempts in between - full candidate dumps on
    /// every ~250ms poll tick would flood the run log for no benefit.
    ///
    /// Review fix (PR #11, non-blocking): <paramref name="ct"/> is
    /// checked at the top of every attempt (poll granularity, same
    /// PollInterval idiom as WaitUntilAsync elsewhere) so Stop interrupts
    /// the retry loop instead of always running the full ~5s - stays
    /// false/does-not-throw per this method's own contract, it just
    /// returns sooner with a "stopped" log line rather than surfacing
    /// OperationCanceledException.
    /// </summary>
    public bool FindMainWindow(Action<string> log, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + MainWindowFindTimeout;
        var attempt = 0;
        IReadOnlyList<MainWindowCandidate> lastCandidates = Array.Empty<MainWindowCandidate>();

        while (true)
        {
            if (ct.IsCancellationRequested)
            {
                log("FindMainWindow: stopped.");
                _mainWindow = null;
                return false;
            }

            attempt++;
            var (element, chosen, candidates) = TryFindMainWindowOnce();
            lastCandidates = candidates;

            if (attempt == 1)
            {
                log($"FindMainWindow attempt {attempt}: {candidates.Count} candidate window(s) on the desktop.");
                foreach (var candidate in candidates)
                {
                    log(MainWindowCandidateLog.Format(candidate));
                }
            }

            if (element is not null && chosen is { } chosenCandidate)
            {
                log($"FindMainWindow: selected pid={chosenCandidate.ProcessId} handle=0x{chosenCandidate.Handle.ToInt64():X} class='{chosenCandidate.ClassName}'.");
                _mainWindow = element;
                _pioneerProcessId = chosenCandidate.ProcessId;
                BringToForeground(element);
                return true;
            }

            if (DateTime.UtcNow >= deadline) break;

            log($"FindMainWindow attempt {attempt}: no eligible candidate yet - retrying...");
            Thread.Sleep(PollInterval);
        }

        log($"FindMainWindow: no eligible PioneerRx main window found within {MainWindowFindTimeout.TotalSeconds:0}s. Last seen:");
        foreach (var candidate in lastCandidates)
        {
            log(MainWindowCandidateLog.Format(candidate));
        }

        _mainWindow = null;
        return false;
    }

    /// <summary>One enumeration + ranking pass — the impure half (real AutomationElements/Process lookups) feeding MainWindowSelector's pure ranking decision. Returns the chosen AutomationElement (looked back up by handle, since MainWindowCandidate itself carries no FlaUI reference) alongside the full candidate list, for logging.</summary>
    private (AutomationElement? Element, MainWindowCandidate? Chosen, List<MainWindowCandidate> All) TryFindMainWindowOnce()
    {
        var candidates = new List<MainWindowCandidate>();
        var elementsByHandle = new Dictionary<IntPtr, AutomationElement>();

        try
        {
            var automation = GetOrCreateAutomation();
            var desktop = automation.GetDesktop();

            foreach (var window in desktop.FindAllChildren())
            {
                string? name;
                try { name = window.Name; } catch { name = null; }

                var processId = SafeProcessId(window);
                var handle = SafeNativeHandle(window);
                var className = SafeClassName(window);
                var (width, height) = SafeSize(window);
                var isPioneerProcess = processId != 0 && IsPioneerProcess(processId);
                var isPrecheckFamily = IsPrecheckFamilyTitle(name);

                candidates.Add(new MainWindowCandidate(name ?? string.Empty, processId, handle, className, width, height, isPioneerProcess, isPrecheckFamily));

                if (handle != IntPtr.Zero && !elementsByHandle.ContainsKey(handle))
                {
                    elementsByHandle[handle] = window;
                }
            }
        }
        catch
        {
            // Best-effort - an empty/partial candidate list this attempt
            // just means FindMainWindow's own retry loop tries again.
        }

        var chosen = MainWindowSelector.Choose(candidates);
        if (chosen is null) return (null, null, candidates);

        return elementsByHandle.TryGetValue(chosen.Value.Handle, out var element)
            ? (element, chosen, candidates)
            : (null, null, candidates);
    }

    /// <summary>Never the Pre-Check/Edit/New-Rx window family this app's own Verify/Order modes attach to (Uia/PioneerRxWindow.cs) — the ribbon this driver needs lives on Pioneer's separate main shell window.</summary>
    private static bool IsPrecheckFamilyTitle(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;

        foreach (var prefix in FieldMap.TargetWindowTitlePrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static (int Width, int Height) SafeSize(AutomationElement element)
    {
        try
        {
            var rect = element.BoundingRectangle;
            return ((int)rect.Width, (int)rect.Height);
        }
        catch
        {
            return (0, 0);
        }
    }

    public async Task<ReportRunResult> RunFinancialReport(ReportRunItem item, Action<string> log, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            ct.ThrowIfCancellationRequested();

            // Round 6 (W-T92 follow-up): never carry a stale popup handle
            // into a new report - TrySelectAndOpenReportRow sets this the
            // moment it confirms a new Pioneer window opened, and
            // WaitForReportParametersWindow trusts it as-is.
            _reportParametersWindowHandle = IntPtr.Zero;

            if (!EnsurePioneerForegroundWithRecovery(log)) return ReportRunResult.Failed("Pioneer lost focus", stopwatch.Elapsed);
            if (_mainWindow is null) return ReportRunResult.Failed("PioneerRx main window not found", stopwatch.Elapsed);

            log("Opening Analysis > Financial Reports...");
            if (!await OpenRibbonScreen("Analysis", "Financial Reports", log, ct).ConfigureAwait(false))
                return ReportRunResult.Failed("Could not open Analysis > Financial Reports", stopwatch.Elapsed);

            log($"Selecting report row '{item.Entry.PioneerRowText}'...");
            var rowOpened = TrySelectAndOpenReportRow(item.Entry, log, ct);
            if (!rowOpened)
            {
                WriteRowSelectionDiagnosticDump(item.Entry.PioneerRowText, log);
                return ReportRunResult.Failed($"Report row '{item.Entry.PioneerRowText}' not found", stopwatch.Elapsed);
            }

            log("Locating the 'Report Parameters' window...");
            var reportParametersWindow = WaitForReportParametersWindow(log, ct);
            if (reportParametersWindow is null)
            {
                log("'Report Parameters' window did not appear within the timeout.");
                WriteUiaDiagnosticDump("Report Parameters window", log);
                return ReportRunResult.Failed("'Report Parameters' window did not appear", stopwatch.Elapsed);
            }

            log($"'Report Parameters' window found. class='{SafeClassName(reportParametersWindow)}' title_len={SafeName(reportParametersWindow).Length}");

            if (!ForegroundAndSettleReportParametersWindow(reportParametersWindow, log))
                return ReportRunResult.Failed("'Report Parameters' window lost focus", stopwatch.Elapsed);

            // Round 4 (GOAL brief step 4's fallback path): snapshot every
            // Pioneer-owned top-level window BEFORE any date keystroke goes
            // out, so a later "did a new window appear" check has a clean
            // baseline that doesn't already include this Report Parameters
            // window itself.
            var knownHandlesBeforeParameterEntry = new HashSet<IntPtr>(
                EnumeratePioneerOwnedTopLevelWindows().Select(w => w.Candidate.Handle));

            log("Entering report date parameters...");
            if (!SetReportParameters(item, reportParametersWindow, log)) return ReportRunResult.Failed("Could not enter report date parameters", stopwatch.Elapsed);

            log("Waiting for the report to generate...");
            var previewTimeout = ReportTimeoutPlan.CalculateTimeout(item.Entry.MacroRunTime);
            var waitStopwatch = Stopwatch.StartNew();
            var previewReady = await WaitUntilAsync(() => IsPreviewReady(), previewTimeout, ct).ConfigureAwait(false);

            if (previewReady)
            {
                log($"Report preview detected (path=primary, elapsed={waitStopwatch.Elapsed.TotalSeconds:0.0}s).");
            }
            else
            {
                log($"Report preview not detected within {previewTimeout.TotalSeconds:0}s - checking for a fallback preview window...");
                var (fallbackOutcome, fallbackPreview) = TryFindFallbackPreviewWindow(reportParametersWindow, knownHandlesBeforeParameterEntry, log);

                // BLOCKING review fix: an unexpected (non-preview) window is
                // its own distinct failure - never fall through to "Timed
                // out waiting for the report preview" (misleading) and
                // never treat it as the preview (InvokeExportButton would
                // run against a dialog, and the real preview - if it shows
                // up later - would be left open to confuse the next report).
                if (fallbackOutcome == FallbackPreviewOutcome.UnexpectedWindow)
                {
                    return ReportRunResult.Failed("Unexpected window after F12", stopwatch.Elapsed);
                }

                if (fallbackOutcome == FallbackPreviewOutcome.Preview && fallbackPreview is not null)
                {
                    _previewWindowElement = fallbackPreview;
                    _previewWindowHandle = SafeNativeHandle(fallbackPreview);
                    previewReady = _previewWindowHandle != IntPtr.Zero;
                    if (previewReady)
                    {
                        log($"Report preview detected (path=fallback, elapsed={waitStopwatch.Elapsed.TotalSeconds:0.0}s).");
                    }
                }
            }

            if (!previewReady) return ReportRunResult.Failed("Timed out waiting for the report preview", stopwatch.Elapsed);

            // Round 7 (W-T92, Will 2026-09-30): the preview window/toolbar
            // appearing (IsPreviewReady above) is not the same thing as
            // the report actually finishing - never export/save until
            // Pioneer's own bottom-left status text confirms "the report
            // has completed". Shared here in RunFinancialReport so it
            // applies to every enabled DateRange/AsOfDate report that
            // reaches this F12 -> save path (see ReportCatalog.All),
            // not just Customer A/R.
            var reportCompleted = await WaitForReportCompletionStatus(log, ct).ConfigureAwait(false);
            if (!reportCompleted)
            {
                // Reviewer round 7 non-blocking fix: best-effort close the
                // stuck preview here too (same as the success path below) -
                // never guaranteed (TryCloseWindowSafely is best-effort and
                // ClosePreview clears _previewWindowElement/_previewWindowHandle
                // either way), but it makes it less likely a window left
                // open after THIS timeout is still around to confuse the
                // NEXT report in the same batch - belt-and-braces on top of
                // FindReportCompletionStatusElement's own per-report scoping fix.
                log("Closing the stuck report preview before moving on...");
                ClosePreview(log);
                return ReportRunResult.Failed("Timed out waiting for the report to finish generating - never saved", stopwatch.Elapsed);
            }

            if (!EnsurePioneerForegroundWithRecovery(log)) return ReportRunResult.Failed("Pioneer lost focus", stopwatch.Elapsed);

            log("Exporting to PDF...");
            if (_previewWindowElement is null) return ReportRunResult.Failed("Report preview window was not found", stopwatch.Elapsed);
            if (!InvokeExportButton(_previewWindowElement, "Export to PDF", "PDF", log)) return ReportRunResult.Failed("Could not find the PDF export button", stopwatch.Elapsed);

            var saved = await SaveAsAndVerify(item.OutputFilePath, log, ct).ConfigureAwait(false);
            if (!saved) return ReportRunResult.Failed("Save As did not produce the expected file", stopwatch.Elapsed);

            log("Closing report preview...");
            ClosePreview(log);

            return ReportRunResult.Saved(item.OutputFilePath, stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log($"Automation error: {ex.Message}");
            return ReportRunResult.Failed(ex.Message, stopwatch.Elapsed);
        }
    }

    public async Task<ReportRunResult> RunPaymentsExport(ReportRunItem item, Action<string> log, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            ct.ThrowIfCancellationRequested();

            if (!EnsurePioneerForegroundWithRecovery(log)) return ReportRunResult.Failed("Pioneer lost focus", stopwatch.Elapsed);
            if (_mainWindow is null) return ReportRunResult.Failed("PioneerRx main window not found", stopwatch.Elapsed);

            log("Opening Third Party > Payments...");
            if (!await OpenRibbonScreen("Third Party", "Payments", log, ct).ConfigureAwait(false))
                return ReportRunResult.Failed("Could not open Third Party > Payments", stopwatch.Elapsed);

            log("Selecting the Search tab...");
            var searchTab = FindDescendantByName(_mainWindow!, "Search", ControlType.TabItem);
            if (searchTab is not null) SelectOrInvoke(searchTab);

            if (!EnsurePioneerForegroundWithRecovery(log)) return ReportRunResult.Failed("Pioneer lost focus", stopwatch.Elapsed);

            log("Setting 'Payment Confirmed Between' range...");
            if (!SetPaymentsDateRange(item, log)) return ReportRunResult.Failed("Could not set the payment date range", stopwatch.Elapsed);

            if (!EnsurePioneerForegroundWithRecovery(log)) return ReportRunResult.Failed("Pioneer lost focus", stopwatch.Elapsed);

            log("Searching (Search - F12)...");
            if (!InvokeSearchOrF12(log))
            {
                WriteUiaDiagnosticDump("Search / F12", log);
                return ReportRunResult.Failed("Could not run the search (Search - F12)", stopwatch.Elapsed);
            }

            log("Waiting for search results...");
            var resultsReady = await WaitUntilAsync(() => IsResultsTabReady(), DefaultReportTimeout, ct).ConfigureAwait(false);
            if (!resultsReady) return ReportRunResult.Failed("Timed out waiting for search results", stopwatch.Elapsed);

            if (!EnsurePioneerForegroundWithRecovery(log)) return ReportRunResult.Failed("Pioneer lost focus", stopwatch.Elapsed);

            log("Exporting results to Excel...");
            if (!InvokeExportButton(_mainWindow!, "Export search results to Excel", "Excel", log))
                return ReportRunResult.Failed("Could not find the Excel export command", stopwatch.Elapsed);

            var saved = await SaveAsAndVerify(item.OutputFilePath, log, ct).ConfigureAwait(false);
            if (!saved) return ReportRunResult.Failed("Save As did not produce the expected file", stopwatch.Elapsed);

            return ReportRunResult.Saved(item.OutputFilePath, stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log($"Automation error: {ex.Message}");
            return ReportRunResult.Failed(ex.Message, stopwatch.Elapsed);
        }
    }

    /// <summary>
    /// "Test date entry" button (W-T92 round 3, GOAL brief step 3):
    /// exercises ONLY the date-entry portion of RunFinancialReport's
    /// SetReportParameters/ReplayReportParameterKeyPlan against whatever
    /// "Report Parameters" window is ALREADY open in Pioneer right now -
    /// no ribbon navigation, no report-row selection, and (per the brief)
    /// never F12 - so Will can verify the Tab+type+read-back behavior in
    /// about 5 seconds and paste the resulting log, without ever actually
    /// running or saving a report. FindMainWindow still has to run first
    /// (it's what sets _pioneerProcessId, which
    /// EnumeratePioneerOwnedTopLevelWindows/WaitForReportParametersWindow
    /// both depend on) — this does not open Analysis &gt; Financial Reports
    /// itself; Will opens the popup by hand (or leaves one open from a
    /// prior run) before clicking the button.
    /// </summary>
    public Task<ReportRunResult> TestDateEntry(ReportRunItem item, Action<string> log, CancellationToken ct)
    {
        // Synchronous by design (no FlaUI/UIA call this method makes ever
        // awaits anything - FindMainWindow, WaitForReportParametersWindow,
        // EnsureWindowForeground, ReplayReportParameterKeyPlan are all
        // sync) - Task.FromResult rather than `async` so this doesn't
        // carry a needless async state machine / CS1998 warning; a thrown
        // OperationCanceledException still propagates to the caller
        // exactly the way an awaited one would (ReportsCoordinator always
        // calls this from inside its own try/await).
        var stopwatch = Stopwatch.StartNew();
        try
        {
            ct.ThrowIfCancellationRequested();

            // Round 6 (W-T92 follow-up): this button never runs row
            // selection, so any cached handle would be stale from a
            // previous run - always fall through to WaitForReportParametersWindow's
            // title-agnostic "any non-main Pioneer window" search below.
            _reportParametersWindowHandle = IntPtr.Zero;

            if (!FindMainWindow(log, ct) || _mainWindow is null)
                return Task.FromResult(ReportRunResult.Failed("PioneerRx main window not found", stopwatch.Elapsed));

            log("Locating the currently open 'Report Parameters' window...");
            var reportParametersWindow = WaitForReportParametersWindow(log, ct);
            if (reportParametersWindow is null)
            {
                log("'Report Parameters' window not found - open it in Pioneer first (Analysis > Financial Reports, double-click a report), then click Test date entry again.");
                return Task.FromResult(ReportRunResult.Failed("'Report Parameters' window not open", stopwatch.Elapsed));
            }

            log($"'Report Parameters' window found. class='{SafeClassName(reportParametersWindow)}' title_len={SafeName(reportParametersWindow).Length}");

            if (!ForegroundAndSettleReportParametersWindow(reportParametersWindow, log))
                return Task.FromResult(ReportRunResult.Failed("'Report Parameters' window lost focus", stopwatch.Elapsed));

            if (item.Entry.ParameterKind == ReportParameterKind.PaymentsSearch)
            {
                log("Test date entry does not apply to the Payments report (it never uses this popup) - pick a different report.");
                return Task.FromResult(ReportRunResult.Failed("Payments report has no Report Parameters popup", stopwatch.Elapsed));
            }

            var plan = ReportParameterKeyPlan.Build(item.Entry, item.Begin, item.End, includeF12: false);
            var ok = ReplayReportParameterKeyPlan(plan, reportParametersWindow, log);

            var result = ok
                ? ReportRunResult.Saved("(test only - no F12 sent, nothing was run)", stopwatch.Elapsed)
                : ReportRunResult.Failed("Date entry test failed - see the log lines above", stopwatch.Elapsed);
            return Task.FromResult(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log($"Automation error: {ex.Message}");
            return Task.FromResult(ReportRunResult.Failed(ex.Message, stopwatch.Elapsed));
        }
    }

    // ------------------------------------------------------------------
    // FOREGROUND / SAFETY
    // ------------------------------------------------------------------

    /// <summary>
    /// Re-checks the OS foreground window belongs to Pioneer's process
    /// before any keystroke/click is sent (GOAL brief SAFETY: "before
    /// sending any keystroke re-check the foreground window is Pioneer's
    /// ... otherwise abort the report"). Makes one attempt to restore
    /// focus first (Pioneer may simply have been alt-tabbed away from,
    /// e.g. by a screen saver or a passing notification) before giving
    /// up — never clicks anything to do so, only SetForegroundWindow on
    /// Pioneer's own cached handle.
    /// </summary>
    private bool EnsurePioneerForeground(Action<string> log)
    {
        if (_mainWindow is null) return false;

        if (IsForegroundOwnedByPioneer()) return true;

        BringToForeground(_mainWindow);

        // Round 2 fix (GOAL brief step 3): was a single fixed 150ms sleep
        // then one check - now a bounded poll, same PollInterval/idiom as
        // WaitUntilAsync below, since focus changes can take longer than
        // 150ms to settle on a slow machine.
        if (WaitUntilSync(IsForegroundOwnedByPioneer, ForegroundSettleTimeout, PollInterval)) return true;

        log("Pioneer lost focus - aborting this report.");
        return false;
    }

    /// <summary>
    /// Round 3 fix (GOAL brief fix 1: "make 'Pioneer lost focus'
    /// recoverable once ... re-activate the main window ... and retry the
    /// step one time before aborting"). Wraps EnsurePioneerForeground
    /// (which already tries BringToForeground once, then polls up to
    /// ForegroundSettleTimeout before giving up) with exactly ONE more
    /// full recovery attempt before the caller truly aborts the report -
    /// used only at RunFinancialReport/RunPaymentsExport's own top-level
    /// step guards, not the finer-grained per-keystroke checks already
    /// inside the ribbon-navigation strategies (those already get
    /// resilience from trying three independent strategies in turn).
    /// </summary>
    private bool EnsurePioneerForegroundWithRecovery(Action<string> log)
    {
        if (EnsurePioneerForeground(log)) return true;
        if (_mainWindow is null) return false;

        log("Retrying once - re-activating the PioneerRx main window...");
        BringToForeground(_mainWindow);

        if (WaitUntilSync(IsForegroundOwnedByPioneer, ForegroundSettleTimeout, PollInterval)) return true;

        log("Pioneer lost focus - recovery attempt failed, aborting this report.");
        return false;
    }

    /// <summary>
    /// Round 4 (W-T92 follow-up, GOAL brief step 1: "Bring it to the
    /// foreground and verify foreground == that window before any key
    /// goes out"). Unlike EnsurePioneerForeground (which only checks the
    /// OS foreground window's PROCESS id - true for ANY Pioneer-owned
    /// window, main window included), this checks the SPECIFIC window
    /// handle - used for the "Report Parameters" popup, a separate
    /// top-level window from _mainWindow that every date/F12 keystroke
    /// must land on exactly, never on whatever else happens to belong to
    /// Pioneer's process. Same one-retry shape as EnsurePioneerForeground:
    /// bring it forward once, poll up to ForegroundSettleTimeout, else
    /// fail rather than guess.
    /// </summary>
    private bool EnsureWindowForeground(AutomationElement window, Action<string> log, string windowLabel)
    {
        var handle = SafeNativeHandle(window);
        if (handle == IntPtr.Zero)
        {
            log($"{windowLabel}: no usable window handle.");
            return false;
        }

        if (GetForegroundWindow() == handle) return true;

        BringToForeground(window);

        if (WaitUntilSync(() => GetForegroundWindow() == handle, ForegroundSettleTimeout, PollInterval)) return true;

        log($"{windowLabel} lost focus - aborting.");
        return false;
    }

    /// <summary>
    /// Round 6 (W-T92 follow-up, GOAL brief step 4: "bring it to the
    /// foreground if it is not ... wait the macro's settle delay ... then
    /// run the existing date-entry sequence unchanged"). Shared by
    /// RunFinancialReport and TestDateEntry - both used to call
    /// EnsureWindowForeground directly and go straight into date entry
    /// with no pause at all; Will's own macro waits after the popup opens
    /// before it starts typing.
    /// </summary>
    private bool ForegroundAndSettleReportParametersWindow(AutomationElement window, Action<string> log)
    {
        if (!EnsureWindowForeground(window, log, "Report Parameters window")) return false;

        log($"  settling {ReportParametersSettleDelay.TotalMilliseconds:0}ms before date entry...");
        Thread.Sleep(ReportParametersSettleDelay);
        return true;
    }

    /// <summary>Synchronous counterpart to WaitUntilAsync below, for the handful of call sites (EnsurePioneerForeground, the keyboard fallback steps) that run on the calling thread with no CancellationToken in scope.</summary>
    private static bool WaitUntilSync(Func<bool> condition, TimeSpan timeout, TimeSpan pollInterval)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            bool result;
            try { result = condition(); }
            catch { result = false; }

            if (result) return true;
            if (DateTime.UtcNow >= deadline) return false;

            Thread.Sleep(pollInterval);
        }
    }

    /// <summary>
    /// Round 6 (W-T92 follow-up, GOAL brief step 2: "'Pioneer lost focus'
    /// must mean the foreground window belongs to a different PROCESS than
    /// Pioneer - any window of the Pioneer PID (main window, the popup, its
    /// dialogs) counts as focused"). This was already a process-id (not
    /// window-handle) comparison, so it already treats the "Report
    /// Parameters" popup - or any other Pioneer-owned window - as focused;
    /// the pure decision itself is now factored out into
    /// ForegroundOwnershipRule so it is unit tested directly, independent
    /// of GetForegroundWindow/GetWindowThreadProcessId.
    /// </summary>
    private bool IsForegroundOwnedByPioneer()
    {
        try
        {
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            GetWindowThreadProcessId(fg, out var pid);
            return ForegroundOwnershipRule.IsStillFocused((int)pid, _pioneerProcessId);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Round 2 fix (GOAL brief step 5 - "make BringToForeground more
    /// robust"): restores the window first if it's minimized (a plain
    /// SetForegroundWindow does nothing useful for an iconic window), then
    /// tries SetForegroundWindow directly. Windows often refuses that call
    /// outright for a background process unless its thread is attached to
    /// the current foreground thread's input queue - if the direct call
    /// didn't take, falls back to the classic AttachThreadInput trick
    /// (attach, retry, detach) before giving up. Every step is best-effort
    /// - EnsurePioneerForeground always re-checks the REAL OS foreground
    /// window afterward and aborts the report rather than trust this
    /// blindly.
    /// </summary>
    private static void BringToForeground(AutomationElement window)
    {
        try
        {
            var handle = SafeNativeHandle(window);
            if (handle == IntPtr.Zero) return;

            if (IsIconic(handle))
            {
                ShowWindow(handle, SW_RESTORE);
            }

            if (SetForegroundWindow(handle)) return;

            var foregroundWindow = GetForegroundWindow();
            if (foregroundWindow == IntPtr.Zero) return;

            var foregroundThreadId = GetWindowThreadProcessId(foregroundWindow, out _);
            var currentThreadId = GetCurrentThreadId();
            if (foregroundThreadId == 0 || foregroundThreadId == currentThreadId) return;

            if (!AttachThreadInput(currentThreadId, foregroundThreadId, true)) return;
            try
            {
                SetForegroundWindow(handle);
            }
            finally
            {
                AttachThreadInput(currentThreadId, foregroundThreadId, false);
            }
        }
        catch
        {
            // Best-effort only - EnsurePioneerForeground's caller re-checks
            // and aborts if this didn't actually work.
        }
    }

    private static int SafeProcessId(AutomationElement element)
    {
        try { return element.FrameworkAutomationElement.ProcessId; }
        catch { return 0; }
    }

    /// <summary>Shared by BringToForeground, EnumeratePioneerOwnedTopLevelWindows, FindPreviewWindow, and IsPreviewReady — the one place this app reads an AutomationElement's native HWND.</summary>
    private static IntPtr SafeNativeHandle(AutomationElement element)
    {
        try
        {
            var handle = element.FrameworkAutomationElement.NativeWindowHandle;
            return handle ?? IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static bool IsPioneerProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return string.Equals(process.ProcessName, PioneerProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------
    // RIBBON NAVIGATION
    // ------------------------------------------------------------------

    /// <summary>
    /// Round 2 rewrite (Will's first real run: "UIA navigation to
    /// 'Analysis' > 'Financial Reports' failed - trying keyboard fallback"
    /// ... "failed - Could not open Analysis > Financial Reports" - the
    /// single UIA guess AND the single keyboard guess both missed, with no
    /// way to tell why). Tries three independent strategies in order,
    /// confirming the target screen actually appeared after EACH one
    /// (GOAL brief step 2iii) before giving up and moving to the next -
    /// only if all three fail does this write the diagnostic dump (step
    /// 1) and return false:
    ///   1. UIA name match - any descendant (any ControlType) whose Name
    ///      equals/starts-with the tab name, then the same for the button
    ///      name, each activated via whichever pattern it supports.
    ///   2. Keyboard KeyTips - tap Alt (enters ribbon key-tip mode), then
    ///      type each name's first letter in turn, same shape as the
    ///      original fallback but with 1.5s settle waits instead of the
    ///      old fixed 200/300/500ms sleeps.
    ///   3. Keyboard Alt+letter accelerators - Will's Macro Express style,
    ///      holding Alt down WITH each letter (TypeSimultaneously) rather
    ///      than tapping Alt separately first.
    /// </summary>
    private async Task<bool> OpenRibbonScreen(string tabName, string buttonName, Action<string> log, CancellationToken ct)
    {
        var confirmationHints = RibbonScreenConfirmation.BuildConfirmationHints(buttonName);

        log($"Ribbon navigation strategy 1/3 (UIA name match) for '{tabName}' > '{buttonName}'...");
        if (TryUiaNameMatchNavigate(tabName, buttonName, log)
            && await WaitForScreenConfirmation(confirmationHints, ct).ConfigureAwait(false))
        {
            return true;
        }

        log($"Ribbon navigation strategy 2/3 (keyboard KeyTips: Alt, then '{tabName}'/'{buttonName}' first letters)...");
        if (TryKeyboardKeyTipsNavigate(tabName, buttonName, log, ct)
            && await WaitForScreenConfirmation(confirmationHints, ct).ConfigureAwait(false))
        {
            return true;
        }

        log($"Ribbon navigation strategy 3/3 (keyboard Alt+letter accelerators for '{tabName}'/'{buttonName}')...");
        if (TryKeyboardAcceleratorNavigate(tabName, buttonName, log, ct)
            && await WaitForScreenConfirmation(confirmationHints, ct).ConfigureAwait(false))
        {
            return true;
        }

        log($"All ribbon navigation strategies failed for '{tabName}' > '{buttonName}'.");
        WriteUiaDiagnosticDump($"{tabName} > {buttonName} navigation", log);
        return false;
    }

    /// <summary>Strategy 1: UIA name match. Logs what each of the two lookups found before returning, per GOAL brief step 1 ("log which lookup strategy was tried and what each found").</summary>
    private bool TryUiaNameMatchNavigate(string tabName, string buttonName, Action<string> log)
    {
        var tab = FindDescendantByNamePrefix(_mainWindow!, tabName);
        log($"  UIA lookup '{tabName}': {(tab is null ? "not found" : "found")}");
        if (tab is null || !TryActivateRibbonElement(tab, log)) return false;

        var button = FindDescendantByNamePrefix(_mainWindow!, buttonName);
        log($"  UIA lookup '{buttonName}': {(button is null ? "not found" : "found")}");
        return button is not null && TryActivateRibbonElement(button, log);
    }

    /// <summary>Any descendant (any ControlType) whose Name equals or starts with <paramref name="namePrefix"/>, case-insensitive - deliberately broader than FindDescendantByName's exact/typed match, since Pioneer's ribbon may not expose "Analysis" as a TabItem the way the phase-1 guess assumed.</summary>
    private static AutomationElement? FindDescendantByNamePrefix(AutomationElement root, string namePrefix)
    {
        if (string.IsNullOrEmpty(namePrefix)) return null;

        try
        {
            var descendants = root.FindAllDescendants();
            foreach (var candidate in descendants)
            {
                string? name;
                try { name = candidate.Name; } catch { continue; }
                if (string.IsNullOrEmpty(name)) continue;
                if (name.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase)) return candidate;
            }
        }
        catch
        {
            // fall through to null below
        }

        return null;
    }

    /// <summary>
    /// GOAL brief step 2i's ordered activation attempts: SelectionItem,
    /// then Invoke, then ExpandCollapse, then a mouse click at the
    /// element's bounding-rectangle centre. Logs which one actually
    /// worked (or that none did).
    ///
    /// Review fix (blocker 2): the mouse click is the one activation path
    /// that lands on WHATEVER window is physically at that screen point
    /// the instant the click fires, regardless of which AutomationElement
    /// this method was asked to click - a focus change between the UIA
    /// lookup and this call could put a completely different window
    /// there. Immediately before the click: re-run EnsurePioneerForeground
    /// (existing guard), then independently confirm via WindowFromPoint
    /// that the window ACTUALLY AT the click point belongs to Pioneer's
    /// own process id (IsPointOwnedByPioneer) - skip the click and log why
    /// rather than guess if either check fails. Instance method (not
    /// static) so it can call those instance guards.
    /// </summary>
    private bool TryActivateRibbonElement(AutomationElement element, Action<string> log)
    {
        try
        {
            if (element.Patterns.SelectionItem.IsSupported)
            {
                element.Patterns.SelectionItem.Pattern.Select();
                log("    activated via SelectionItem");
                return true;
            }
        }
        catch { /* try the next pattern */ }

        try
        {
            if (element.Patterns.Invoke.IsSupported)
            {
                element.Patterns.Invoke.Pattern.Invoke();
                log("    activated via Invoke");
                return true;
            }
        }
        catch { /* try the next pattern */ }

        try
        {
            if (element.Patterns.ExpandCollapse.IsSupported)
            {
                element.Patterns.ExpandCollapse.Pattern.Expand();
                log("    activated via ExpandCollapse");
                return true;
            }
        }
        catch { /* fall through to the mouse click below */ }

        try
        {
            var rect = element.BoundingRectangle;
            if (rect.Width > 0 && rect.Height > 0)
            {
                var center = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);

                if (!EnsurePioneerForeground(log))
                {
                    log("    skipping mouse click - Pioneer lost focus");
                    return false;
                }

                if (!IsPointOwnedByPioneer(center))
                {
                    log("    skipping mouse click - the window at the click point is not Pioneer's");
                    return false;
                }

                Mouse.LeftClick(center);
                log("    activated via mouse click at bounding-rectangle centre");
                return true;
            }
        }
        catch { /* fall through to false below */ }

        log("    could not activate - no supported pattern and no usable bounding rectangle");
        return false;
    }

    /// <summary>Review fix (blocker 2): confirms the window physically AT <paramref name="point"/> right now belongs to Pioneer's own process, via WindowFromPoint + GetWindowThreadProcessId - an independent, stronger check than EnsurePioneerForeground's OS-foreground-window check, since "the foreground window" and "whatever is under this exact pixel" aren't always the same window.</summary>
    private bool IsPointOwnedByPioneer(Point point)
    {
        try
        {
            var hwnd = WindowFromPoint(new POINT { X = point.X, Y = point.Y });
            if (hwnd == IntPtr.Zero) return false;

            GetWindowThreadProcessId(hwnd, out var pid);
            return _pioneerProcessId != 0 && pid == (uint)_pioneerProcessId;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Strategy 2: the original keyboard fallback shape (tap Alt for key-tips, then each name's first letter) with 1.5s settle waits and a per-keystroke foreground re-check. Non-blocking review fix: checks <paramref name="ct"/> at the top and between keystrokes so Stop responds promptly instead of waiting out the full ~4.5s of settle sleeps first - cancellation propagates as OperationCanceledException, same contract as the rest of this class.</summary>
    private bool TryKeyboardKeyTipsNavigate(string tabName, string buttonName, Action<string> log, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            // Re-check foreground before EACH keystroke, not just once
            // before the whole sequence - three separate sends is enough
            // time for focus to move between them.
            if (!EnsurePioneerForeground(log)) return false;
            Keyboard.Press(VirtualKeyShort.ALT);
            Keyboard.Release(VirtualKeyShort.ALT);
            Thread.Sleep(KeyboardStepWait);

            ct.ThrowIfCancellationRequested();
            if (!EnsurePioneerForeground(log)) return false;
            Keyboard.Type(tabName.Substring(0, 1));
            Thread.Sleep(KeyboardStepWait);

            ct.ThrowIfCancellationRequested();
            if (!EnsurePioneerForeground(log)) return false;
            Keyboard.Type(buttonName.Substring(0, 1));
            Thread.Sleep(KeyboardStepWait);

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Strategy 3: classic held-Alt accelerator combos (Alt+letter for the tab, Alt+letter for the button) - Will's Macro Express macros use this style, distinct from strategy 2's tap-then-type key-tips sequence. Same non-blocking cancellation-responsiveness fix as TryKeyboardKeyTipsNavigate.</summary>
    private bool TryKeyboardAcceleratorNavigate(string tabName, string buttonName, Action<string> log, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!EnsurePioneerForeground(log)) return false;
            if (!SendAltLetterCombo(tabName)) return false;
            Thread.Sleep(KeyboardStepWait);

            ct.ThrowIfCancellationRequested();
            if (!EnsurePioneerForeground(log)) return false;
            if (!SendAltLetterCombo(buttonName)) return false;
            Thread.Sleep(KeyboardStepWait);

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Sends Alt held down simultaneously with <paramref name="name"/>'s first letter (FlaUI's TypeSimultaneously). Returns false without sending anything if the first character isn't a plain A-Z letter.</summary>
    private static bool SendAltLetterCombo(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;

        var key = LetterToVirtualKey(name[0]);
        if (key is null) return false;

        Keyboard.TypeSimultaneously(VirtualKeyShort.ALT, key.Value);
        return true;
    }

    /// <summary>
    /// UNVERIFIED assumption (flagged per this file's own risk posture):
    /// FlaUI.Core.WindowsAPI.VirtualKeyShort's KEY_A..KEY_Z members follow
    /// the standard Win32 virtual-key codes (0x41-0x5A), which are the
    /// same numeric values as uppercase ASCII 'A'-'Z' - confirmed present
    /// in the referenced FlaUI.Core 4.0.0 package's own XML docs (KEY_A
    /// through KEY_Z all exist), but the exact enum values were not
    /// independently re-verified against Win32's VK_A..VK_Z table on this
    /// Mac (no dotnet/Windows here to spot-check at runtime).
    /// </summary>
    private static VirtualKeyShort? LetterToVirtualKey(char letter)
    {
        var upper = char.ToUpperInvariant(letter);
        if (upper < 'A' || upper > 'Z') return null;

        return (VirtualKeyShort)((int)VirtualKeyShort.KEY_A + (upper - 'A'));
    }

    /// <summary>GOAL brief step 2iii: after a navigation strategy runs, poll (every PollInterval, via WaitUntilAsync) up to RibbonConfirmationTimeout for the target screen to actually appear.</summary>
    private async Task<bool> WaitForScreenConfirmation(IReadOnlyList<string> nameHints, CancellationToken ct) =>
        await WaitUntilAsync(() => IsScreenConfirmed(nameHints), RibbonConfirmationTimeout, ct).ConfigureAwait(false);

    /// <summary>True once EITHER a descendant of the main window matches one of the hints by exact name, OR a Pioneer-owned top-level window's title CONTAINS one of them (see RibbonScreenConfirmation - e.g. "Run Financial Reports", the screen's own list-title convention).</summary>
    private bool IsScreenConfirmed(IReadOnlyList<string> nameHints)
    {
        if (_mainWindow is null) return false;

        foreach (var hint in nameHints)
        {
            if (FindDescendantByName(_mainWindow, hint) is not null) return true;
        }

        foreach (var (_, candidate) in EnumeratePioneerOwnedTopLevelWindows())
        {
            if (RibbonScreenConfirmation.NameContainsAnyHint(candidate.Title, nameHints)) return true;
        }

        return false;
    }

    // ------------------------------------------------------------------
    // ROW SELECTION (Round 3 - GOAL brief fix 2: "each of those reports
    // has to be double clicked on to get them to open the next window")
    // ------------------------------------------------------------------

    /// <summary>
    /// Round 6 rewrite (W-T92 follow-up, Will's build da39140 run: the
    /// double-click DID open the "Report Parameters" popup, but the old
    /// title match ("contains 'Report Parameters'") never recognized it -
    /// see WaitForNewPioneerWindow's own doc for the theory - so this
    /// fell through to grid-keyboard arrow keys landing on the now-open
    /// popup's date field ("scrolling through the months") and then
    /// aborted with "Pioneer lost focus" when a later foreground check hit
    /// a window it didn't expect). Detection of "did the row open" is now
    /// title-agnostic entirely: a native-window-inventory snapshot is
    /// taken once before any strategy runs, and after each strategy's
    /// click/Enter attempt, WaitForNewPioneerWindow polls for ANY new
    /// visible top-level window of Pioneer's own pid against that
    /// baseline - GOAL brief step 3's own rule ("a new window means the
    /// row opened") IS the confirmation now, not a side effect of it.
    ///
    /// Tries each row-text candidate (PioneerRowText, then
    /// PioneerRowTextAlias if the catalog entry has one) and, for each,
    /// the three layered strategies in order (UIA deep search, grid
    /// keyboard, OCR) via the pure ReportRowSelectionSequencer - stopping
    /// at the first strategy whose own attempt AND the new-window
    /// confirmation both succeed. hardStop (GOAL brief step 3: "never
    /// start row strategy 2 or 3 ... if a new Pioneer window appeared
    /// after strategy 1") is now set the instant ANY new window is
    /// confirmed, success or not - there is no more "unexpected, keep
    /// going" case now that recognizing the popup doesn't depend on its
    /// title: once something new opened, every later strategy attempt
    /// would just be sending clicks/keys into a screen that already
    /// moved on.
    /// </summary>
    private bool TrySelectAndOpenReportRow(ReportCatalogEntry entry, Action<string> log, CancellationToken ct)
    {
        var rowTextCandidates = ReportRowSelectionSequencer.BuildRowTextCandidates(entry);
        var baselineHandles = SnapshotPioneerWindowHandles();
        var hardStop = false;

        bool RunStrategy(Func<string, bool> clickAttempt, string rowText)
        {
            if (hardStop) return false;

            var clicked = clickAttempt(rowText);
            if (!clicked) return false;

            var newWindow = WaitForNewPioneerWindow(baselineHandles, ReportParametersConfirmationTimeout, log, ct);
            if (newWindow is null) return false;

            // A new Pioneer-owned top-level window appeared - the row DID
            // open, whatever its title is. Never try a later strategy
            // against a screen that has already moved on, and hand the
            // confirmed handle straight to WaitForReportParametersWindow
            // so it resolves this EXACT window rather than re-searching.
            hardStop = true;
            _reportParametersWindowHandle = newWindow.Value.Handle;
            return true;
        }

        bool UiaDeepSearchStrategy(string rowText) => RunStrategy(rt => TryDeepUiaRowSelect(rt, log), rowText);
        bool GridKeyboardStrategy(string rowText) => RunStrategy(rt => TryGridKeyboardRowSelect(rt, baselineHandles, log, ct), rowText);
        bool OcrStrategy(string rowText) => RunStrategy(rt => TryOcrRowSelectSync(rt, log, ct), rowText);

        var strategies = new List<Func<string, bool>> { UiaDeepSearchStrategy, GridKeyboardStrategy, OcrStrategy };

        return ReportRowSelectionSequencer.TrySelect(rowTextCandidates, strategies, (rowText, strategyNumber) =>
            log($"  Row selection strategy {strategyNumber}/3 for '{rowText}'..."));
    }

    /// <summary>Native (non-UIA) snapshot of Pioneer's own visible top-level window handles right now - the "before" baseline WaitForNewPioneerWindow diffs against. See EnumerateVisibleTopLevelWindowsForProcess's own doc for why this is raw Win32, not EnumeratePioneerOwnedTopLevelWindows' UIA desktop walk.</summary>
    private HashSet<IntPtr> SnapshotPioneerWindowHandles() =>
        new(EnumerateVisibleTopLevelWindowsForProcess(_pioneerProcessId).Select(w => w.Handle));

    /// <summary>
    /// Round 6 (W-T92 follow-up, GOAL brief step 1): polls (every
    /// PollInterval, up to <paramref name="timeout"/>) for any NEW visible
    /// top-level window of Pioneer's own process id that wasn't in
    /// <paramref name="baselineHandles"/> - title-agnostic by
    /// construction, since EnumerateVisibleTopLevelWindowsForProcess never
    /// reads a window's title text at all. Logs the diff (class/
    /// title-length/size/owner handle - never a title, per this file's
    /// established PHI-safety convention, see MainWindowCandidateLog) the
    /// moment something new is found, and the full last-seen inventory if
    /// nothing ever appears, so a future failure can still be diagnosed
    /// from the run log alone.
    ///
    /// Review fix (BLOCKING, round 6 review): NewWindowDetector.NewWindows
    /// itself now excludes transient chrome (tooltips, combo/auto-suggest
    /// dropdowns, IME windows, anything under 100x100 - see
    /// TransientWindowFilter's own doc) so a false positive there can
    /// never trigger TrySelectAndOpenReportRow's hardStop the way the
    /// original title-matching bug did. Every window
    /// NewWindowDetector.IgnoredTransientWindows reports is logged too
    /// (once per handle, per poll call) so a real miss is still
    /// diagnosable - it just never counts as "the" new window. When more
    /// than one real new window exists, NewWindows already returns them
    /// largest-first, so index 0 here is always the biggest.
    /// </summary>
    private NativeWindowSnapshot? WaitForNewPioneerWindow(IReadOnlyCollection<IntPtr> baselineHandles, TimeSpan timeout, Action<string> log, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        var lastSnapshot = new List<NativeWindowSnapshot>();
        var loggedIgnoredHandles = new HashSet<IntPtr>();

        while (true)
        {
            if (ct.IsCancellationRequested) return null;

            lastSnapshot = EnumerateVisibleTopLevelWindowsForProcess(_pioneerProcessId);
            var newWindows = NewWindowDetector.NewWindows(baselineHandles, lastSnapshot);

            foreach (var ignored in NewWindowDetector.IgnoredTransientWindows(baselineHandles, lastSnapshot))
            {
                if (loggedIgnoredHandles.Add(ignored.Handle))
                {
                    log($"    {NativeWindowSnapshotLog.FormatIgnoredTransient(ignored)}");
                }
            }

            if (newWindows.Count > 0)
            {
                foreach (var w in newWindows)
                {
                    log($"    new Pioneer window: {NativeWindowSnapshotLog.Format(w)}");
                }

                return newWindows[0];
            }

            if (DateTime.UtcNow >= deadline) break;
            Thread.Sleep(PollInterval);
        }

        log("    no new Pioneer window appeared within the timeout. Current inventory:");
        if (lastSnapshot.Count == 0)
        {
            log("      (none)");
        }
        foreach (var w in lastSnapshot)
        {
            log($"      {NativeWindowSnapshotLog.Format(w)}");
        }

        return null;
    }

    /// <summary>
    /// Round 6 rewrite (W-T92 follow-up). Prefers the exact window
    /// TrySelectAndOpenReportRow already confirmed opened
    /// (_reportParametersWindowHandle, set by its own WaitForNewPioneerWindow
    /// call) - resolving a cached HANDLE instead of re-searching means this
    /// can never land on a DIFFERENT window than the one row selection
    /// actually saw appear. Falls back to a title-agnostic "any non-main
    /// Pioneer-owned window" poll only when there is no cached handle (the
    /// "Test date entry" button runs this with no row selection having
    /// happened first - see that method's own doc) or the cached handle no
    /// longer resolves to a live window.
    /// </summary>
    private AutomationElement? WaitForReportParametersWindow(Action<string> log, CancellationToken ct)
    {
        if (_reportParametersWindowHandle != IntPtr.Zero)
        {
            var cached = ResolveNativeHandleToElement(_reportParametersWindowHandle);
            if (cached is not null) return cached;

            log("  cached 'Report Parameters' window handle is no longer visible - falling back to a fresh search.");
        }

        AutomationElement? match = null;

        // Non-blocking review fix: reuses WaitUntilSync (same poll/timeout
        // idiom as EnsurePioneerForeground etc.) instead of a hand-rolled
        // deadline loop - the condition delegate both checks cancellation
        // (returning true to stop polling early, same as WaitUntilSync's
        // own "stop as soon as the condition is met" contract) and stashes
        // whatever FindNonMainPioneerWindowElement found into the
        // captured `match` local.
        WaitUntilSync(() =>
        {
            if (ct.IsCancellationRequested) return true;
            match = FindNonMainPioneerWindowElement();
            return match is not null;
        }, ReportParametersWindowLocateTimeout, PollInterval);

        return ct.IsCancellationRequested ? null : match;
    }

    /// <summary>Title-agnostic fallback used only when there's no confirmed handle from row selection (see WaitForReportParametersWindow's own doc): any visible Pioneer-owned top-level window that isn't the main ribbon window.</summary>
    private AutomationElement? FindNonMainPioneerWindowElement()
    {
        var mainHandle = _mainWindow is null ? IntPtr.Zero : SafeNativeHandle(_mainWindow);

        foreach (var snapshot in EnumerateVisibleTopLevelWindowsForProcess(_pioneerProcessId))
        {
            if (snapshot.Handle == IntPtr.Zero || snapshot.Handle == mainHandle) continue;

            var element = ResolveNativeHandleToElement(snapshot.Handle);
            if (element is not null) return element;
        }

        return null;
    }

    /// <summary>Converts a native HWND into a FlaUI AutomationElement (AutomationBase.FromHandle) - never throws, returns null for a stale/invalid/no-longer-visible handle.</summary>
    private AutomationElement? ResolveNativeHandleToElement(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return null;

        try
        {
            if (!IsWindowVisible(handle)) return null;
            return GetOrCreateAutomation().FromHandle(handle);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Round 6 (W-T92 follow-up, GOAL brief step 1): raw Win32 enumeration
    /// - EnumWindows + GetWindowThreadProcessId + IsWindowVisible -
    /// deliberately independent of FlaUI/UIA3Automation.GetDesktop() (the
    /// basis of EnumeratePioneerOwnedTopLevelWindows below), which is what
    /// missed the "Report Parameters" popup in Will's build da39140 run:
    /// that popup either never exposed a UIA Name at all, or FlaUI's own
    /// desktop-children snapshot simply hadn't caught up to it yet by the
    /// time the old title match polled it. Never reads a window's title
    /// TEXT - only its LENGTH (GetWindowTextLength, no GetWindowText call
    /// at all) - so nothing here can ever log or leak field content.
    /// </summary>
    private static List<NativeWindowSnapshot> EnumerateVisibleTopLevelWindowsForProcess(int processId)
    {
        var result = new List<NativeWindowSnapshot>();
        if (processId == 0) return result;

        try
        {
            EnumWindows((hWnd, _) =>
            {
                try
                {
                    if (!IsWindowVisible(hWnd)) return true;

                    GetWindowThreadProcessId(hWnd, out var pid);
                    if (pid != (uint)processId) return true;

                    var classNameBuilder = new StringBuilder(256);
                    GetClassName(hWnd, classNameBuilder, classNameBuilder.Capacity);

                    var owner = GetWindow(hWnd, GW_OWNER);
                    var titleLength = GetWindowTextLength(hWnd);

                    var width = 0;
                    var height = 0;
                    if (GetWindowRect(hWnd, out var rect))
                    {
                        width = rect.Right - rect.Left;
                        height = rect.Bottom - rect.Top;
                    }

                    result.Add(new NativeWindowSnapshot(hWnd, classNameBuilder.ToString(), titleLength, width, height, owner));
                }
                catch
                {
                    // Best-effort per-window - a window that can't be inspected is simply skipped.
                }

                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // Best-effort - an empty list here just means the caller's poll tries again.
        }

        return result;
    }

    /// <summary>What TryFindFallbackPreviewWindow found, for RunFinancialReport to react to — see that method's own doc for why "found a new window" and "found a new window that's actually a preview" have to be distinguished (W-T92 review fix, BLOCKING).</summary>
    private enum FallbackPreviewOutcome
    {
        /// <summary>No new Pioneer-owned window since the parameters window was located (or it's still open) - caller falls through to the ordinary "Timed out waiting for the report preview" failure.</summary>
        NotFound,

        /// <summary>A new window appeared and it exposes the same preview affordances FindPreviewWindow itself requires (LooksLikePreviewWindow) - safe to treat as the preview.</summary>
        Preview,

        /// <summary>A new window appeared but does NOT look like a preview (no Print Immediately/Export to PDF affordance) - an unexpected dialog, already logged (class + title length only) and best-effort dismissed via TryCloseWindowSafely. Caller must fail this report with a distinct reason rather than risk treating a random dialog as the preview.</summary>
        UnexpectedWindow
    }

    /// <summary>
    /// Round 4 (W-T92 follow-up, GOAL brief step 4: "If IsPreviewReady
    /// never trips but the Report Parameters window has closed and a new
    /// Pioneer-owned window appeared, treat that new window as the
    /// preview"). Same "new since a known-handles snapshot" idiom as
    /// WaitForNewPioneerWindow (row selection's own new-window confirmation
    /// check) - <paramref name="knownHandlesBeforeParameterEntry"/> is
    /// captured right after the Report Parameters window was located and
    /// foregrounded, before any date keystroke went out, so anything not
    /// in that set (and not the parameters window's own now-closed
    /// handle) counts as new. Never fires while the parameters window is
    /// still open - a window that appeared for some OTHER reason (an
    /// error dialog, say) while the parameters popup is still up is not
    /// the preview.
    ///
    /// BLOCKING review fix: the original version accepted ANY new window
    /// unconditionally and handed it straight to InvokeExportButton — an
    /// error/confirmation dialog would get mislabelled "the preview" and
    /// left open, breaking every report after it in the batch. Now every
    /// new window is checked against LooksLikePreviewWindow (the SAME
    /// affordance check FindPreviewWindow itself requires) before it's
    /// accepted; if MULTIPLE new windows appeared, the one WITH preview
    /// affordances wins. If none of them look like a preview, this logs
    /// the first one (class + title length only - never title text),
    /// best-effort dismisses it (TryCloseWindowSafely - WM_CLOSE first,
    /// Alt+F4 only if it's confirmed foreground), and returns
    /// UnexpectedWindow so RunFinancialReport can fail this one report
    /// cleanly instead of corrupting the rest of the batch.
    /// </summary>
    private (FallbackPreviewOutcome Outcome, AutomationElement? Window) TryFindFallbackPreviewWindow(
        AutomationElement reportParametersWindow, HashSet<IntPtr> knownHandlesBeforeParameterEntry, Action<string> log)
    {
        var parametersHandle = SafeNativeHandle(reportParametersWindow);
        var current = EnumeratePioneerOwnedTopLevelWindows();

        if (current.Any(w => w.Candidate.Handle == parametersHandle))
        {
            log("    fallback check: 'Report Parameters' window is still open - not treating any window as the preview.");
            return (FallbackPreviewOutcome.NotFound, null);
        }

        var newWindows = current
            .Where(w => w.Candidate.Handle != IntPtr.Zero
                        && w.Candidate.Handle != parametersHandle
                        && !knownHandlesBeforeParameterEntry.Contains(w.Candidate.Handle))
            .ToList();

        if (newWindows.Count == 0)
        {
            log("    fallback check: 'Report Parameters' window closed but no new Pioneer window was found.");
            return (FallbackPreviewOutcome.NotFound, null);
        }

        foreach (var (window, candidate) in newWindows)
        {
            if (!LooksLikePreviewWindow(window)) continue;

            log($"    fallback check: 'Report Parameters' window closed and a new Pioneer window with preview affordances appeared - treating it as the preview (path=fallback-new-window). class='{SafeClassName(window)}' title_len={candidate.Title.Length}");
            return (FallbackPreviewOutcome.Preview, window);
        }

        var (unexpectedWindow, unexpectedCandidate) = newWindows[0];
        log($"    fallback check: 'Report Parameters' window closed but the new Pioneer window has no preview affordances - treating it as unexpected, not the preview. class='{SafeClassName(unexpectedWindow)}' title_len={unexpectedCandidate.Title.Length}");
        TryCloseWindowSafely(unexpectedCandidate.Handle, log, "the unexpected window");

        return (FallbackPreviewOutcome.UnexpectedWindow, null);
    }

    // --- Strategy 2a: UIA deep search inside FinancialReportsWorkArea ---

    /// <summary>
    /// GOAL brief fix 2a: unlimited-depth RawViewWalker search scoped to
    /// the FinancialReportsWorkArea pane (capped at MaxRowSearchNodes),
    /// matching DataItem/ListItem/Custom/Edit/Text descendants by Name OR
    /// ValuePattern value OR LegacyIAccessible name/value — exact match
    /// only (ReportRowTextMatcher), never a prefix, so a row like
    /// "Inventory Valuation" can never match "Inventory Valuation (With
    /// Last Supplier)". On a match: ScrollIntoView if supported, then
    /// double-click its bounding-rectangle centre.
    /// </summary>
    private bool TryDeepUiaRowSelect(string rowText, Action<string> log)
    {
        log($"    strategy 1/3 (UIA deep search in '{FinancialReportsWorkAreaAutomationId}') for '{rowText}'...");

        var workArea = FindDescendantByAutomationId(_mainWindow!, FinancialReportsWorkAreaAutomationId);
        if (workArea is null)
        {
            log($"    '{FinancialReportsWorkAreaAutomationId}' pane not found.");
            return false;
        }

        var match = DeepFindRowElement(workArea, rowText);
        if (match is null)
        {
            log("    no matching row element found in the deep UIA search.");
            return false;
        }

        try
        {
            if (match.Patterns.ScrollItem.IsSupported)
            {
                match.Patterns.ScrollItem.Pattern.ScrollIntoView();
            }
        }
        catch
        {
            // Best-effort - a row already fully on-screen doesn't need this.
        }

        return TryDoubleClickElement(match, log, "UIA deep search");
    }

    private static AutomationElement? FindDescendantByAutomationId(AutomationElement root, string automationId)
    {
        try
        {
            return root.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>DFS via RawViewWalker (unbounded control-view depth — WinForms/third-party grids commonly hide rows from the CONTROL view), capped at MaxRowSearchNodes so a huge/broken tree can't hang the row-selection step.</summary>
    private AutomationElement? DeepFindRowElement(AutomationElement root, string rowText)
    {
        if (string.IsNullOrWhiteSpace(rowText)) return null;

        ITreeWalker walker;
        try
        {
            walker = GetOrCreateAutomation().TreeWalkerFactory.GetRawViewWalker();
        }
        catch
        {
            return null;
        }

        var visited = 0;
        var stack = new Stack<AutomationElement>();
        stack.Push(root);

        while (stack.Count > 0 && visited < MaxRowSearchNodes)
        {
            var current = stack.Pop();
            visited++;

            if (!ReferenceEquals(current, root) && IsRowTextMatch(current, rowText))
            {
                return current;
            }

            try
            {
                var child = walker.GetFirstChild(current);
                while (child is not null && visited < MaxRowSearchNodes)
                {
                    stack.Push(child);
                    child = walker.GetNextSibling(child);
                }
            }
            catch
            {
                // Best-effort - a subtree that can't be walked is simply skipped.
            }
        }

        return null;
    }

    private static bool IsRowTextMatch(AutomationElement element, string rowText)
    {
        ControlType controlType;
        try { controlType = element.ControlType; } catch { return false; }
        if (!RowSearchControlTypes.Contains(controlType)) return false;

        if (TryGetName(element, out var name) && ReportRowTextMatcher.IsExactMatch(name, rowText)) return true;
        if (TryGetValuePatternValue(element, out var value) && ReportRowTextMatcher.IsExactMatch(value, rowText)) return true;
        if (TryGetLegacyIAccessibleNameOrValue(element, out var legacyText) && ReportRowTextMatcher.IsExactMatch(legacyText, rowText)) return true;

        return false;
    }

    private static bool TryGetName(AutomationElement element, out string? name)
    {
        try
        {
            name = element.Name;
            return !string.IsNullOrEmpty(name);
        }
        catch
        {
            name = null;
            return false;
        }
    }

    private static bool TryGetValuePatternValue(AutomationElement element, out string? value)
    {
        try
        {
            if (element.Patterns.Value.IsSupported)
            {
                value = element.Patterns.Value.Pattern.Value.ValueOrDefault;
                return !string.IsNullOrEmpty(value);
            }
        }
        catch
        {
            // fall through
        }

        value = null;
        return false;
    }

    /// <summary>
    /// UNVERIFIED assumption (same risk posture as this file's own header
    /// doc): FlaUI's ILegacyIAccessiblePattern exposes Name/Value as
    /// AutomationProperty&lt;string&gt; (read via .ValueOrDefault, the same
    /// pattern this file already uses for SelectionItem.IsSelected) —
    /// confirmed present as pattern members in the referenced FlaUI.Core
    /// 4.0.0 package's own metadata, but not build/run-verified on this
    /// Mac (no dotnet/Windows here). If the exact property shape differs,
    /// this degrades to "always returns false" (caught below), which
    /// falls through to the next row-selection strategy rather than
    /// crashing the report.
    /// </summary>
    private static bool TryGetLegacyIAccessibleNameOrValue(AutomationElement element, out string? text)
    {
        try
        {
            if (element.Patterns.LegacyIAccessible.IsSupported)
            {
                var pattern = element.Patterns.LegacyIAccessible.Pattern;

                var name = SafeLegacyIAccessibleValue(() => pattern.Name.ValueOrDefault);
                if (!string.IsNullOrEmpty(name))
                {
                    text = name;
                    return true;
                }

                var value = SafeLegacyIAccessibleValue(() => pattern.Value.ValueOrDefault);
                if (!string.IsNullOrEmpty(value))
                {
                    text = value;
                    return true;
                }
            }
        }
        catch
        {
            // fall through
        }

        text = null;
        return false;
    }

    private static string? SafeLegacyIAccessibleValue(Func<string?> read)
    {
        try { return read(); }
        catch { return null; }
    }

    // --- Strategy 2b: grid keyboard navigation ---

    /// <summary>
    /// GOAL brief fix 2b: click once inside the grid to focus it, Ctrl+Home,
    /// type the target's first few characters for incremental search,
    /// verify via SelectionPattern/focused-element Name/LegacyIAccessible
    /// value; else step Down one row at a time (max MaxGridKeyboardSteps)
    /// until the selected/focused row's text exactly matches; then Enter —
    /// falling back to a direct double-click on the believed-selected row
    /// if no new Pioneer-owned top-level window appears within
    /// GridKeyboardEnterConfirmationTimeout (round 6: title-agnostic - see
    /// WaitForNewPioneerWindow's own doc - rather than the old "titled
    /// 'Report Parameters'" check, which is exactly what missed the popup
    /// in Will's build da39140 run and let Enter's own arrow-key fallback
    /// path scroll the now-open popup's date field instead).
    /// </summary>
    private bool TryGridKeyboardRowSelect(string rowText, IReadOnlyCollection<IntPtr> baselineHandles, Action<string> log, CancellationToken ct)
    {
        log($"    strategy 2/3 (grid keyboard navigation) for '{rowText}'...");

        var workArea = FindDescendantByAutomationId(_mainWindow!, FinancialReportsWorkAreaAutomationId);
        if (workArea is null)
        {
            log($"    '{FinancialReportsWorkAreaAutomationId}' pane not found.");
            return false;
        }

        Rectangle workAreaRect;
        try { workAreaRect = workArea.BoundingRectangle; }
        catch { log("    could not read the work area's bounding rectangle."); return false; }

        if (workAreaRect.Width <= 0 || workAreaRect.Height <= 0)
        {
            log("    work area has no usable bounding rectangle.");
            return false;
        }

        var focusPoint = new Point(
            workAreaRect.X + (int)(workAreaRect.Width * 0.4),
            workAreaRect.Y + FirstDataRowOffsetFromWorkAreaTop);

        if (!TryClickPoint(focusPoint, log, "grid keyboard")) return false;

        try
        {
            ct.ThrowIfCancellationRequested();
            if (!EnsurePioneerForeground(log)) return false;
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.HOME);
            Thread.Sleep(PollInterval);

            ct.ThrowIfCancellationRequested();
            if (!EnsurePioneerForeground(log)) return false;
            Keyboard.Type(rowText.Length > 3 ? rowText.Substring(0, 3) : rowText);
            Thread.Sleep(PollInterval);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // fall through to the step-down loop below - incremental
            // search isn't supported by every grid.
        }

        var currentText = TryGetSelectedOrFocusedRowText(workArea);
        var steps = 0;
        while (!ReportRowTextMatcher.IsExactMatch(currentText, rowText) && steps < MaxGridKeyboardSteps)
        {
            ct.ThrowIfCancellationRequested();
            if (!EnsurePioneerForeground(log)) return false;

            try { Keyboard.Type(VirtualKeyShort.DOWN); }
            catch { break; }

            Thread.Sleep(PollInterval);
            currentText = TryGetSelectedOrFocusedRowText(workArea);
            steps++;
        }

        if (!ReportRowTextMatcher.IsExactMatch(currentText, rowText))
        {
            log($"    grid keyboard strategy could not locate row '{rowText}' after {steps} step(s).");
            return false;
        }

        if (!EnsurePioneerForeground(log)) return false;
        try { Keyboard.Type(VirtualKeyShort.RETURN); }
        catch { return false; }

        if (WaitForNewPioneerWindow(baselineHandles, GridKeyboardEnterConfirmationTimeout, log, ct) is not null)
        {
            return true;
        }

        log("    no new Pioneer window appeared after Enter - falling back to a direct double-click on the selected row.");

        var focusedElement = SafeFocusedElement();
        return focusedElement is not null && TryDoubleClickElement(focusedElement, log, "grid keyboard fallback");
    }

    /// <summary>Selected-item Name (via SelectionPattern) if the work area/grid supports it, else the focused element's Name, else its LegacyIAccessible Name/Value — whichever is readable first.</summary>
    private string? TryGetSelectedOrFocusedRowText(AutomationElement workArea)
    {
        try
        {
            if (workArea.Patterns.Selection.IsSupported)
            {
                var selection = workArea.Patterns.Selection.Pattern.Selection.ValueOrDefault;
                if (selection is { Length: > 0 })
                {
                    var name = SafeName(selection[0]);
                    if (!string.IsNullOrEmpty(name)) return name;
                }
            }
        }
        catch
        {
            // fall through to the focused-element checks below
        }

        var focused = SafeFocusedElement();
        if (focused is null) return null;

        if (TryGetName(focused, out var focusedName)) return focusedName;
        return TryGetLegacyIAccessibleNameOrValue(focused, out var legacyText) ? legacyText : null;
    }

    private AutomationElement? SafeFocusedElement()
    {
        try { return GetOrCreateAutomation().FocusedElement(); }
        catch { return null; }
    }

    // --- Strategy 2c: OCR ---

    /// <summary>
    /// GOAL brief fix 2c: captures the FinancialReportsWorkArea pane via
    /// the SAME OCR engine the Verify-mode capture path already uses
    /// (Ocr/EscriptImageCapture.CaptureRegion + IOcrEngine — no new OCR
    /// dependency), then hands the recognized Words to the pure
    /// ReportGridOcrMatcher (Reports/ReportGridOcr.cs) to find the exact
    /// "Report Name" column line, and double-clicks its centre.
    ///
    /// Review fix (PR #11 blocker): the work-area rect is re-read AFTER
    /// RecognizeAsync's await returns and compared (WorkAreaStability) to
    /// the rect captured before it — if Pioneer's window moved/resized
    /// meanwhile, the pre-await rect is stale (still Pioneer-owned, so
    /// IsPointOwnedByPioneer alone wouldn't catch it, but a different
    /// row/pane could be under that offset now). One retry (fresh
    /// capture+OCR) is allowed; a second mismatch aborts the strategy
    /// rather than clicking on a guess. The matched OCR point is also
    /// scaled from the captured BITMAP's pixel space into the CURRENT
    /// rect's coordinate space (OcrCaptureScale — a no-op 1.0/1.0 scale
    /// in the ordinary case, see that class's doc) and verified to fall
    /// inside the current rect before the click.
    /// </summary>
    private async Task<bool> TryOcrRowSelect(string rowText, Action<string> log, CancellationToken ct)
    {
        log($"    strategy 3/3 (OCR) for '{rowText}'...");

        var workArea = FindDescendantByAutomationId(_mainWindow!, FinancialReportsWorkAreaAutomationId);
        if (workArea is null)
        {
            log($"    '{FinancialReportsWorkAreaAutomationId}' pane not found.");
            return false;
        }

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            Rectangle captureRect;
            try { captureRect = workArea.BoundingRectangle; }
            catch { log("    could not read the work area's bounding rectangle for OCR capture."); return false; }

            if (captureRect.Width <= 0 || captureRect.Height <= 0)
            {
                log("    work area has no usable bounding rectangle for OCR capture.");
                return false;
            }

            OcrTextResult ocrResult;
            int bitmapWidth;
            int bitmapHeight;
            try
            {
                using var bitmap = EscriptImageCapture.CaptureRegion(captureRect);
                bitmapWidth = bitmap.Width;
                bitmapHeight = bitmap.Height;
                ocrResult = await _ocrEngine.RecognizeAsync(bitmap, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                log($"    OCR capture/recognition failed: {ex.Message}");
                return false;
            }

            Rectangle currentRect;
            try { currentRect = workArea.BoundingRectangle; }
            catch { log("    could not re-read the work area's bounding rectangle after OCR."); return false; }

            if (WorkAreaStability.HasMoved(
                    captureRect.X, captureRect.Y, captureRect.Width, captureRect.Height,
                    currentRect.X, currentRect.Y, currentRect.Width, currentRect.Height))
            {
                if (attempt == 1)
                {
                    log("    work area moved during OCR - retrying once.");
                    continue;
                }

                log("    work area moved during OCR again on the retry - aborting the OCR strategy rather than clicking a stale offset.");
                return false;
            }

            var match = ReportGridOcrMatcher.FindReportNameLine(ocrResult.Words, rowText);
            if (match is null)
            {
                log($"    OCR did not find an exact 'Report Name' column match for '{rowText}'.");
                return false;
            }

            var (scaleX, scaleY) = OcrCaptureScale.ComputeScale(currentRect.Width, currentRect.Height, bitmapWidth, bitmapHeight);
            var (screenX, screenY) = OcrCaptureScale.ToScreenPoint(match.Value.CenterX, match.Value.CenterY, scaleX, scaleY, currentRect.X, currentRect.Y);
            var screenCenter = new Point((int)Math.Round(screenX), (int)Math.Round(screenY));

            if (!currentRect.Contains(screenCenter))
            {
                log("    matched OCR point fell outside the current work area rectangle - aborting rather than risk a wrong click.");
                return false;
            }

            return TryDoubleClickPoint(screenCenter, log, "OCR");
        }

        return false;
    }

    /// <summary>Synchronous wrapper for ReportRowSelectionSequencer's Func&lt;string, bool&gt; strategy slot - same "this file already blocks on async work from a sync helper" idiom used throughout this class.</summary>
    private bool TryOcrRowSelectSync(string rowText, Action<string> log, CancellationToken ct) =>
        TryOcrRowSelect(rowText, log, ct).GetAwaiter().GetResult();

    // --- Shared click helpers ---

    /// <summary>Double-clicks an element's bounding-rectangle centre — same foreground/click-point safety guards as TryActivateRibbonElement's single click (review fix precedent: re-check EnsurePioneerForeground AND independently confirm the window physically at the click point is Pioneer's, immediately before the click).</summary>
    private bool TryDoubleClickElement(AutomationElement element, Action<string> log, string strategyLabel)
    {
        Rectangle rect;
        try { rect = element.BoundingRectangle; }
        catch { log($"    [{strategyLabel}] could not read the element's bounding rectangle."); return false; }

        if (rect.Width <= 0 || rect.Height <= 0)
        {
            log($"    [{strategyLabel}] element has no usable bounding rectangle.");
            return false;
        }

        var center = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        return TryDoubleClickPoint(center, log, strategyLabel);
    }

    /// <summary>Never double-clicks if the foreground window isn't Pioneer's, or if the window physically at the click point isn't Pioneer's (GOAL brief fix 2: "Never double-click if the foreground window is not Pioneer's").</summary>
    private bool TryDoubleClickPoint(Point point, Action<string> log, string strategyLabel)
    {
        if (!EnsurePioneerForeground(log))
        {
            log($"    [{strategyLabel}] skipping double-click - Pioneer lost focus");
            return false;
        }

        if (!IsPointOwnedByPioneer(point))
        {
            log($"    [{strategyLabel}] skipping double-click - the window at the click point is not Pioneer's");
            return false;
        }

        try
        {
            Mouse.LeftDoubleClick(point);
            log($"    [{strategyLabel}] double-clicked at ({point.X}, {point.Y})");
            return true;
        }
        catch (Exception ex)
        {
            log($"    [{strategyLabel}] double-click failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Single click, same foreground/click-point safety guards as TryDoubleClickPoint - used only to focus the grid before keyboard navigation (strategy 2b), never to activate a row by itself.</summary>
    private bool TryClickPoint(Point point, Action<string> log, string strategyLabel)
    {
        if (!EnsurePioneerForeground(log))
        {
            log($"    [{strategyLabel}] skipping click - Pioneer lost focus");
            return false;
        }

        if (!IsPointOwnedByPioneer(point))
        {
            log($"    [{strategyLabel}] skipping click - the window at the click point is not Pioneer's");
            return false;
        }

        try
        {
            Mouse.LeftClick(point);
            return true;
        }
        catch (Exception ex)
        {
            log($"    [{strategyLabel}] click failed: {ex.Message}");
            return false;
        }
    }

    // --- Row-selection diagnostic dump (GOAL brief fix 2: RAW view, depth 8) ---

    /// <summary>
    /// Extends the diagnostic-dump idea from WriteUiaDiagnosticDump to
    /// this step specifically: walks the RAW UIA view (not the depth-3
    /// CONTROL view the general dump uses) under FinancialReportsWorkArea
    /// to depth MaxRowDiagnosticDumpDepth, so a row-selection failure's
    /// log shows the grid's real structure — control types, class names,
    /// and which patterns each element actually supports. Names are
    /// redacted per ReportRowNameRedaction (catalog row name / column
    /// header allowlist), NOT UiaNameRedaction's control-type allowlist —
    /// see that class's own doc for why this dump needs a different rule.
    /// </summary>
    private void WriteRowSelectionDiagnosticDump(string rowText, Action<string> log)
    {
        try
        {
            log($"--- UIA diagnostic dump (report row '{rowText}') ---");

            if (_mainWindow is null)
            {
                log("Diagnostics: no main window reference to dump.");
                log("--- end diagnostic dump ---");
                return;
            }

            var workArea = FindDescendantByAutomationId(_mainWindow, FinancialReportsWorkAreaAutomationId);
            if (workArea is null)
            {
                log($"Diagnostics: '{FinancialReportsWorkAreaAutomationId}' pane not found - falling back to the general navigation dump.");
                WriteUiaDiagnosticDump($"report row '{rowText}'", log);
                return;
            }

            var snapshots = CaptureRawViewSnapshots(workArea, MaxRowDiagnosticDumpDepth);
            foreach (var line in UiaDumpFormatter.FormatElementDump(snapshots, MaxDiagnosticDumpLines, e => ReportRowNameRedaction.RedactIfNeeded(e.Name)))
            {
                log(line);
            }

            log("--- end diagnostic dump ---");
        }
        catch (Exception ex)
        {
            log($"Diagnostics: row selection dump failed - {ex.Message}");
        }
    }

    private List<UiaElementSnapshot> CaptureRawViewSnapshots(AutomationElement root, int maxDepth)
    {
        var result = new List<UiaElementSnapshot>();

        ITreeWalker walker;
        try { walker = GetOrCreateAutomation().TreeWalkerFactory.GetRawViewWalker(); }
        catch { return result; }

        CaptureRawViewRecursive(walker, root, 1, maxDepth, result);
        return result;
    }

    private static void CaptureRawViewRecursive(ITreeWalker walker, AutomationElement element, int depth, int maxDepth, List<UiaElementSnapshot> result)
    {
        if (depth > maxDepth) return;
        if (result.Count >= MaxRowDiagnosticDumpNodes) return;

        AutomationElement? child;
        try { child = walker.GetFirstChild(element); }
        catch { return; }

        while (child is not null)
        {
            if (result.Count >= MaxRowDiagnosticDumpNodes) return;

            var name = SafeName(child);
            if (!string.IsNullOrEmpty(name))
            {
                string controlType;
                try { controlType = child.ControlType.ToString(); } catch { controlType = "<unknown>"; }

                string automationId;
                try { automationId = child.AutomationId ?? string.Empty; } catch { automationId = string.Empty; }

                var className = SafeClassName(child);
                var patterns = SafeSupportedPatternsSummary(child);

                result.Add(new UiaElementSnapshot(controlType, name, automationId, className, depth, patterns));
            }

            CaptureRawViewRecursive(walker, child, depth + 1, maxDepth, result);

            try { child = walker.GetNextSibling(child); }
            catch { child = null; }
        }
    }

    private static string SafeSupportedPatternsSummary(AutomationElement element)
    {
        var supported = new List<string>();
        TryAddSupportedPattern(() => element.Patterns.Invoke.IsSupported, "Invoke", supported);
        TryAddSupportedPattern(() => element.Patterns.SelectionItem.IsSupported, "SelectionItem", supported);
        TryAddSupportedPattern(() => element.Patterns.Selection.IsSupported, "Selection", supported);
        TryAddSupportedPattern(() => element.Patterns.ScrollItem.IsSupported, "ScrollItem", supported);
        TryAddSupportedPattern(() => element.Patterns.Value.IsSupported, "Value", supported);
        TryAddSupportedPattern(() => element.Patterns.LegacyIAccessible.IsSupported, "LegacyIAccessible", supported);
        TryAddSupportedPattern(() => element.Patterns.ExpandCollapse.IsSupported, "ExpandCollapse", supported);
        return string.Join(",", supported);
    }

    private static void TryAddSupportedPattern(Func<bool> check, string name, List<string> supported)
    {
        try { if (check()) supported.Add(name); }
        catch { /* pattern probe best-effort */ }
    }

    // ------------------------------------------------------------------
    // DIAGNOSTICS (GOAL brief step 1 - "make the next run self-diagnosing")
    // ------------------------------------------------------------------

    /// <summary>
    /// Writes a compact, capped UIA dump into the run log via
    /// <paramref name="log"/> (ReportsCoordinator wires this to BOTH
    /// ReportsLog.Append and the run-&lt;timestamp&gt;.log - see
    /// ReportsCoordinator.RunAsync) whenever a navigation/lookup step
    /// fails: the main window's own identity, its descendants to depth 3,
    /// and every top-level window owned by Pioneer's process. PHI-safe by
    /// construction - see UiaDumpFormatter's class doc; never logs a
    /// field VALUE, only control names/types/ids. Best-effort: a failure
    /// while building the dump itself is logged and swallowed rather than
    /// turning a report failure into an unhandled exception.
    /// </summary>
    private void WriteUiaDiagnosticDump(string context, Action<string> log)
    {
        try
        {
            log($"--- UIA diagnostic dump ({context}) ---");

            if (_mainWindow is null)
            {
                log("Diagnostics: no main window reference to dump.");
                return;
            }

            var mainName = SafeName(_mainWindow);
            var mainClass = SafeClassName(_mainWindow);
            var mainHandle = SafeNativeHandle(_mainWindow);
            log(UiaDumpFormatter.FormatMainWindowHeader(mainName, mainClass, _pioneerProcessId, mainHandle));

            var snapshots = CaptureDescendantSnapshots(_mainWindow, MaxDiagnosticDumpDepth);
            foreach (var line in UiaDumpFormatter.FormatElementDump(snapshots, MaxDiagnosticDumpLines))
            {
                log(line);
            }

            var topLevel = EnumeratePioneerOwnedTopLevelWindows()
                .Select(w => new TopLevelWindowSnapshot(w.Candidate.Title, SafeClassName(w.Window)))
                .ToList();
            log("Top-level Pioneer-owned windows:");
            foreach (var line in UiaDumpFormatter.FormatTopLevelWindowList(topLevel))
            {
                log(line);
            }

            log("--- end diagnostic dump ---");
        }
        catch (Exception ex)
        {
            log($"Diagnostics: dump failed - {ex.Message}");
        }
    }

    private static List<UiaElementSnapshot> CaptureDescendantSnapshots(AutomationElement root, int maxDepth)
    {
        var result = new List<UiaElementSnapshot>();
        CaptureDescendantsRecursive(root, 1, maxDepth, result);
        return result;
    }

    /// <summary>Skips empty-name elements from the RESULT (GOAL brief: "skip empty-name elements") but still recurses into their children - a nameless container can still hold named descendants. Caps total captured elements well above MaxDiagnosticDumpLines so a huge tree can't make this best-effort walk itself slow, while still leaving UiaDumpFormatter the final say on how many lines are actually written.</summary>
    private static void CaptureDescendantsRecursive(AutomationElement element, int depth, int maxDepth, List<UiaElementSnapshot> result)
    {
        if (depth > maxDepth) return;
        if (result.Count >= MaxDiagnosticDumpLines * 2) return;

        AutomationElement[] children;
        try { children = element.FindAllChildren(); }
        catch { return; }

        foreach (var child in children)
        {
            if (result.Count >= MaxDiagnosticDumpLines * 2) return;

            var name = SafeName(child);
            if (!string.IsNullOrEmpty(name))
            {
                string controlType;
                try { controlType = child.ControlType.ToString(); } catch { controlType = "<unknown>"; }

                string automationId;
                try { automationId = child.AutomationId ?? string.Empty; } catch { automationId = string.Empty; }

                var className = SafeClassName(child);

                result.Add(new UiaElementSnapshot(controlType, name, automationId, className, depth));
            }

            CaptureDescendantsRecursive(child, depth + 1, maxDepth, result);
        }
    }

    private static string SafeName(AutomationElement element)
    {
        try { return element.Name ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static string SafeClassName(AutomationElement element)
    {
        try { return element.ClassName ?? string.Empty; }
        catch { return string.Empty; }
    }

    private bool InvokeSearchOrF12(Action<string> log)
    {
        var searchButton = FindDescendantByName(_mainWindow!, "Search");
        if (searchButton is not null && SelectOrInvoke(searchButton)) return true;

        log("Search button not found by name - falling back to F12 key.");
        try
        {
            Keyboard.Type(VirtualKeyShort.F12);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// <paramref name="searchRoot"/> is the actual window that owns the
    /// export control — the PDF export button lives on the report
    /// PREVIEW window's own toolbar (a separate top-level window from
    /// Pioneer's main ribbon window - see FindPreviewWindow), while the
    /// Excel export command lives inside the Payments screen hosted on
    /// _mainWindow itself. Passed explicitly rather than assumed, so this
    /// method never accidentally searches (or clicks into) the wrong
    /// window.
    /// </summary>
    private bool InvokeExportButton(AutomationElement searchRoot, string primaryName, string toolTipContains, Action<string> log)
    {
        var button = FindDescendantByName(searchRoot, primaryName);
        if (button is not null && SelectOrInvoke(button)) return true;

        // Fallback: some toolbar buttons expose only an icon Name with the
        // real label as a ToolTip (see GOAL brief: "the PDF button's
        // tooltip is 'Export to PDF'") - walk descendants looking for one
        // whose HelpText/Name contains the expected substring.
        try
        {
            var descendants = searchRoot.FindAllDescendants();
            foreach (var candidate in descendants)
            {
                string? name;
                try { name = candidate.Name; } catch { continue; }
                if (!string.IsNullOrEmpty(name) && name.Contains(toolTipContains, StringComparison.OrdinalIgnoreCase))
                {
                    if (SelectOrInvoke(candidate)) return true;
                }
            }
        }
        catch
        {
            // fall through to false below
        }

        log($"Could not find export control '{primaryName}'.");
        return false;
    }

    // ------------------------------------------------------------------
    // DATE PARAMETERS
    // ------------------------------------------------------------------

    /// <summary>
    /// Round 4 rewrite (W-T92 round 4 — Will's build-67a1566 report,
    /// verbatim: "it's making it to the report parameters screen, then it
    /// does something, then it starts scrolling through the months in the
    /// initial date window, probably pushing the down arrow. You need to
    /// use tab to get between fields"). Purely keyboard, macro-style,
    /// replayed against the SPECIFIC "Report Parameters" window
    /// <paramref name="parametersWindow"/> — see ReplayReportParameterKeyPlan
    /// and the pure Reports/ReportParameterKeys.cs plan builder it replays.
    /// </summary>
    private bool SetReportParameters(ReportRunItem item, AutomationElement parametersWindow, Action<string> log)
    {
        if (item.Entry.ParameterKind == ReportParameterKind.PaymentsSearch)
        {
            // Payments never reaches here - RunFinancialReport is never
            // called for a PaymentsSearch entry (ReportsCoordinator routes
            // it to RunPaymentsExport instead).
            return false;
        }

        var plan = ReportParameterKeyPlan.Build(item.Entry, item.Begin, item.End);
        return ReplayReportParameterKeyPlan(plan, parametersWindow, log);
    }

    /// <summary>
    /// Sends one ReportParameterKeyPlan (Reports/ReportParameterKeys.cs)
    /// keystroke-for-keystroke against <paramref name="parametersWindow"/>
    /// — never a UIA field lookup, never a click. Same per-key
    /// foreground-check convention as TryKeyboardKeyTipsNavigate:
    /// re-verify foreground == THIS SPECIFIC window (EnsureWindowForeground,
    /// not just "owned by Pioneer's process") immediately before every key,
    /// aborting rather than risk a keystroke landing on the wrong window. A
    /// small settle (KeyEntrySettleDelay) follows every key/action.
    ///
    /// Round 4 rewrite (W-T92 round 4 — see SetReportParameters' doc for
    /// Will's exact report). Rounds 3-5 assumed the popup's Begin/Date
    /// field is already focused the instant it opens and tried to force
    /// that assumption true with a click - that click (on whatever
    /// happened to have OS focus) is exactly what could land on a
    /// calendar-picker button or some other control and start "scrolling
    /// through months". This version never clicks and never calls
    /// SelectAll/Ctrl+V on the default path at all - it just sends each
    /// report's own LeadingTabs first (from ReportCatalogEntry, taken
    /// verbatim from Will's own working Macro Express recordings -
    /// Reports/recipes/README-macro-strings.txt), then types the date text
    /// itself. After each date, the focused field's value is read back via
    /// ValuePattern; a mismatch gets exactly ONE retry using the OLD
    /// clipboard-paste path (Ctrl+A, Ctrl+V - ClipboardHelper.TrySetText,
    /// retrying/STA-marshaled) on the SAME field, and a second mismatch
    /// aborts the whole plan without ever sending F12 - never run a report
    /// against a wrong date. Belt-and-braces: before the very first key of
    /// the plan, this still just LOGS whether NumLock is off
    /// (GetKeyState(VK_NUMLOCK)) - never toggled, since silently flipping a
    /// pharmacist's NumLock state would be its own surprise.
    ///
    /// Round 5 rewrite (W-T92, Will verbatim: "I have described the
    /// problem to you in immaculate detail and have given you even the
    /// exact keystrokes that are needed through the original macro file I
    /// sent you. Fucking figure it out."). Round 4's date text was bare
    /// "MMddyyyy" digits sent as KEYEVENTF_UNICODE packets - neither
    /// matches the macro. Will's macro types "MM-dd-yyyy"/"MM-dd-yy" (see
    /// ReportDateKeys) as real virtual-key + scan-code keystrokes (Macro
    /// Express's default "Text Type" mode is "simulate keystrokes", not
    /// Unicode input) - every Tab/date/F12 send below now goes through
    /// NativeInput's raw SendInput helpers (SendSpecialKey/TypeKeystrokes)
    /// instead of FlaUI.Core.Input.Keyboard/TypeUnicodeText, so every key
    /// this method sends carries both wVk and wScan, exactly like the
    /// macro's own physical-keyboard replay. The focused element's
    /// ClassName/Name/AutomationId (DescribeControl) is now logged after
    /// every key action, not just before Tab groups, so the next failure
    /// can be diagnosed from the run log alone.
    /// </summary>
    private bool ReplayReportParameterKeyPlan(IReadOnlyList<ReportParameterKeyAction> plan, AutomationElement parametersWindow, Action<string> log)
    {
        if (plan.Count == 0) return true;

        var typeCount = 0;
        foreach (var a in plan)
        {
            if (a.Kind == ReportParameterKeyActionKind.TypeText) typeCount++;
        }
        var typeSeen = 0;

        var numLockOn = (GetKeyState(VK_NUMLOCK) & 1) != 0;
        log($"  NumLock is {(numLockOn ? "ON" : "OFF")} before date entry.");

        var initialFocused = SafeFocusedElement();
        log($"  focused control before date entry: {DescribeControl(initialFocused)}");

        for (var i = 0; i < plan.Count; i++)
        {
            var action = plan[i];

            if (!EnsureWindowForeground(parametersWindow, log, "Report Parameters window"))
            {
                log("  aborting date entry - 'Report Parameters' window lost focus.");
                return false;
            }

            if (action.Kind == ReportParameterKeyActionKind.Tab
                && (i == 0 || plan[i - 1].Kind != ReportParameterKeyActionKind.Tab))
            {
                var tabCount = 1;
                while (i + tabCount < plan.Count && plan[i + tabCount].Kind == ReportParameterKeyActionKind.Tab) tabCount++;
                log($"  key Tab x{tabCount} (focused before: {DescribeControl(SafeFocusedElement())})");
            }

            try
            {
                switch (action.Kind)
                {
                    case ReportParameterKeyActionKind.Tab:
                        if (!NativeInput.SendSpecialKey((ushort)VirtualKeyShort.TAB, sendFailure => log($"  Tab: {sendFailure}")))
                        {
                            log("  Tab: SendInput reported a failed send.");
                        }
                        if (i + 1 >= plan.Count || plan[i + 1].Kind != ReportParameterKeyActionKind.Tab)
                        {
                            log($"  focused after Tab group: {DescribeControl(SafeFocusedElement())}");
                        }
                        break;

                    case ReportParameterKeyActionKind.ArrowDown:
                        log("  key ArrowDown");
                        if (!NativeInput.SendSpecialKey((ushort)VirtualKeyShort.DOWN, sendFailure => log($"  ArrowDown: {sendFailure}")))
                        {
                            log("  ArrowDown: SendInput reported a failed send.");
                        }
                        log($"  focused after ArrowDown: {DescribeControl(SafeFocusedElement())}");
                        break;

                    case ReportParameterKeyActionKind.TypeText:
                    {
                        // "Begin"/"End" for a two-date plan, "Date" for a
                        // single-date (AsOfDate) plan - derived from
                        // position, not stored on the action itself, so
                        // ReportParameterKeyPlan's own record-equality
                        // golden tests don't have to carry a label field.
                        var fieldLabel = typeCount <= 1 ? "Date" : (typeSeen == 0 ? "Begin" : "End");
                        typeSeen++;

                        var expected = action.Text ?? string.Empty;
                        log($"  text {expected}");
                        log("  sent as vk+scan keystrokes");

                        // Round 5 fix (W-T92, Will verbatim: "...given you
                        // even the exact keystrokes..."): Macro Express's
                        // default Text Type mode sends real VK+scan-code
                        // keystrokes, not KEYEVENTF_UNICODE packets - see
                        // NativeInput.TypeKeystrokes's own doc. SendInput's
                        // return value is still checked the same way
                        // (TypeKeystrokes returns false on any short send);
                        // a failed send is logged distinctly from a
                        // readback mismatch, but still falls through to the
                        // SAME readback+retry logic below - if nothing was
                        // actually typed, the readback will legitimately
                        // show Unavailable/Mismatch and the existing
                        // decision handles it honestly rather than this
                        // branch guessing.
                        if (!NativeInput.TypeKeystrokes(expected, (int)NativeInput.KeystrokeCharDelay.TotalMilliseconds, sendFailure => log($"  {fieldLabel}: {sendFailure}")))
                        {
                            log($"  {fieldLabel}: TypeKeystrokes reported a failed send - checking the readback anyway.");
                        }
                        Thread.Sleep(KeyEntrySettleDelay);

                        var readBack = TryReadFocusedFieldValue();
                        var decision = ReadbackEvaluator.Decide(readBack, expected);

                        if (decision == ReadbackDecision.Ok)
                        {
                            log($"  {fieldLabel} = {expected} ✓ (focused: {DescribeControl(SafeFocusedElement())})");
                            break;
                        }

                        if (decision == ReadbackDecision.Unavailable)
                        {
                            // Round 4 reviewer fix (blocking finding 2): Pioneer's
                            // date fields don't reliably expose ValuePattern (this
                            // file already needed a LegacyIAccessible fallback for
                            // row-text matching - see TryGetLegacyIAccessibleNameOrValue).
                            // TryReadFocusedFieldValue now tries that fallback too,
                            // so "Unavailable" here means BOTH failed to read
                            // anything at all - not that the value is wrong. The
                            // macro-faithful keys were already sent; there is no
                            // signal here to retry against, and treating an
                            // unreadable field as a mismatch would abort (and skip
                            // F12) on EVERY report, every run, which is worse than
                            // just proceeding.
                            log($"  {fieldLabel}: readback unavailable (focused: {DescribeControl(SafeFocusedElement())}) - continuing without a retry.");
                            break;
                        }

                        // ReadbackDecision.Mismatch - a real, different value came back.
                        log($"  {fieldLabel} read back as '{readBack}', expected '{expected}' - retrying once with clipboard paste (Ctrl+A, Ctrl+V).");

                        if (!EnsureWindowForeground(parametersWindow, log, "Report Parameters window"))
                        {
                            log("  aborting date entry - 'Report Parameters' window lost focus during retry.");
                            return false;
                        }

                        if (!ClipboardHelper.TrySetText(expected))
                        {
                            log($"  {fieldLabel}: could not set the clipboard for the paste retry - aborting rather than run the report against a wrong date.");
                            return false;
                        }

                        log("  key Ctrl+A");
                        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
                        Thread.Sleep(KeyEntrySettleDelay);

                        log("  key Ctrl+V");
                        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_V);
                        Thread.Sleep(KeyEntrySettleDelay);

                        var retryReadBack = TryReadFocusedFieldValue();
                        var retryDecision = ReadbackEvaluator.Decide(retryReadBack, expected);

                        if (retryDecision == ReadbackDecision.Ok)
                        {
                            log($"  {fieldLabel} = {expected} ✓ (after paste retry, focused: {DescribeControl(SafeFocusedElement())})");
                        }
                        else if (retryDecision == ReadbackDecision.Unavailable)
                        {
                            log($"  {fieldLabel}: readback still unavailable after the paste retry (focused: {DescribeControl(SafeFocusedElement())}) - continuing without aborting.");
                        }
                        else
                        {
                            log($"  {fieldLabel} still reads back as '{retryReadBack}' after the retry (focused: {DescribeControl(SafeFocusedElement())}) - aborting rather than run the report against a wrong date.");
                            return false;
                        }
                        break;
                    }

                    case ReportParameterKeyActionKind.F12:
                        if (!NativeInput.SendSpecialKey((ushort)VirtualKeyShort.F12, sendFailure => log($"  F12: {sendFailure}")))
                        {
                            log("  F12: SendInput reported a failed send.");
                        }
                        log("  key F12");
                        log($"  focused after F12: {DescribeControl(SafeFocusedElement())}");
                        break;

                    case ReportParameterKeyActionKind.SelectAll:
                    case ReportParameterKeyActionKind.PasteText:
                    default:
                        // Never produced by ReportParameterKeyPlan.Build -
                        // SelectAll/PasteText are sent directly by the
                        // TypeText mismatch-retry branch above, never
                        // queued as their own plan step.
                        log($"  unexpected plan action '{action.Kind}' - ignoring.");
                        break;
                }
            }
            catch (Exception ex)
            {
                log($"  key action '{action.Kind}' failed: {ex.Message}");
                return false;
            }

            Thread.Sleep(KeyEntrySettleDelay);
        }

        return true;
    }

    /// <summary>
    /// ClassName + Name + AutomationId of an element, for the "focused
    /// control before/after" log lines the GOAL brief asks for (step 2:
    /// "Log every keystroke/paste with the focused control's name before
    /// and after"). Round 5 addition (W-T92, GOAL brief step 4): ClassName
    /// too, logged after EVERY key action (each Tab group, each date,
    /// F12) - this is what actually lets the next failure be diagnosed
    /// from Documents\Pioneer Reports\&lt;yyyy-MM&gt;\run-*.log without a live
    /// UIA session, since Name/AutomationId alone were never enough to
    /// tell "the right date field" apart from "some other control the
    /// popup happened to focus". Never throws, never returns an empty
    /// string - always something loggable.
    /// </summary>
    private static string DescribeControl(AutomationElement? element)
    {
        if (element is null) return "(none)";

        var name = TryGetName(element, out var n) ? n : null;
        string automationId;
        try { automationId = element.AutomationId; } catch { automationId = string.Empty; }
        string className;
        try { className = element.ClassName; } catch { className = string.Empty; }

        if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(automationId) && string.IsNullOrEmpty(className)) return "(unnamed)";
        return $"class='{className}' name='{name}' id='{automationId}'";
    }

    /// <summary>Reads back whatever currently has keyboard focus via ValuePattern - the actual verification the GOAL brief asks for ("read it back through UI Automation ValuePattern"). Null (not empty string) means unreadable, distinct from a real empty value.</summary>
    /// <summary>
    /// Round 4 reviewer fix (blocking finding 2): ValuePattern-only left
    /// EVERY readback null on a field that doesn't expose it - this file
    /// already needed the LegacyIAccessible fallback for row-text matching
    /// (TryGetLegacyIAccessibleNameOrValue, used by TrySelectAndOpenReportRow)
    /// because Pioneer's own controls don't reliably expose ValuePattern, so
    /// the date fields in "Report Parameters" are entirely plausible
    /// candidates for the same gap. Falls back to it here too before giving
    /// up (returning null, which ReadbackEvaluator.Decide treats as
    /// Unavailable, not Mismatch - see ReplayReportParameterKeyPlan's
    /// TypeText case).
    /// </summary>
    private string? TryReadFocusedFieldValue()
    {
        var focused = SafeFocusedElement();
        if (focused is null) return null;
        if (TryGetValuePatternValue(focused, out var value)) return value;
        return TryGetLegacyIAccessibleNameOrValue(focused, out var legacyText) ? legacyText : null;
    }

    private bool SetPaymentsDateRange(ReportRunItem item, Action<string> log)
    {
        var editFields = _mainWindow!.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit));
        if (editFields.Length < 2)
        {
            log("Could not find the 'Payment Confirmed Between' fields.");
            return false;
        }

        return SetDateValue(editFields[0], item.Begin) && SetDateValue(editFields[1], item.End);
    }

    /// <summary>ValuePattern.SetValue first; keyboard fallback (click, Ctrl+A, type mm/dd/yyyy) per the GOAL brief. Still used by SetPaymentsDateRange only — RunFinancialReport's own date entry goes through ReplayReportParameterKeyPlan instead (Round 4, W-T92 follow-up — see that method's doc for why a UIA field lookup + ValuePattern was the actual bug on the Report Parameters popup).</summary>
    private static bool SetDateValue(AutomationElement field, DateTime value)
    {
        var text = value.ToString("MM/dd/yyyy");
        try
        {
            if (field.Patterns.Value.IsSupported)
            {
                field.Patterns.Value.Pattern.SetValue(text);
                return true;
            }
        }
        catch
        {
            // fall through to keyboard fallback
        }

        try
        {
            field.Focus();
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
            Keyboard.Type(text);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------
    // ELEMENT LOOKUP
    // ------------------------------------------------------------------

    private static AutomationElement? FindDescendantByName(AutomationElement root, string name, ControlType? controlType = null)
    {
        if (string.IsNullOrEmpty(name)) return null;

        try
        {
            return controlType is { } type
                ? root.FindFirstDescendant(cf => cf.ByName(name).And(cf.ByControlType(type)))
                : root.FindFirstDescendant(cf => cf.ByName(name));
        }
        catch
        {
            return null;
        }
    }

    private static bool SelectOrInvoke(AutomationElement element)
    {
        try
        {
            if (element.Patterns.SelectionItem.IsSupported)
            {
                element.Patterns.SelectionItem.Pattern.Select();
                return true;
            }

            if (element.Patterns.Invoke.IsSupported)
            {
                element.Patterns.Invoke.Pattern.Invoke();
                return true;
            }

            element.Click(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------
    // WAIT / POLL HELPERS
    // ------------------------------------------------------------------

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            bool result;
            try { result = condition(); }
            catch { result = false; }

            if (result) return true;

            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// True once no "Please wait while the report is generated..." modal
    /// is present on any Pioneer-owned window AND the report preview has
    /// appeared as its own top-level window (see FindPreviewWindow) — its
    /// AutomationElement/native handle are captured into
    /// _previewWindowElement/_previewWindowHandle as a side effect, since
    /// this is the ONLY moment the preview can be reliably identified
    /// (review fix, safety blocker 2: ClosePreview must target this exact
    /// captured handle later, never "whatever is foreground then").
    /// </summary>
    private bool IsPreviewReady()
    {
        foreach (var (window, _) in EnumeratePioneerOwnedTopLevelWindows())
        {
            if (FindDescendantByName(window, "Please wait") is not null) return false;
        }

        var preview = FindPreviewWindow();
        if (preview is null) return false;

        _previewWindowElement = preview;
        _previewWindowHandle = SafeNativeHandle(preview);
        return _previewWindowHandle != IntPtr.Zero;
    }

    /// <summary>
    /// Round 7 (W-T92, Will 2026-09-30, verbatim: "when saving the report,
    /// it needs to wait until it is run. The window will show 'Please wait
    /// while the report is generated...' in the bottom left and then
    /// change to 'The report has completed' when it is done. The app needs
    /// to watch for this and not try to save the report until that has
    /// happened, otherwise the report will save empty."). Polls every
    /// PollInterval (~250ms) for Pioneer's own bottom-left status text —
    /// scoped to THIS report's own preview window (FindReportCompletionStatusElement;
    /// see that method's own doc for why never any other Pioneer window) —
    /// feeding whatever it reads through ReportCompletionStatus.Decide, the
    /// pure decision this loop is a thin, build/run-unverifiable-on-this-Mac
    /// shim around (see that class's own doc). Logs every
    /// Generating/Completed/other transition with a UTC timestamp so a
    /// run's log shows exactly when the report actually finished, not just
    /// that it eventually did. Returns true only once Decide says Proceed;
    /// false (never saves) on TimedOut, after logging the last status text
    /// actually seen. If the status element is never found at all during
    /// the wait, a one-time diagnostic dump of every Edit/Text/StatusBar
    /// element's AutomationId/ClassName/ControlType (never a Name/value —
    /// see WriteStatusElementDiagnosticDump) is written before returning
    /// false, so the real status control can be re-identified/re-tuned
    /// from the log alone.
    /// </summary>
    private async Task<bool> WaitForReportCompletionStatus(Action<string> log, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        string? lastStatusText = null;
        string? lastLoggedPhase = null;
        var everFound = false;

        log("Waiting for Pioneer's report-completion status (bottom-left) before saving...");

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var (element, text) = FindReportCompletionStatusElement(log);
            if (element is not null)
            {
                everFound = true;
                lastStatusText = text;

                var phase = ReportCompletionStatus.IsCompleted(text) ? "completed"
                    : ReportCompletionStatus.IsGenerating(text) ? "generating"
                    : "other";
                if (phase != lastLoggedPhase)
                {
                    log($"  report status ({DateTime.UtcNow:O}): {phase} - '{text}'");
                    lastLoggedPhase = phase;
                }
            }

            var decision = ReportCompletionStatus.Decide(lastStatusText, stopwatch.Elapsed, ReportCompletionStatusTimeout);

            if (decision == ReportCompletionDecision.Proceed)
            {
                log($"Report has completed (elapsed={stopwatch.Elapsed.TotalSeconds:0.0}s) - proceeding to save.");
                return true;
            }

            if (decision == ReportCompletionDecision.TimedOut)
            {
                if (!everFound)
                {
                    log("Report-completion status element was never found - dumping status/editable elements once.");
                    WriteStatusElementDiagnosticDump(log);
                }

                log($"Timed out after {ReportCompletionStatusTimeout.TotalSeconds:0}s waiting for the report to finish generating - last status text seen: '{lastStatusText ?? "<none>"}'. Not saving.");
                return false;
            }

            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>ControlTypes eligible for FindReportCompletionStatusElement's search and WriteStatusElementDiagnosticDump's fallback dump — a plain text/status-bar label is where Pioneer would expose this kind of transient message; restricted (not a full-tree walk) so this never accidentally matches an unrelated Edit field's current value.</summary>
    private static readonly ControlType[] ReportStatusSearchControlTypes = { ControlType.Text, ControlType.StatusBar };

    /// <summary>
    /// Reviewer round 7 BLOCKING fix: this used to search EVERY Pioneer-
    /// owned top-level window (EnumeratePioneerOwnedTopLevelWindows), so
    /// report N's own preview window — left open by ClosePreview being
    /// best-effort (TryCloseWindowSafely can leave a window open) and by
    /// RunFinancialReport's failure paths never calling ClosePreview at
    /// all — could still be showing "The report has completed" and
    /// satisfy report N+1's very first 250ms poll tick, saving report
    /// N+1 before it had even started generating (the exact "report will
    /// save empty" bug this whole round exists to prevent — same class of
    /// bug as the documented TryFindFallbackPreviewWindow fix). Scoped now
    /// to _previewWindowElement, THIS report's own confirmed preview
    /// window — reliably fresh by the time this is ever called: it's only
    /// reachable after RunFinancialReport's previewReady gate passed,
    /// which is exactly the code (IsPreviewReady / the fallback path) that
    /// just (re)set _previewWindowElement/_previewWindowHandle for this
    /// specific report. Falls back to _mainWindow ONLY if the status text
    /// isn't found in the preview window and IS found there instead
    /// (logged, so it's visible which window actually matched) — never any
    /// other Pioneer-owned window. Both searches are depth-capped
    /// (ReportStatusSearchMaxDepth) rather than an unbounded
    /// FindAllDescendants, since this runs on every ~250ms tick for up to
    /// 180s (reviewer non-blocking fix).
    /// </summary>
    private (AutomationElement? Element, string? Text) FindReportCompletionStatusElement(Action<string> log)
    {
        if (_previewWindowElement is not null)
        {
            var previewMatch = FindStatusPhraseInWindow(_previewWindowElement, ReportStatusSearchMaxDepth);
            if (previewMatch is { } found) return found;
        }

        if (_mainWindow is not null)
        {
            var mainMatch = FindStatusPhraseInWindow(_mainWindow, ReportStatusSearchMaxDepth);
            if (mainMatch is { } foundInMain)
            {
                log("  report-completion status matched in the PioneerRx main window (not the preview window) - falling back.");
                return foundInMain;
            }
        }

        return (null, null);
    }

    /// <summary>
    /// Depth-capped walk of <paramref name="root"/>'s Text/StatusBar
    /// descendants, collecting every candidate reading and handing them to
    /// ReportCompletionStatus.SelectStatusText — reviewer non-blocking fix:
    /// a Completed reading always wins over a Generating one, regardless
    /// of which one UIA happens to enumerate first, rather than this
    /// method returning on whichever phrase it meets first.
    /// </summary>
    private static (AutomationElement Element, string Text)? FindStatusPhraseInWindow(AutomationElement root, int maxDepth)
    {
        var candidates = new List<(AutomationElement Element, string Text)>();
        CollectStatusPhraseCandidates(root, 1, maxDepth, candidates);
        if (candidates.Count == 0) return null;

        var selectedText = ReportCompletionStatus.SelectStatusText(candidates.Select(c => c.Text));
        if (selectedText is null) return null;

        foreach (var candidate in candidates)
        {
            if (candidate.Text == selectedText) return candidate;
        }

        return null;
    }

    private static void CollectStatusPhraseCandidates(AutomationElement element, int depth, int maxDepth, List<(AutomationElement Element, string Text)> result)
    {
        if (depth > maxDepth) return;

        AutomationElement[] children;
        try { children = element.FindAllChildren(); }
        catch { return; }

        foreach (var child in children)
        {
            ControlType controlType;
            try { controlType = child.ControlType; } catch { controlType = default; }

            if (Array.IndexOf(ReportStatusSearchControlTypes, controlType) >= 0)
            {
                string? name;
                try { name = child.Name; } catch { name = null; }

                if (!string.IsNullOrEmpty(name)
                    && (ReportCompletionStatus.IsGenerating(name) || ReportCompletionStatus.IsCompleted(name)))
                {
                    result.Add((child, name));
                }
            }

            CollectStatusPhraseCandidates(child, depth + 1, maxDepth, result);
        }
    }

    /// <summary>
    /// Round 7 (W-T92): when WaitForReportCompletionStatus's search for the
    /// bottom-left status text finds nothing at all, this walks every
    /// Pioneer-owned top-level window's Edit/Text/StatusBar descendants
    /// (depth-capped, same MaxDiagnosticDumpDepth/MaxDiagnosticDumpLines
    /// limits as the other diagnostic dumps in this file) and logs ONLY
    /// ControlType/AutomationId/ClassName for each — reuses
    /// UiaDumpFormatter.FormatElementDump, which already redacts every
    /// non-chrome-allowlisted ControlType's Name via UiaNameRedaction
    /// (Edit/Text/StatusBar are never on that allowlist), so the real
    /// status text is never written to the log by this path either. Kept
    /// as its own method (not a reuse of WriteUiaDiagnosticDump) since it
    /// filters to status/editable control types only, across every
    /// Pioneer-owned top-level window, not just _mainWindow's own tree.
    /// </summary>
    private void WriteStatusElementDiagnosticDump(Action<string> log)
    {
        try
        {
            log("--- report-completion status element dump ---");

            foreach (var (window, candidate) in EnumeratePioneerOwnedTopLevelWindows())
            {
                log($"  window: '{MainWindowTitleRedaction.RedactIfNeeded(candidate.Title)}' class='{SafeClassName(window)}'");

                var snapshots = CaptureStatusElementSnapshots(window, MaxDiagnosticDumpDepth);
                foreach (var line in UiaDumpFormatter.FormatElementDump(snapshots, MaxDiagnosticDumpLines))
                {
                    log(line);
                }
            }

            log("--- end report-completion status element dump ---");
        }
        catch (Exception ex)
        {
            log($"Diagnostics: report-completion status dump failed - {ex.Message}");
        }
    }

    private static readonly HashSet<ControlType> StatusDiagnosticControlTypes = new()
    {
        ControlType.Edit, ControlType.Text, ControlType.StatusBar
    };

    private static List<UiaElementSnapshot> CaptureStatusElementSnapshots(AutomationElement root, int maxDepth)
    {
        var result = new List<UiaElementSnapshot>();
        CaptureStatusElementsRecursive(root, 1, maxDepth, result);
        return result;
    }

    private static void CaptureStatusElementsRecursive(AutomationElement element, int depth, int maxDepth, List<UiaElementSnapshot> result)
    {
        if (depth > maxDepth) return;
        if (result.Count >= MaxDiagnosticDumpLines * 2) return;

        AutomationElement[] children;
        try { children = element.FindAllChildren(); }
        catch { return; }

        foreach (var child in children)
        {
            if (result.Count >= MaxDiagnosticDumpLines * 2) return;

            ControlType controlType;
            string controlTypeName;
            try { controlType = child.ControlType; controlTypeName = controlType.ToString(); }
            catch { controlType = default; controlTypeName = "<unknown>"; }

            if (StatusDiagnosticControlTypes.Contains(controlType))
            {
                var name = SafeName(child);

                string automationId;
                try { automationId = child.AutomationId ?? string.Empty; } catch { automationId = string.Empty; }

                var className = SafeClassName(child);

                result.Add(new UiaElementSnapshot(controlTypeName, name, automationId, className, depth));
            }

            CaptureStatusElementsRecursive(child, depth + 1, maxDepth, result);
        }
    }

    /// <summary>
    /// Enumerates top-level desktop windows belonging to Pioneer's own
    /// process (_pioneerProcessId, captured by FindMainWindow) — the
    /// shared building block behind FindPreviewWindow, the Save As dialog
    /// lookup (SaveAsWindowSelector.Choose), and the overwrite-confirm
    /// prompt search, so none of those three ever consider a window
    /// belonging to some other running application (review fix, safety
    /// blocker 1's root cause).
    /// </summary>
    private List<(AutomationElement Window, WindowCandidate Candidate)> EnumeratePioneerOwnedTopLevelWindows()
    {
        var result = new List<(AutomationElement, WindowCandidate)>();
        try
        {
            var automation = GetOrCreateAutomation();
            var desktop = automation.GetDesktop();
            foreach (var window in desktop.FindAllChildren())
            {
                var processId = SafeProcessId(window);
                if (processId != _pioneerProcessId) continue;

                string? name;
                try { name = window.Name; } catch { name = null; }

                var handle = SafeNativeHandle(window);
                result.Add((window, new WindowCandidate(name ?? string.Empty, processId, handle)));
            }
        }
        catch
        {
            // Best-effort - an empty list here just means the callers
            // above find nothing and fail the report, never guess.
        }

        return result;
    }

    /// <summary>
    /// The report preview is its OWN top-level window (a separate window
    /// from Pioneer's main ribbon window - confirmed by its toolbar icons
    /// in the GOAL brief: Home/Print Immediately/Export to PDF/etc.), so
    /// this explicitly excludes _mainWindow's own handle before matching
    /// on those toolbar icon names - a false match here would make
    /// InvokeExportButton search (and ClosePreview eventually try to
    /// close) the wrong window.
    /// </summary>
    private AutomationElement? FindPreviewWindow()
    {
        var mainHandle = _mainWindow is null ? IntPtr.Zero : SafeNativeHandle(_mainWindow);

        foreach (var (window, candidate) in EnumeratePioneerOwnedTopLevelWindows())
        {
            if (candidate.Handle != IntPtr.Zero && candidate.Handle == mainHandle) continue;

            if (LooksLikePreviewWindow(window)) return window;
        }

        return null;
    }

    /// <summary>
    /// W-T92 review fix: the one place FindPreviewWindow and
    /// TryFindFallbackPreviewWindow both decide whether a window is
    /// actually the report preview - see PreviewWindowAffordances
    /// (Reports/AutomationWindowSelection.cs) for why this exists and why
    /// it's the SAME two toolbar-icon names for both callers.
    /// </summary>
    private bool LooksLikePreviewWindow(AutomationElement window) =>
        PreviewWindowAffordances.HasAffordance(
            FindDescendantByName(window, PreviewWindowAffordances.PrintImmediately) is not null,
            FindDescendantByName(window, PreviewWindowAffordances.ExportToPdf) is not null);

    private bool IsResultsTabReady()
    {
        var resultsTab = FindDescendantByName(_mainWindow!, "Results", ControlType.TabItem);
        if (resultsTab is null) return false;

        try
        {
            return resultsTab.Patterns.SelectionItem.IsSupported
                ? resultsTab.Patterns.SelectionItem.Pattern.IsSelected.ValueOrDefault
                : true;
        }
        catch
        {
            return true;
        }
    }

    // ------------------------------------------------------------------
    // SAVE AS
    // ------------------------------------------------------------------

    /// <summary>
    /// Waits for a "Save As" dialog, sets its File name box to the FULL
    /// <paramref name="fullPath"/>, presses Enter, answers an overwrite
    /// prompt with Yes if one appears, then waits until the dialog is
    /// gone AND the file exists on disk.
    /// </summary>
    private async Task<bool> SaveAsAndVerify(string fullPath, Action<string> log, CancellationToken ct)
    {
        var dialogAppeared = await WaitUntilAsync(() => FindSaveAsDialog().Field is not null, DialogTimeout, ct).ConfigureAwait(false);
        if (!dialogAppeared)
        {
            log("Save As dialog did not appear.");
            WriteUiaDiagnosticDump("Save As dialog", log);
            return false;
        }

        var (fileNameField, saveAsHandle) = FindSaveAsDialog();
        if (fileNameField is null)
        {
            log("Save As dialog appeared but its File name box could not be found.");
            WriteUiaDiagnosticDump("Save As dialog file name box", log);
            return false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? ".");

            if (fileNameField.Patterns.Value.IsSupported)
            {
                fileNameField.Patterns.Value.Pattern.SetValue(fullPath);
            }
            else
            {
                fileNameField.Focus();
                Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
                Keyboard.Type(fullPath);
            }

            // Review follow-up ("apply the same specific-window-handle rule
            // anywhere else a keystroke could hit the main window
            // unintentionally"): Enter is an untargeted keystroke - go
            // straight to whatever the OS foreground window is. Re-verify
            // that's still THIS SPECIFIC Save As window (by handle, not
            // merely "some Pioneer-owned window") immediately before
            // sending it, rather than assuming focus never moved between
            // SetValue/Focus above and this line.
            if (GetForegroundWindow() != saveAsHandle)
            {
                log("Save As dialog lost focus before Enter could be sent - aborting rather than risk it going to the wrong window.");
                return false;
            }

            Keyboard.Type(VirtualKeyShort.RETURN);
        }
        catch (Exception ex)
        {
            log($"Could not fill in the Save As dialog: {ex.Message}");
            return false;
        }

        // "If duplicate, overwrite" - answer any confirmation prompt with
        // Yes (macro strings: "Confirm Save As"). Review fix (non-blocking
        // item b): a Windows confirmation dialog is its OWN top-level
        // window, not a descendant of _mainWindow - search every
        // Pioneer-owned top-level window (never any other application's)
        // for one that looks like a "Confirm"/"Save As"-titled prompt and
        // press ITS "Yes" button. Best-effort/fail-safe as before - if
        // none appears within a short window this is simply a no-op (no
        // duplicate file, or Pioneer didn't ask).
        await WaitUntilAsync(() =>
        {
            foreach (var (window, candidate) in EnumeratePioneerOwnedTopLevelWindows())
            {
                if (string.IsNullOrEmpty(candidate.Title)) continue;
                if (!candidate.Title.Contains("Confirm", StringComparison.OrdinalIgnoreCase)
                    && !candidate.Title.Contains("Save As", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var yesButton = FindDescendantByName(window, "Yes", ControlType.Button);
                if (yesButton is not null)
                {
                    SelectOrInvoke(yesButton);
                    return true;
                }
            }

            return false;
        }, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);

        var savedFileExists = await WaitUntilAsync(() => File.Exists(fullPath), SaveVerifyTimeout, ct).ConfigureAwait(false);
        if (!savedFileExists)
        {
            log($"Expected file was never found on disk: {fullPath}");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Review fix (safety blocker 1): restricted to top-level windows
    /// belonging to Pioneer's OWN process (SaveAsWindowSelector.Choose),
    /// never "any window on the desktop whose title merely contains
    /// 'Save As'" - a same-titled window from an unrelated application can
    /// no longer receive Pioneer's output path + Enter.
    /// </summary>
    /// <summary>Returns both the File name edit field AND the dialog's own window handle — SaveAsAndVerify needs the handle to re-verify (by specific handle, not just process) that this exact dialog is still frontmost immediately before the untargeted Enter keystroke.</summary>
    private (AutomationElement? Field, IntPtr WindowHandle) FindSaveAsDialog()
    {
        var owned = EnumeratePioneerOwnedTopLevelWindows();
        var candidates = owned.Select(o => o.Candidate).ToList();
        var chosenHandle = SaveAsWindowSelector.Choose(candidates, _pioneerProcessId, GetForegroundWindow());
        if (chosenHandle is null) return (null, IntPtr.Zero);

        var match = owned.FirstOrDefault(o => o.Candidate.Handle == chosenHandle.Value);
        if (match.Window is null) return (null, IntPtr.Zero);

        try
        {
            var editFields = match.Window.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit));
            var field = editFields.Length > 0 ? editFields[0] : null;
            return (field, chosenHandle.Value);
        }
        catch
        {
            return (null, IntPtr.Zero);
        }
    }

    /// <summary>
    /// Review fix (safety blocker 2), generalized (W-T92 date-entry review
    /// fix - non-blocking item): the shared WM_CLOSE-primary/Alt+F4-fallback
    /// close logic, originally inline in ClosePreview only. Never blindly
    /// Alt+F4s "whatever is currently focused" - primary path is a
    /// WM_CLOSE posted straight to <paramref name="handle"/>'s own HWND
    /// (targeted, works regardless of focus); Alt+F4 is only ever used as
    /// a last-resort fallback, and only when PreviewCloseDecision confirms
    /// the OS foreground window IS that exact handle right now. Now also
    /// used by TryFindFallbackPreviewWindow to safely dismiss an unexpected
    /// (non-preview) window that appeared after F12, without risking
    /// Alt+F4 landing on whatever else is focused.
    /// </summary>
    private void TryCloseWindowSafely(IntPtr handle, Action<string> log, string windowLabel)
    {
        if (handle == IntPtr.Zero) return;

        var primaryCloseSucceeded = false;
        try
        {
            primaryCloseSucceeded = PostMessage(handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            primaryCloseSucceeded = false;
        }

        if (primaryCloseSucceeded) return;

        var foreground = GetForegroundWindow();
        if (PreviewCloseDecision.ShouldFallBackToAltF4(primaryCloseSucceeded, handle, foreground))
        {
            try
            {
                Keyboard.TypeSimultaneously(VirtualKeyShort.ALT, VirtualKeyShort.F4);
            }
            catch
            {
                // Best-effort only - see each caller's own doc for why a
                // window left open here isn't itself a failed report.
            }
        }
        else
        {
            log($"Could not confirm {windowLabel} to close it safely - leaving it open rather than risk closing the wrong window.");
        }
    }

    /// <summary>
    /// Review fix (safety blocker 2): closes the SPECIFIC preview window
    /// captured by IsPreviewReady (_previewWindowHandle/_previewWindowElement)
    /// - never blindly Alt+F4s "whatever is currently focused". See
    /// TryCloseWindowSafely for the shared close logic itself - if focus
    /// has reverted to Pioneer's main window (or anywhere else) after Save
    /// As closed, Alt+F4 is never sent.
    /// </summary>
    private void ClosePreview(Action<string> log)
    {
        TryCloseWindowSafely(_previewWindowHandle, log, "the report preview window");

        _previewWindowElement = null;
        _previewWindowHandle = IntPtr.Zero;
    }
}
