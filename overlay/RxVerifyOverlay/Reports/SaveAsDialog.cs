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

/// <summary>Why DetectWithoutFileNameBox accepted a window. Lower is better.</summary>
public enum SaveAsDetectionReason
{
    /// <summary>Pioneer-owned #32770 with a Save/Export title, new since the export click or foreground.</summary>
    StandardSaveDialog = 1,

    /// <summary>Another process's #32770 with a Save/Export title that is both new and foreground (e.g. a shell-hosted dialog not owned by a Pioneer window).</summary>
    OtherOwnerStandardSaveDialog = 2,

    /// <summary>A Pioneer-owned window of any class that appeared after the export click and is the foreground window (the modal that grabbed focus).</summary>
    NewForegroundPioneerWindow = 3,

    /// <summary>A new Pioneer-owned window of any class whose title says Save/Export (WinForms/WPF-hosted Save As).</summary>
    NewPioneerSaveTitledWindow = 4,

    /// <summary>A new Pioneer-owned #32770 with any other title (localized Save As).</summary>
    NewPioneerStandardDialog = 5
}

/// <summary>A window accepted without UIA evidence, and why.</summary>
public readonly record struct SaveAsDetection(SaveAsCandidate Candidate, SaveAsDetectionReason Reason, bool IsNew, bool IsForeground);

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
    /// Fallback detection that needs NO UI Automation evidence. The UIA path
    /// (Rank + a File name box found by SaveAsFileNameFieldMatcher) only
    /// recognises the Windows common dialog; Will's Pioneer build showed a
    /// modal window that was open (the export Invoke blocked on it) yet was
    /// never accepted, so the run stalled 20s at "waiting for the Save As
    /// dialog". Here the window is chosen from what Win32 alone reports:
    /// it must be visible, not excluded (Pioneer main window / report
    /// preview), not the "Confirm Save As" prompt, and APPEARED AFTER the
    /// export click (<paramref name="baselineHandles"/> = every top-level
    /// handle visible just before the click) or be the foreground window.
    /// Ordered best first by <see cref="SaveAsDetectionReason"/>, then
    /// enumeration order. A window of another application is only ever
    /// accepted as a #32770 with a Save/Export title that is both new and
    /// the foreground window. The caller still brings the chosen window to
    /// the foreground and refuses to type unless it really is foreground.
    /// </summary>
    public static IReadOnlyList<SaveAsDetection> DetectWithoutFileNameBox(
        IReadOnlyList<SaveAsCandidate> candidates, int pioneerProcessId, IntPtr foregroundHandle,
        IReadOnlyCollection<IntPtr>? excludedHandles, IReadOnlyCollection<IntPtr>? baselineHandles)
    {
        var found = new List<(SaveAsDetection Detection, int Index)>();
        for (var index = 0; index < candidates.Count; index++)
        {
            var c = candidates[index];
            if (!c.IsVisible || c.Handle == IntPtr.Zero) continue;
            if (excludedHandles is not null && excludedHandles.Contains(c.Handle)) continue;
            if (string.Equals((c.Title ?? string.Empty).Trim(), "Confirm Save As", StringComparison.OrdinalIgnoreCase)) continue;

            var isNew = baselineHandles is not null && !baselineHandles.Contains(c.Handle);
            var isForeground = foregroundHandle != IntPtr.Zero && c.Handle == foregroundHandle;
            if (!isNew && !isForeground) continue;

            var pioneer = BelongsToPioneer(c, pioneerProcessId);
            var dialogClass = IsDialogClass(c.ClassName);
            var titleSave = TitleLooksLikeSaveDialog(c.Title);

            SaveAsDetectionReason reason;
            if (pioneer && dialogClass && titleSave) reason = SaveAsDetectionReason.StandardSaveDialog;
            else if (!pioneer && dialogClass && titleSave && isNew && isForeground) reason = SaveAsDetectionReason.OtherOwnerStandardSaveDialog;
            else if (!pioneer) continue;
            else if (isNew && isForeground) reason = SaveAsDetectionReason.NewForegroundPioneerWindow;
            else if (isNew && titleSave) reason = SaveAsDetectionReason.NewPioneerSaveTitledWindow;
            else if (isNew && dialogClass) reason = SaveAsDetectionReason.NewPioneerStandardDialog;
            else continue;

            found.Add((new SaveAsDetection(c, reason, isNew, isForeground), index));
        }

        return found
            .OrderBy(x => (int)x.Detection.Reason)
            .ThenBy(x => x.Detection.IsForeground ? 0 : 1)
            .ThenBy(x => x.Index)
            .Select(x => x.Detection)
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

public enum SaveAsKeyKind
{
    SelectAll,
    TypeText,
    AltS,
    AltF4
}

public readonly record struct SaveAsKeyStep(SaveAsKeyKind Kind, string? Text = null);

/// <summary>
/// Will's macro, as data (HQ 2026-11-10, verbatim: "type a filename
/// (Date_Filename) and then save it (Alt+s) and close the report (alt+f4)").
/// The driver replays these keys only while the target window is the OS
/// foreground window.
/// </summary>
public static class SaveAsKeyPlan
{
    /// <summary>Select any default name, type the full path, press Alt+S.</summary>
    public static IReadOnlyList<SaveAsKeyStep> BuildSave(string fullPath) => new[]
    {
        new SaveAsKeyStep(SaveAsKeyKind.SelectAll),
        new SaveAsKeyStep(SaveAsKeyKind.TypeText, fullPath),
        new SaveAsKeyStep(SaveAsKeyKind.AltS)
    };

    /// <summary>Close the report preview.</summary>
    public static IReadOnlyList<SaveAsKeyStep> BuildClosePreview() => new[]
    {
        new SaveAsKeyStep(SaveAsKeyKind.AltF4)
    };
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

    /// <summary>One line describing an accepted window and the reason - no title.</summary>
    public static string FormatDetection(SaveAsDetection detection, int pioneerProcessId) =>
        $"{Prefix}dialog accepted by window evidence: reason={detection.Reason} new_since_click={(detection.IsNew ? "yes" : "no")} foreground={(detection.IsForeground ? "yes" : "no")} "
        + $"handle=0x{detection.Candidate.Handle.ToInt64():X} pid={detection.Candidate.ProcessId} ownerPid={detection.Candidate.OwnerProcessId} class='{detection.Candidate.ClassName}'";

    /// <summary>All candidate lines, Pioneer-related first (they are what the next round needs), capped so a crowded desktop cannot flood the log.</summary>
    public static IReadOnlyList<string> FormatAll(IReadOnlyList<SaveAsCandidate> candidates, int pioneerProcessId, int maxLines)
    {
        return candidates
            .OrderBy(c => SaveAsDialogMatcher.BelongsToPioneer(c, pioneerProcessId) ? 0 : 1)
            .Take(maxLines)
            .Select(c => Format(c, pioneerProcessId))
            .ToList();
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
