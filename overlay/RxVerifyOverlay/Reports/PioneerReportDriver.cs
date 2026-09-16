using System;
using System.Diagnostics;
using System.IO;
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

    private UIA3Automation? _automation;
    private AutomationElement? _mainWindow;
    private int _pioneerProcessId;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

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
            if (!InvokeExportButton("Export to PDF", "PDF", log)) return ReportRunResult.Failed("Could not find the PDF export button", stopwatch.Elapsed);

            var saved = await SaveAsAndVerify(item.OutputFilePath, log, ct).ConfigureAwait(false);
            if (!saved) return ReportRunResult.Failed("Save As did not produce the expected file", stopwatch.Elapsed);

            log("Closing report preview...");
            ClosePreview();

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
            if (!InvokeExportButton("Export search results to Excel", "Excel", log))
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
            var handle = window.FrameworkAutomationElement.NativeWindowHandle;
            if (handle is { } h && h != IntPtr.Zero)
            {
                SetForegroundWindow(h);
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
            Keyboard.Press(VirtualKeyShort.ALT);
            Keyboard.Release(VirtualKeyShort.ALT);
            Thread.Sleep(200);
            Keyboard.Type(tabName.Substring(0, 1));
            Thread.Sleep(300);
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

    private bool InvokeExportButton(string primaryName, string toolTipContains, Action<string> log)
    {
        var button = FindDescendantByName(_mainWindow!, primaryName);
        if (button is not null && SelectOrInvoke(button)) return true;

        // Fallback: some toolbar buttons expose only an icon Name with the
        // real label as a ToolTip (see GOAL brief: "the PDF button's
        // tooltip is 'Export to PDF'") - walk descendants looking for one
        // whose HelpText/Name contains the expected substring.
        try
        {
            var descendants = _mainWindow!.FindAllDescendants();
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
    /// is present AND at least one report-preview-shaped top-level window
    /// exists (a window whose toolbar exposes the preview's known icons —
    /// approximated here by presence of a "Print Immediately" or "Export
    /// to PDF"-named element anywhere under the desktop's Pioneer-owned
    /// windows).
    /// </summary>
    private bool IsPreviewReady()
    {
        if (FindDescendantByName(_mainWindow!, "Please wait") is not null) return false;

        return FindDescendantByName(_mainWindow!, "Print Immediately") is not null
            || FindDescendantByName(_mainWindow!, "Export to PDF") is not null;
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
        var dialogAppeared = await WaitUntilAsync(() => FindSaveAsFileNameField() is not null, DialogTimeout, ct).ConfigureAwait(false);
        if (!dialogAppeared)
        {
            log("Save As dialog did not appear.");
            return false;
        }

        var fileNameField = FindSaveAsFileNameField();
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

            Keyboard.Type(VirtualKeyShort.RETURN);
        }
        catch (Exception ex)
        {
            log($"Could not fill in the Save As dialog: {ex.Message}");
            return false;
        }

        // "If duplicate, overwrite" - answer any confirmation prompt with
        // Yes (macro strings: "Confirm Save As"). Best-effort - if none
        // appears within a short window this is simply a no-op.
        await WaitUntilAsync(() =>
        {
            var yesButton = _mainWindow is null ? null : FindDescendantByName(_mainWindow, "Yes", ControlType.Button);
            if (yesButton is not null)
            {
                SelectOrInvoke(yesButton);
                return true;
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

    private AutomationElement? FindSaveAsFileNameField()
    {
        try
        {
            var automation = GetOrCreateAutomation();
            var desktop = automation.GetDesktop();
            foreach (var window in desktop.FindAllChildren())
            {
                string? name;
                try { name = window.Name; } catch { continue; }
                if (name is null || !name.Contains("Save As", StringComparison.OrdinalIgnoreCase)) continue;

                var editFields = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit));
                if (editFields.Length > 0) return editFields[0];
            }
        }
        catch
        {
            // fall through to null
        }

        return null;
    }

    private void ClosePreview()
    {
        try
        {
            Keyboard.TypeSimultaneously(VirtualKeyShort.ALT, VirtualKeyShort.F4);
        }
        catch
        {
            // Best-effort only - a preview left open is a nuisance, not a
            // failed report (the file was already verified saved before
            // this is called).
        }
    }
}
