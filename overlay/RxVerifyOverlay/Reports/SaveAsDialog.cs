using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace RxVerifyOverlay.Reports;

// W-T92 round 8 (Will, 2026-10-20, verbatim: "It successfully ran the
// report and made it to the save as screen. It just neeeded to write a
// filename and save it, then close the report."). Everything in this
// file is the PURE half of the Save As step - plain data in, plain
// decision out, no FlaUI/UIA/Win32 - so it can be unit tested on any OS
// (RxVerifyOverlay.Tests/Reports/SaveAsDialogTests.cs). The impure half
// (EnumWindows, UIA descendants, SetValue/WM_SETTEXT, clicking Save)
// lives in PioneerReportDriver.SaveAsAndVerify.
/// <summary>
/// One enumerated top-level window, built from raw Win32 EnumWindows (NOT
/// the FlaUI desktop-children list: owned dialogs are not reliably listed
/// there - the same gap round 6 found for the Report Parameters popup).
/// <paramref name="OwnerHandle"/>/<paramref name="OwnerProcessId"/> are the
/// GW_OWNER window and its process, so a dialog that lives in another
/// process but is OWNED by a Pioneer window still counts as Pioneer's.
/// </summary>
public readonly record struct SaveAsCandidate(
    string Title,
    string ClassName,
    int ProcessId,
    IntPtr Handle,
    IntPtr OwnerHandle,
    int OwnerProcessId,
    bool IsVisible);

/// <summary>How strongly a window looks like the Save As dialog. Lower is better.</summary>
public enum SaveAsMatchTier
{
    /// <summary>Not a candidate at all (invisible, or not Pioneer's by pid/owner, or no title/class evidence).</summary>
    None = 0,

    /// <summary>Standard dialog class (#32770) AND a title containing "Save" or "Export".</summary>
    Strong = 1,

    /// <summary>Standard dialog class (#32770) with any other title (localized, empty) - only usable once a File name edit is confirmed inside it.</summary>
    DialogClass = 2,

    /// <summary>A non-#32770 window whose title contains "Save" or "Export" - last resort, again only usable once a File name edit is confirmed.</summary>
    TitleOnly = 3
}

public static class SaveAsDialogMatcher
{
    /// <summary>The window class of every standard Windows dialog, including the common Save/Open dialogs and MessageBox.</summary>
    public const string DialogClassName = "#32770";

    public static bool TitleLooksLikeSaveDialog(string? title)
    {
        if (string.IsNullOrEmpty(title)) return false;
        return title.Contains("Save", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Export", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the window's own process OR its owner window's process is Pioneer's. The guard against other apps' dialogs: neither means "not ours", whatever the title says.</summary>
    public static bool BelongsToPioneer(SaveAsCandidate candidate, int pioneerProcessId)
    {
        if (pioneerProcessId == 0) return false;
        return candidate.ProcessId == pioneerProcessId || candidate.OwnerProcessId == pioneerProcessId;
    }

    public static bool IsDialogClass(string? className) =>
        string.Equals(className, DialogClassName, StringComparison.Ordinal);

    public static SaveAsMatchTier Classify(SaveAsCandidate candidate, int pioneerProcessId)
    {
        if (!candidate.IsVisible) return SaveAsMatchTier.None;
        if (candidate.Handle == IntPtr.Zero) return SaveAsMatchTier.None;
        if (!BelongsToPioneer(candidate, pioneerProcessId)) return SaveAsMatchTier.None;

        var titleMatches = TitleLooksLikeSaveDialog(candidate.Title);
        var dialogClass = IsDialogClass(candidate.ClassName);

        if (dialogClass && titleMatches) return SaveAsMatchTier.Strong;
        if (dialogClass) return SaveAsMatchTier.DialogClass;
        if (titleMatches) return SaveAsMatchTier.TitleOnly;
        return SaveAsMatchTier.None;
    }

    /// <summary>
    /// Pioneer-owned candidates worth probing for a File name box, best
    /// first: tier (Strong before DialogClass before TitleOnly), then the OS
    /// foreground window, then enumeration (z-order) order. Never includes
    /// anything Classify says None for (in particular another application's
    /// "Save As"), nor any handle in <paramref name="excludedHandles"/> -
    /// the caller passes Pioneer's main window and the report preview, which
    /// must never be mistaken for the dialog. Every returned candidate
    /// still has to prove itself by containing an identified File name box
    /// (SaveAsFileNameFieldMatcher.MinimumAcceptedScore) before anything is
    /// typed into it.
    /// </summary>
    public static IReadOnlyList<SaveAsCandidate> Rank(
        IReadOnlyList<SaveAsCandidate> candidates, int pioneerProcessId, IntPtr foregroundHandle,
        IReadOnlyCollection<IntPtr>? excludedHandles = null)
    {
        return candidates
            .Where(c => excludedHandles is null || !excludedHandles.Contains(c.Handle))
            .Select((candidate, index) => (candidate, index, tier: Classify(candidate, pioneerProcessId)))
            .Where(x => x.tier != SaveAsMatchTier.None)
            .OrderBy(x => (int)x.tier)
            .ThenBy(x => x.candidate.Handle == foregroundHandle && foregroundHandle != IntPtr.Zero ? 0 : 1)
            .ThenBy(x => x.index)
            .Select(x => x.candidate)
            .ToList();
    }

    /// <summary>
    /// A Pioneer-owned standard dialog other than the Save As dialog
    /// itself that is tied to it: OWNED by the Save As dialog, or titled
    /// "Confirm Save As" (class #32770). An unrelated Pioneer prompt that
    /// merely says "already exists" is never a candidate. The caller still
    /// requires a "Yes" button and IsReplacePrompt before pressing anything.
    /// </summary>
    public static bool IsPossibleOverwritePrompt(SaveAsCandidate candidate, int pioneerProcessId, IntPtr saveAsHandle)
    {
        if (!candidate.IsVisible || candidate.Handle == IntPtr.Zero) return false;
        if (candidate.Handle == saveAsHandle) return false;
        if (!BelongsToPioneer(candidate, pioneerProcessId)) return false;
        if (!IsDialogClass(candidate.ClassName)) return false;

        return IsTiedToSaveAs(candidate, saveAsHandle);
    }

    /// <summary>Owned by the Save As dialog, or titled exactly "Confirm Save As" (a looser "Confirm Save..." match would also hit e.g. "Confirm Save Changes").</summary>
    public static bool IsTiedToSaveAs(SaveAsCandidate candidate, IntPtr saveAsHandle)
    {
        var ownedBySaveAs = saveAsHandle != IntPtr.Zero && candidate.OwnerHandle == saveAsHandle;
        var exactTitle = string.Equals((candidate.Title ?? string.Empty).Trim(), "Confirm Save As", StringComparison.OrdinalIgnoreCase);
        return ownedBySaveAs || exactTitle;
    }

    /// <summary>
    /// Whether a candidate may be answered Yes: tied to the Save As dialog
    /// (IsTiedToSaveAs) AND its body text says replace / overwrite / already
    /// exists. Title alone is never enough. The caller additionally requires
    /// a Yes button.
    /// </summary>
    public static bool IsReplacePrompt(SaveAsCandidate candidate, IntPtr saveAsHandle, IEnumerable<string> bodyTexts) =>
        IsTiedToSaveAs(candidate, saveAsHandle) && BodySaysReplace(bodyTexts);

    /// <summary>
    /// Whether a window found by Rank may be treated as the Save As dialog
    /// once its contents are known: it must contain an identified File name
    /// box, and unless it is a Strong (#32770 + Save/Export title) match it
    /// must also contain a Save button - so an unrelated Pioneer dialog that
    /// merely has an Edit with id 1001 is never accepted.
    /// </summary>
    public static bool IsAcceptableDialog(SaveAsMatchTier tier, bool hasFileNameBox, bool hasSaveButton)
    {
        if (tier == SaveAsMatchTier.None || !hasFileNameBox) return false;
        return tier == SaveAsMatchTier.Strong || hasSaveButton;
    }

    /// <summary>True when any body text says replace / overwrite / already exists.</summary>
    public static bool BodySaysReplace(IEnumerable<string> bodyTexts)
    {
        foreach (var text in bodyTexts)
        {
            if (string.IsNullOrEmpty(text)) continue;
            if (text.Contains("replace", StringComparison.OrdinalIgnoreCase)
                || text.Contains("overwrite", StringComparison.OrdinalIgnoreCase)
                || text.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// One control inside a candidate dialog, from Win32 EnumChildWindows
/// (which lists EVERY descendant, so an Edit inside a ComboBoxEx32 / ComboBox
/// shows up with that combo as <paramref name="ParentHandle"/>).
/// <paramref name="Text"/> is only used to recognise the Save button; it is
/// never logged. <paramref name="ControlId"/> is GetDlgCtrlID.
/// </summary>
public readonly record struct SaveAsChild(IntPtr Handle, IntPtr ParentHandle, string ClassName, int ControlId, string Text);

/// <summary>Why a window is NOT a safe typing target. A window is accepted only when this is None.</summary>
[Flags]
public enum SaveAsStrictFailure
{
    None = 0,
    NotVisible = 1 << 0,
    NotPioneer = 1 << 1,
    Excluded = 1 << 2,
    ConfirmPrompt = 1 << 3,
    NotNew = 1 << 4,
    NotForeground = 1 << 5,
    NotDialogClass = 1 << 6,
    NoEditChild = 1 << 7,
    NoSaveButton = 1 << 8,

    /// <summary>Neither a Save/Export title nor a file-dialog shell child (DUIViewWndClassName / SHELLDLL_DefView): an input form with an Edit and an OK button.</summary>
    NoSaveEvidence = 1 << 9
}

/// <summary>
/// The ONLY test that decides a window may receive the file name and the
/// Save click. Everything is Win32 evidence (no UI Automation):
/// (a) visible top-level, Pioneer pid or Pioneer owner;
/// (b) not Pioneer's main window, the report preview or "Confirm Save As";
/// (c) NEW since the export click AND the foreground window (both);
/// (d) window class #32770;
/// (e) children include a file-name Edit (id 1001 / 1152, or any Edit inside a
///     ComboBox / ComboBoxEx32) AND a Button whose text is "&amp;Save" / "Save"
///     (id 1 alone is NOT enough: an input dialog's OK button is id 1);
/// (f) the title contains Save or Export, OR a file-dialog shell child exists
///     (DUIViewWndClassName or SHELLDLL_DefView). Title and button text are
///     read only for this decision and are never logged.
/// A new foreground Pioneer window that fails any of these (a data-entry
/// form, a message box, an alert) is never typed into.
/// </summary>
public static class SaveAsStrictMatcher
{
    public static SaveAsStrictFailure CheckWindow(
        SaveAsCandidate c, int pioneerProcessId, IntPtr foregroundHandle,
        IReadOnlyCollection<IntPtr>? excludedHandles, IReadOnlyCollection<IntPtr>? baselineHandles)
    {
        var failure = SaveAsStrictFailure.None;
        if (!c.IsVisible || c.Handle == IntPtr.Zero) failure |= SaveAsStrictFailure.NotVisible;
        if (!SaveAsDialogMatcher.BelongsToPioneer(c, pioneerProcessId)) failure |= SaveAsStrictFailure.NotPioneer;
        if (excludedHandles is not null && excludedHandles.Contains(c.Handle)) failure |= SaveAsStrictFailure.Excluded;
        if (string.Equals((c.Title ?? string.Empty).Trim(), "Confirm Save As", StringComparison.OrdinalIgnoreCase)) failure |= SaveAsStrictFailure.ConfirmPrompt;
        if (baselineHandles is null || baselineHandles.Contains(c.Handle)) failure |= SaveAsStrictFailure.NotNew;
        if (foregroundHandle == IntPtr.Zero || c.Handle != foregroundHandle) failure |= SaveAsStrictFailure.NotForeground;
        if (!SaveAsDialogMatcher.IsDialogClass(c.ClassName)) failure |= SaveAsStrictFailure.NotDialogClass;
        return failure;
    }

    public static bool IsEditClass(string? className) => string.Equals(className, "Edit", StringComparison.OrdinalIgnoreCase);

    public static bool IsComboClass(string? className) =>
        string.Equals(className, "ComboBox", StringComparison.OrdinalIgnoreCase)
        || string.Equals(className, "ComboBoxEx32", StringComparison.OrdinalIgnoreCase);

    /// <summary>The file-name Edit: id 1001 (Vista dialog) first, then id 1152 (legacy dialog), then any Edit whose parent is a ComboBox / ComboBoxEx32. Null when none.</summary>
    public static SaveAsChild? FindFileNameEdit(IReadOnlyList<SaveAsChild> children)
    {
        var edits = children.Where(ch => IsEditClass(ch.ClassName)).ToList();
        if (edits.Count == 0) return null;

        foreach (var wanted in new[] { 1001, 1152 })
        {
            var byId = edits.Where(e => e.ControlId == wanted).Select(e => (SaveAsChild?)e).FirstOrDefault();
            if (byId is not null) return byId;
        }

        foreach (var edit in edits)
        {
            var parent = children.Where(ch => ch.Handle == edit.ParentHandle && edit.ParentHandle != IntPtr.Zero).Select(ch => (SaveAsChild?)ch).FirstOrDefault();
            if (parent is not null && IsComboClass(parent.Value.ClassName)) return edit;
        }

        return null;
    }

    /// <summary>The Save button: class Button whose text is "Save" / "&amp;Save". Control id 1 alone is not enough (every dialog's OK button is id 1).</summary>
    public static SaveAsChild? FindSaveButton(IReadOnlyList<SaveAsChild> children)
    {
        foreach (var ch in children)
        {
            if (!string.Equals(ch.ClassName, "Button", StringComparison.OrdinalIgnoreCase)) continue;
            var text = (ch.Text ?? string.Empty).Replace("&", string.Empty).Trim();
            if (string.Equals(text, "Save", StringComparison.OrdinalIgnoreCase)) return ch;
        }

        return null;
    }

    public static bool HasFileDialogShellChild(IReadOnlyList<SaveAsChild> children) =>
        children.Any(ch => string.Equals(ch.ClassName, "DUIViewWndClassName", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(ch.ClassName, "SHELLDLL_DefView", StringComparison.OrdinalIgnoreCase));

    /// <param name="windowTitle">The dialog's title, used only to look for Save / Export; never logged.</param>
    public static SaveAsStrictFailure CheckChildren(IReadOnlyList<SaveAsChild> children, string? windowTitle)
    {
        var failure = SaveAsStrictFailure.None;
        if (FindFileNameEdit(children) is null) failure |= SaveAsStrictFailure.NoEditChild;
        if (FindSaveButton(children) is null) failure |= SaveAsStrictFailure.NoSaveButton;
        if (!SaveAsDialogMatcher.TitleLooksLikeSaveDialog(windowTitle) && !HasFileDialogShellChild(children)) failure |= SaveAsStrictFailure.NoSaveEvidence;
        return failure;
    }

    /// <summary>"Rules a-d minus the foreground test" are all satisfied: worth looking inside (and worth bringing to the foreground).</summary>
    public static bool IsWorthInspecting(SaveAsStrictFailure windowCheck) =>
        (windowCheck & ~SaveAsStrictFailure.NotForeground) == SaveAsStrictFailure.None;

    /// <summary>Pioneer-related windows the inventory should describe: visible, Pioneer pid/owner, not excluded, and new since the click or foreground.</summary>
    public static IReadOnlyList<(SaveAsCandidate Candidate, bool IsNew, bool IsForeground)> InventoryWindows(
        IReadOnlyList<SaveAsCandidate> all, int pioneerProcessId, IntPtr foregroundHandle,
        IReadOnlyCollection<IntPtr>? excludedHandles, IReadOnlyCollection<IntPtr>? baselineHandles)
    {
        return all
            .Where(c => c.IsVisible && c.Handle != IntPtr.Zero
                        && SaveAsDialogMatcher.BelongsToPioneer(c, pioneerProcessId)
                        && (excludedHandles is null || !excludedHandles.Contains(c.Handle)))
            .Select(c => (Candidate: c,
                          IsNew: baselineHandles is not null && !baselineHandles.Contains(c.Handle),
                          IsForeground: foregroundHandle != IntPtr.Zero && c.Handle == foregroundHandle))
            .Where(x => x.IsNew || x.IsForeground)
            .ToList();
    }
}

/// <summary>
/// One descendant of the Save As dialog, reduced to the four strings that
/// identify the File name control. ControlType is the FlaUI name
/// ("Edit", "ComboBox", ...).
/// </summary>
public readonly record struct SaveAsFieldCandidate(string ControlType, string AutomationId, string Name, string ClassName);

/// <summary>
/// Which descendant of the Save As dialog is the File name box. The old
/// code took the FIRST Edit in document order - in the Vista-style
/// dialog the toolbar's search box can come first, so the path could be
/// typed into the wrong control. Common-dialog facts used here: the
/// File name combo is AutomationId "1148" (ComboBox) wrapping an Edit
/// with AutomationId "1001"; the Edit is also named "File name:".
/// </summary>
public static class SaveAsFileNameFieldMatcher
{
    public static int Score(SaveAsFieldCandidate c)
    {
        var isEdit = string.Equals(c.ControlType, "Edit", StringComparison.OrdinalIgnoreCase);
        var isCombo = string.Equals(c.ControlType, "ComboBox", StringComparison.OrdinalIgnoreCase);
        if (!isEdit && !isCombo) return 0;

        var name = c.Name ?? string.Empty;
        var id = c.AutomationId ?? string.Empty;
        var cls = c.ClassName ?? string.Empty;

        if (name.Contains("Search", StringComparison.OrdinalIgnoreCase)
            || id.Contains("Search", StringComparison.OrdinalIgnoreCase)
            || cls.Contains("Search", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Address", StringComparison.OrdinalIgnoreCase)
            || cls.Contains("Address", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var nameIsFileName = name.Contains("File name", StringComparison.OrdinalIgnoreCase);

        if (isEdit && id == "1001") return 100;
        if (isEdit && nameIsFileName) return 90;
        if (isCombo && id == "1148") return 80;
        if (isCombo && nameIsFileName) return 70;

        // No "last resort" for an unidentified Edit: typing a path into a
        // control we cannot positively identify as the File name box is
        // exactly how a non-Save-As Pioneer window could be written into.
        return 0;
    }

    /// <summary>A control must score at least this (Edit 1001 / "File name" Edit / File name combo) to be treated as the File name box.</summary>
    public const int MinimumAcceptedScore = 70;

    /// <summary>Index of the best candidate, or null when nothing scores above zero. Ties keep the earlier (document-order) candidate.</summary>
    public static int? ChooseIndex(IReadOnlyList<SaveAsFieldCandidate> candidates)
    {
        int? best = null;
        var bestScore = 0;
        for (var i = 0; i < candidates.Count; i++)
        {
            var score = Score(candidates[i]);
            if (score >= MinimumAcceptedScore && score > bestScore)
            {
                bestScore = score;
                best = i;
            }
        }

        return best;
    }

    /// <summary>The Save button: AutomationId "1" (IDOK) or the name "Save" (also "&amp;Save"). Case-insensitive on the name.</summary>
    public static bool IsSaveButton(string controlType, string automationId, string name)
    {
        if (!string.Equals(controlType, "Button", StringComparison.OrdinalIgnoreCase)) return false;
        if (automationId == "1") return true;
        var trimmed = (name ?? string.Empty).Replace("&", string.Empty).Trim();
        return string.Equals(trimmed, "Save", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Filename/path composition for the Save As File name box.</summary>
public static class SaveAsPathBuilder
{
    /// <summary>Replaces characters Windows forbids in a file name with '-' and trims. An all-invalid/empty name becomes "Report".</summary>
    public static string SanitizeFileNameComponent(string? name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = (name ?? string.Empty).Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray();
        var cleaned = new string(chars).Trim().TrimEnd('.');
        return cleaned.Length == 0 ? "Report" : cleaned;
    }

    /// <summary>Will's convention: "&lt;MM-dd-yy&gt;_&lt;Report Name&gt;.&lt;ext&gt;" (the date is the END/as-of date). Extension may be given with or without the dot.</summary>
    public static string BuildFileName(DateTime stampDate, string reportSaveName, string extension)
    {
        var ext = (extension ?? string.Empty).TrimStart('.');
        var datePart = stampDate.ToString("MM-dd-yy", CultureInfo.InvariantCulture);
        return ext.Length == 0
            ? $"{datePart}_{SanitizeFileNameComponent(reportSaveName)}"
            : $"{datePart}_{SanitizeFileNameComponent(reportSaveName)}.{ext}";
    }

    /// <summary>True when a File name box read-back equals the path we set (case-insensitive, surrounding whitespace ignored).</summary>
    public static bool TextMatches(string? readBack, string expected) =>
        string.Equals((readBack ?? string.Empty).Trim(), (expected ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Output folder + file name, as one absolute path (the exact string typed into the File name box).</summary>
    public static string BuildFullPath(string outputFolder, DateTime stampDate, string reportSaveName, string extension) =>
        Path.GetFullPath(Path.Combine(outputFolder, BuildFileName(stampDate, reportSaveName, extension)));

    /// <summary>
    /// Normalizes the path the driver was handed. Returns false (with a
    /// reason) for an empty or non-rooted path - a relative path typed
    /// into a Save As box would save relative to wherever the dialog
    /// happens to be, which is exactly the wrong place.
    /// </summary>
    public static bool TryNormalize(string? path, out string normalized, out string? problem)
    {
        normalized = string.Empty;
        problem = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            problem = "output path is empty";
            return false;
        }

        if (!Path.IsPathRooted(path))
        {
            problem = "output path is not absolute";
            return false;
        }

        try
        {
            normalized = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            problem = $"output path is invalid ({ex.GetType().Name})";
            return false;
        }

        if (string.IsNullOrEmpty(Path.GetFileName(normalized)))
        {
            problem = "output path has no file name";
            return false;
        }

        return true;
    }
}

public enum SaveCompletionState
{
    /// <summary>Save As dialog still showing.</summary>
    WaitingForDialogToClose,

    /// <summary>Dialog gone but the file is not on disk yet.</summary>
    WaitingForFile,

    /// <summary>File exists but is still zero bytes (export still being written).</summary>
    WaitingForContent,

    /// <summary>Dialog closed AND file on disk with size greater than zero.</summary>
    Done
}

public static class SaveCompletion
{
    public static SaveCompletionState Evaluate(bool dialogStillOpen, bool fileExists, long fileLength)
    {
        if (dialogStillOpen) return SaveCompletionState.WaitingForDialogToClose;
        if (!fileExists) return SaveCompletionState.WaitingForFile;
        if (fileLength <= 0) return SaveCompletionState.WaitingForContent;
        return SaveCompletionState.Done;
    }

    public static string DescribeTimeout(SaveCompletionState state) => state switch
    {
        SaveCompletionState.WaitingForDialogToClose => "the Save As dialog never closed",
        SaveCompletionState.WaitingForFile => "the dialog closed but the file never appeared on disk",
        SaveCompletionState.WaitingForContent => "the file appeared but stayed empty (0 bytes)",
        _ => "completed"
    };
}

/// <summary>
/// Log formatting for the "save-as" lines. Window TITLES are never
/// written (a Pioneer screen title can carry something patient-specific):
/// a candidate line carries handle/pid/owner/class, the title LENGTH and
/// whether the title reads like Save/Export - nothing else.
/// </summary>
public static class SaveAsCandidateLog
{
    public const string Prefix = "save-as: ";

    public static string Format(SaveAsCandidate candidate, int pioneerProcessId) =>
        $"{Prefix}  candidate handle=0x{candidate.Handle.ToInt64():X} pid={candidate.ProcessId} owner=0x{candidate.OwnerHandle.ToInt64():X} ownerPid={candidate.OwnerProcessId} "
        + $"class='{candidate.ClassName}' visible={(candidate.IsVisible ? "yes" : "no")} pioneer={(SaveAsDialogMatcher.BelongsToPioneer(candidate, pioneerProcessId) ? "yes" : "no")} "
        + $"title_len={(candidate.Title ?? string.Empty).Length} save_like_title={(SaveAsDialogMatcher.TitleLooksLikeSaveDialog(candidate.Title) ? "yes" : "no")}";

    /// <summary>All candidate lines, Pioneer-related first (they are what the next round needs), capped so a crowded desktop cannot flood the log.</summary>
    public static IReadOnlyList<string> FormatAll(IReadOnlyList<SaveAsCandidate> candidates, int pioneerProcessId, int maxLines)
    {
        return candidates
            .OrderBy(c => SaveAsDialogMatcher.BelongsToPioneer(c, pioneerProcessId) ? 0 : 1)
            .Take(maxLines)
            .Select(c => Format(c, pioneerProcessId))
            .ToList();
    }

    /// <summary>One new/foreground Pioneer window for the "not recognised" inventory: class, pid, ownerPid, flags and size - no title text.</summary>
    public static string FormatInventoryWindow(SaveAsCandidate c, bool isNew, bool isForeground, int width, int height, SaveAsStrictFailure failure) =>
        $"{Prefix}  window handle=0x{c.Handle.ToInt64():X} class='{c.ClassName}' pid={c.ProcessId} ownerPid={c.OwnerProcessId} new={(isNew ? "yes" : "no")} foreground={(isForeground ? "yes" : "no")} size={width}x{height} title_len={(c.Title ?? string.Empty).Length} rejected_because={DescribeFailure(failure)}";

    /// <summary>One child control: class and control id only - no text.</summary>
    public static string FormatInventoryChild(SaveAsChild child) =>
        $"{Prefix}    control class='{child.ClassName}' id={child.ControlId}";

    public static string DescribeFailure(SaveAsStrictFailure failure)
    {
        if (failure == SaveAsStrictFailure.None) return "nothing";
        var names = new List<string>();
        foreach (SaveAsStrictFailure flag in Enum.GetValues(typeof(SaveAsStrictFailure)))
        {
            if (flag != SaveAsStrictFailure.None && failure.HasFlag(flag)) names.Add(flag.ToString());
        }

        return string.Join("+", names);
    }

    /// <summary>The periodic "still waiting" line: how many windows are new since the export click and how many of those are Pioneer's, plus the foreground window's class/pid - no titles.</summary>
    public static string FormatWaitingSummary(
        IReadOnlyList<SaveAsCandidate> all, IReadOnlyCollection<IntPtr>? baselineHandles, int pioneerProcessId, IntPtr foregroundHandle, double elapsedSeconds)
    {
        var visible = all.Where(c => c.IsVisible && c.Handle != IntPtr.Zero).ToList();
        var newOnes = baselineHandles is null ? new List<SaveAsCandidate>() : visible.Where(c => !baselineHandles.Contains(c.Handle)).ToList();
        var newPioneer = newOnes.Count(c => SaveAsDialogMatcher.BelongsToPioneer(c, pioneerProcessId));
        var fg = visible.FirstOrDefault(c => c.Handle == foregroundHandle);
        var fgText = fg.Handle == IntPtr.Zero
            ? "foreground=unknown"
            : $"foreground class='{fg.ClassName}' pid={fg.ProcessId} ownerPid={fg.OwnerProcessId} pioneer={(SaveAsDialogMatcher.BelongsToPioneer(fg, pioneerProcessId) ? "yes" : "no")}";
        return $"{Prefix}still waiting for the dialog ({elapsedSeconds:0}s): {visible.Count} visible top-level windows, {newOnes.Count} new since the export click ({newPioneer} Pioneer-related); {fgText}.";
    }
}
