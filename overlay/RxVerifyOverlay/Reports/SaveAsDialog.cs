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

        var ownedBySaveAs = saveAsHandle != IntPtr.Zero && candidate.OwnerHandle == saveAsHandle;
        var confirmSaveAsTitle = (candidate.Title ?? string.Empty).Contains("Confirm Save", StringComparison.OrdinalIgnoreCase);
        return ownedBySaveAs || confirmSaveAsTitle;
    }

    /// <summary>
    /// Whether a Pioneer-owned dialog is really the "already exists, replace
    /// it?" prompt (answer Yes) rather than some other Yes/No question - e.g.
    /// "open the exported file now?" must NOT be answered Yes. True when the
    /// title is the standard "Confirm Save As", or any body text says
    /// replace / overwrite / already exists.
    /// </summary>
    public static bool IsReplacePrompt(string? title, IEnumerable<string> bodyTexts)
    {
        if ((title ?? string.Empty).Contains("Confirm Save", StringComparison.OrdinalIgnoreCase)) return true;

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
/// Log formatting for the "save-as" lines. A candidate that is not
/// Pioneer's is logged by LENGTH only; a Pioneer window's title text is
/// logged only when it reads like a file/confirm dialog (a Pioneer
/// screen title could carry something patient-specific - same rule as
/// MainWindowTitleRedaction).
/// </summary>
public static class SaveAsCandidateLog
{
    public const string Prefix = "save-as: ";

    private const int MaxLoggedTitleLength = 80;

    private static readonly string[] SafeTitleWords = { "Save", "Export", "Confirm", "Replace" };

    public static string TitleForLog(SaveAsCandidate candidate, int pioneerProcessId)
    {
        var title = candidate.Title ?? string.Empty;
        if (title.Length == 0) return "<untitled>";
        if (!SaveAsDialogMatcher.BelongsToPioneer(candidate, pioneerProcessId)) return "[other-app]";
        if (title.Length > MaxLoggedTitleLength) return "[redacted]";

        foreach (var word in SafeTitleWords)
        {
            if (title.Contains(word, StringComparison.OrdinalIgnoreCase)) return title;
        }

        return "[redacted]";
    }

    public static string Format(SaveAsCandidate candidate, int pioneerProcessId) =>
        $"{Prefix}  candidate handle=0x{candidate.Handle.ToInt64():X} pid={candidate.ProcessId} owner=0x{candidate.OwnerHandle.ToInt64():X} ownerPid={candidate.OwnerProcessId} "
        + $"class='{candidate.ClassName}' visible={(candidate.IsVisible ? "yes" : "no")} pioneer={(SaveAsDialogMatcher.BelongsToPioneer(candidate, pioneerProcessId) ? "yes" : "no")} "
        + $"title_len={(candidate.Title ?? string.Empty).Length} title='{TitleForLog(candidate, pioneerProcessId)}'";

    /// <summary>All candidate lines, Pioneer-related first (they are what the next round needs), capped so a crowded desktop cannot flood the log.</summary>
    public static IReadOnlyList<string> FormatAll(IReadOnlyList<SaveAsCandidate> candidates, int pioneerProcessId, int maxLines)
    {
        return candidates
            .OrderBy(c => SaveAsDialogMatcher.BelongsToPioneer(c, pioneerProcessId) ? 0 : 1)
            .Take(maxLines)
            .Select(c => Format(c, pioneerProcessId))
            .ToList();
    }
}
