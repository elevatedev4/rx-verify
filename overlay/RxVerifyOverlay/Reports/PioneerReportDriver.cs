using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
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

    private const uint WM_CLOSE = 0x0010;

    private UIA3Automation? _automation;
    private AutomationElement? _mainWindow;
    private int _pioneerProcessId;

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

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private UIA3Automation GetOrCreateAutomation() => _automation ??= new UIA3Automation();

    public bool FindMainWindow()
    {
        try
        {
            var automation = GetOrCreateAutomation();
            var desktop = automation.GetDesktop();
            var topLevel = desktop.FindAllChildren();

            foreach (var window in topLevel)
            {
                string? name;
                try { name = window.Name; } catch { continue; }
                if (string.IsNullOrEmpty(name)) continue;

                // Never the Pre-Check/Edit/New-Rx window this app's own
                // Verify/Order modes attach to (Uia/PioneerRxWindow.cs) —
                // the ribbon this driver needs lives on Pioneer's separate
                // main shell window.
                var isPreCheckFamily = false;
                foreach (var prefix in FieldMap.TargetWindowTitlePrefixes)
                {
                    if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        isPreCheckFamily = true;
                        break;
                    }
                }
                if (isPreCheckFamily) continue;

                var processId = SafeProcessId(window);
                var isPioneerProcess = processId != 0 && IsPioneerProcess(processId);

                if (isPioneerProcess || name.Contains("Pioneer", StringComparison.OrdinalIgnoreCase))
                {
                    _mainWindow = window;
                    _pioneerProcessId = processId;
                    BringToForeground(window);
                    return true;
                }
            }

            _mainWindow = null;
            return false;
        }
        catch
        {
            _mainWindow = null;
            return false;
        }
    }

    public async Task<ReportRunResult> RunFinancialReport(ReportRunItem item, Action<string> log, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            ct.ThrowIfCancellationRequested();

            if (!EnsurePioneerForeground(log)) return ReportRunResult.Failed("Pioneer lost focus", stopwatch.Elapsed);
            if (_mainWindow is null) return ReportRunResult.Failed("PioneerRx main window not found", stopwatch.Elapsed);

            log("Opening Analysis > Financial Reports...");
            if (!OpenRibbonScreen("Analysis", "Financial Reports", log))
                return ReportRunResult.Failed("Could not open Analysis > Financial Reports", stopwatch.Elapsed);

            log($"Selecting report row '{item.Entry.PioneerRowText}'...");
            var row = FindDescendantByName(_mainWindow!, item.Entry.PioneerRowText);
            if (row is null) return ReportRunResult.Failed($"Report row '{item.Entry.PioneerRowText}' not found", stopwatch.Elapsed);
            if (!SelectOrInvoke(row)) return ReportRunResult.Failed("Could not select the report row", stopwatch.Elapsed);

            if (!EnsurePioneerForeground(log)) return ReportRunResult.Failed("Pioneer lost focus", stopwatch.Elapsed);

            log("Setting report parameters...");
            if (!SetReportParameters(item, log)) return ReportRunResult.Failed("Could not set report date parameters", stopwatch.Elapsed);

            if (!EnsurePioneerForeground(log)) return ReportRunResult.Failed("Pioneer lost focus", stopwatch.Elapsed);

            log("Running report (View - F12)...");
            if (!InvokeViewOrF12(log)) return ReportRunResult.Failed("Could not start the report (View - F12)", stopwatch.Elapsed);

            log("Waiting for the report to generate...");
            var previewReady = await WaitUntilAsync(() => IsPreviewReady(), DefaultReportTimeout, ct).ConfigureAwait(false);
            if (!previewReady) return ReportRunResult.Failed("Timed out waiting for the report preview", stopwatch.Elapsed);

            if (!EnsurePioneerForeground(log)) return ReportRunResult.Failed("Pioneer lost focus", stopwatch.Elapsed);

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

            if (!EnsurePioneerForeground(log)) return ReportRunResult.Failed("Pioneer lost focus", stopwatch.Elapsed);
            if (_mainWindow is null) return ReportRunResult.Failed("PioneerRx main window not found", stopwatch.Elapsed);

            log("Opening Third Party > Payments...");
            if (!OpenRibbonScreen("Third Party", "Payments", log))
                return ReportRunResult.Failed("Could not open Third Party > Payments", stopwatch.Elapsed);

            log("Selecting the Search tab...");
            var searchTab = FindDescendantByName(_mainWindow!, "Search", ControlType.TabItem);
            if (searchTab is not null) SelectOrInvoke(searchTab);

            if (!EnsurePioneerForeground(log)) return ReportRunResult.Failed("Pioneer lost focus", stopwatch.Elapsed);

            log("Setting 'Payment Confirmed Between' range...");
            if (!SetPaymentsDateRange(item, log)) return ReportRunResult.Failed("Could not set the payment date range", stopwatch.Elapsed);

            if (!EnsurePioneerForeground(log)) return ReportRunResult.Failed("Pioneer lost focus", stopwatch.Elapsed);

            log("Searching (Search - F12)...");
            if (!InvokeSearchOrF12(log)) return ReportRunResult.Failed("Could not run the search (Search - F12)", stopwatch.Elapsed);

            log("Waiting for search results...");
            var resultsReady = await WaitUntilAsync(() => IsResultsTabReady(), DefaultReportTimeout, ct).ConfigureAwait(false);
            if (!resultsReady) return ReportRunResult.Failed("Timed out waiting for search results", stopwatch.Elapsed);

            if (!EnsurePioneerForeground(log)) return ReportRunResult.Failed("Pioneer lost focus", stopwatch.Elapsed);

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
        Thread.Sleep(150);

        if (IsForegroundOwnedByPioneer()) return true;

        log("Pioneer lost focus - aborting this report.");
        return false;
    }

    private bool IsForegroundOwnedByPioneer()
    {
        try
        {
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            GetWindowThreadProcessId(fg, out var pid);
            return _pioneerProcessId != 0 && pid == (uint)_pioneerProcessId;
        }
        catch
        {
            return false;
        }
    }

    private static void BringToForeground(AutomationElement window)
    {
        try
        {
            var handle = SafeNativeHandle(window);
            if (handle != IntPtr.Zero)
            {
                SetForegroundWindow(handle);
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
    /// Selects ribbon tab <paramref name="tabName"/> then invokes
    /// <paramref name="buttonName"/> on it (e.g. "Analysis" then
    /// "Financial Reports"). UIA-first (SelectionItem/Invoke on elements
    /// found by Name), falling back to Will's own keyboard pattern (ALT
    /// to activate ribbon key-tips, then typing the tab/button name) if
    /// no matching UIA element is found — see class doc for why the
    /// exact fallback keystrokes are a best-effort guess pending a live
    /// PioneerRx to confirm ribbon access keys against.
    /// </summary>
    private bool OpenRibbonScreen(string tabName, string buttonName, Action<string> log)
    {
        var tab = FindDescendantByName(_mainWindow!, tabName, ControlType.TabItem);
        if (tab is not null && SelectOrInvoke(tab))
        {
            var button = FindDescendantByName(_mainWindow!, buttonName);
            if (button is not null && SelectOrInvoke(button)) return true;
        }

        log($"UIA navigation to '{tabName}' > '{buttonName}' failed - trying keyboard fallback.");
        try
        {
            // Review fix (non-blocking item a): re-check foreground before
            // EACH keystroke, not just once before the whole sequence -
            // three separate sends (ALT, tab-name letter, button-name
            // letter) is enough time for focus to move between them.
            if (!EnsurePioneerForeground(log)) return false;
            Keyboard.Press(VirtualKeyShort.ALT);
            Keyboard.Release(VirtualKeyShort.ALT);
            Thread.Sleep(200);

            if (!EnsurePioneerForeground(log)) return false;
            Keyboard.Type(tabName.Substring(0, 1));
            Thread.Sleep(300);

            if (!EnsurePioneerForeground(log)) return false;
            Keyboard.Type(buttonName.Substring(0, 1));
            Thread.Sleep(500);
        }
        catch
        {
            return false;
        }

        var confirmButton = FindDescendantByName(_mainWindow!, buttonName);
        return confirmButton is not null;
    }

    private bool InvokeViewOrF12(Action<string> log)
    {
        var viewButton = FindDescendantByName(_mainWindow!, "View");
        if (viewButton is not null && SelectOrInvoke(viewButton)) return true;

        log("View button not found by name - falling back to F12 key.");
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

    private bool SetReportParameters(ReportRunItem item, Action<string> log)
    {
        return item.Entry.ParameterKind switch
        {
            ReportParameterKind.DateRange => SetDateRangeFields(item.Begin, item.End, log),
            ReportParameterKind.AsOfDate => SetSingleDateField(item.End, log),
            // Payments never reaches here - RunFinancialReport is never
            // called for a PaymentsSearch entry (ReportsCoordinator routes
            // it to RunPaymentsExport instead).
            _ => false
        };
    }

    private bool SetDateRangeFields(DateTime begin, DateTime end, Action<string> log)
    {
        var editFields = _mainWindow!.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit));
        if (editFields.Length < 2)
        {
            log("Could not find two date fields for the Begin/End range.");
            return false;
        }

        return SetDateValue(editFields[0], begin) && SetDateValue(editFields[1], end);
    }

    private bool SetSingleDateField(DateTime asOf, Action<string> log)
    {
        var editFields = _mainWindow!.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit));
        if (editFields.Length < 1)
        {
            log("Could not find the As Of date field.");
            return false;
        }

        return SetDateValue(editFields[0], asOf);
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

    /// <summary>ValuePattern.SetValue first; keyboard fallback (click, Ctrl+A, type mm/dd/yyyy) per the GOAL brief.</summary>
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

            if (FindDescendantByName(window, "Print Immediately") is not null
                || FindDescendantByName(window, "Export to PDF") is not null)
            {
                return window;
            }
        }

        return null;
    }

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
            return false;
        }

        var (fileNameField, saveAsHandle) = FindSaveAsDialog();
        if (fileNameField is null)
        {
            log("Save As dialog appeared but its File name box could not be found.");
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
    /// Review fix (safety blocker 2): closes the SPECIFIC preview window
    /// captured by IsPreviewReady (_previewWindowHandle/_previewWindowElement)
    /// - never blindly Alt+F4s "whatever is currently focused". Primary
    /// path is a WM_CLOSE posted straight to that window's own HWND
    /// (targeted, works regardless of focus); Alt+F4 is only ever used as
    /// a last-resort fallback, and only when PreviewCloseDecision confirms
    /// the OS foreground window IS that exact preview handle right now -
    /// if focus has reverted to Pioneer's main window (or anywhere else)
    /// after Save As closed, Alt+F4 is never sent.
    /// </summary>
    private void ClosePreview(Action<string> log)
    {
        var previewHandle = _previewWindowHandle;
        var primaryCloseSucceeded = false;

        if (previewHandle != IntPtr.Zero)
        {
            try
            {
                primaryCloseSucceeded = PostMessage(previewHandle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
            catch
            {
                primaryCloseSucceeded = false;
            }
        }

        if (!primaryCloseSucceeded)
        {
            var foreground = GetForegroundWindow();
            if (PreviewCloseDecision.ShouldFallBackToAltF4(primaryCloseSucceeded, previewHandle, foreground))
            {
                try
                {
                    Keyboard.TypeSimultaneously(VirtualKeyShort.ALT, VirtualKeyShort.F4);
                }
                catch
                {
                    // Best-effort only - a preview left open is a nuisance,
                    // not a failed report (the file was already verified
                    // saved before this is called).
                }
            }
            else
            {
                log("Could not confirm the report preview window to close it safely - leaving it open rather than risk closing the wrong window.");
            }
        }

        _previewWindowElement = null;
        _previewWindowHandle = IntPtr.Zero;
    }
}
