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
        if (!IsDialogClass(candidate.ClassName)) return false;

        // Owned by the confirmed Save As dialog is the strongest tie there is, whichever process that dialog lives in;
        // otherwise the prompt must be Pioneer's (by pid or owner) AND titled "Confirm Save As".
        var ownedBySaveAs = saveAsHandle != IntPtr.Zero && candidate.OwnerHandle == saveAsHandle;
        if (!ownedBySaveAs && !BelongsToPioneer(candidate, pioneerProcessId)) return false;

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

/// <summary>Why a window is NOT accepted as the Save As dialog. A window is accepted only when this is None.</summary>
[Flags]
public enum SaveAsRejection
{
    None = 0,
    NotVisible = 1 << 0,

    /// <summary>The window was already on the desktop in the snapshot taken just before the export click (or no snapshot exists).</summary>
    NotNew = 1 << 1,
    NotDialogClass = 1 << 2,
    TitleNotAllowed = 1 << 3,
    NoEditChild = 1 << 4,
    NoSaveButton = 1 << 5
}

/// <summary>
/// The ONLY test that decides a window is the Save As dialog we may act on.
/// Win32 evidence only (no UI Automation), and deliberately NO process, owner
/// or foreground condition: the dialog is accepted whichever process owns it,
/// because a missed dialog leaves Pioneer stuck (three rounds of that) while a
/// wrong window is excluded by the four tests that matter:
/// (a) visible top-level window that was NOT in the snapshot taken just before
///     the export click;
/// (b) window class #32770;
/// (c) title (trimmed, case-insensitive) is exactly one of <see cref="AllowedTitles"/>;
/// (d) at any depth it contains an Edit control and a Button whose caption
///     with "&amp;" removed equals "Save".
/// Everything done afterwards is addressed to that dialog's own handles only.
/// </summary>
public static class SaveAsDialogDetector
{
    /// <summary>The only window titles that are ever accepted, and the only titles that may appear in the log.</summary>
    public static readonly IReadOnlyList<string> AllowedTitles = new[] { "Save As", "Save Print Output As", "Export", "Save" };

    public static bool IsAllowedTitle(string? title)
    {
        var trimmed = (title ?? string.Empty).Trim();
        if (trimmed.Length == 0) return false;
        foreach (var allowed in AllowedTitles)
        {
            if (string.Equals(trimmed, allowed, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>Window-level tests (a) to (c). <paramref name="baselineHandles"/> null (no snapshot) rejects everything as not new.</summary>
    public static SaveAsRejection CheckWindow(SaveAsCandidate c, IReadOnlyCollection<IntPtr>? baselineHandles)
    {
        var failure = SaveAsRejection.None;
        if (!c.IsVisible || c.Handle == IntPtr.Zero) failure |= SaveAsRejection.NotVisible;
        if (baselineHandles is null || baselineHandles.Contains(c.Handle)) failure |= SaveAsRejection.NotNew;
        if (!SaveAsDialogMatcher.IsDialogClass(c.ClassName)) failure |= SaveAsRejection.NotDialogClass;
        if (!IsAllowedTitle(c.Title)) failure |= SaveAsRejection.TitleNotAllowed;
        return failure;
    }

    /// <summary>Content tests (d): any Edit at any depth, and a Button captioned Save / &amp;Save.</summary>
    public static SaveAsRejection CheckChildren(IReadOnlyList<SaveAsChild> children)
    {
        var failure = SaveAsRejection.None;
        if (!children.Any(ch => IsEditClass(ch.ClassName))) failure |= SaveAsRejection.NoEditChild;
        if (FindSaveButton(children) is null) failure |= SaveAsRejection.NoSaveButton;
        return failure;
    }

    public static bool IsEditClass(string? className) => string.Equals(className, "Edit", StringComparison.OrdinalIgnoreCase);

    public static bool IsComboClass(string? className) =>
        string.Equals(className, "ComboBox", StringComparison.OrdinalIgnoreCase)
        || string.Equals(className, "ComboBoxEx32", StringComparison.OrdinalIgnoreCase);

    private static bool IsSearchOrAddressClass(string? className) =>
        !string.IsNullOrEmpty(className)
        && (className.Contains("Search", StringComparison.OrdinalIgnoreCase) || className.Contains("Address", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The file-name Edit: id 1001 (Vista dialog), then id 1152 (legacy dialog),
    /// then any Edit whose parent is a ComboBox / ComboBoxEx32 (the file-name
    /// combo nests its Edit, so a direct-child test would miss it). An Edit
    /// under a search or address control is never chosen. Null when none can
    /// be identified - the caller then must not write into an unidentified Edit.
    /// </summary>
    public static SaveAsChild? FindFileNameEdit(IReadOnlyList<SaveAsChild> children)
    {
        var edits = children.Where(ch => IsEditClass(ch.ClassName) && !HasSearchOrAddressAncestor(ch, children)).ToList();
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

    private static bool HasSearchOrAddressAncestor(SaveAsChild child, IReadOnlyList<SaveAsChild> all)
    {
        var current = child;
        for (var depth = 0; depth < 16; depth++)
        {
            if (current.ParentHandle == IntPtr.Zero) return false;
            SaveAsChild? parent = null;
            foreach (var ch in all)
            {
                if (ch.Handle == current.ParentHandle) { parent = ch; break; }
            }

            if (parent is null) return false;
            if (IsSearchOrAddressClass(parent.Value.ClassName)) return true;
            current = parent.Value;
        }

        return false;
    }

    /// <summary>The Save button: class Button whose caption with "&amp;" removed is "Save". Control id 1 alone is not enough (every dialog's OK button is id 1).</summary>
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

    /// <summary>The Cancel button (caption "Cancel"/"&amp;Cancel", else control id 2 = IDCANCEL). Null when neither exists.</summary>
    public static SaveAsChild? FindCancelButton(IReadOnlyList<SaveAsChild> children)
    {
        foreach (var ch in children)
        {
            if (!string.Equals(ch.ClassName, "Button", StringComparison.OrdinalIgnoreCase)) continue;
            var text = (ch.Text ?? string.Empty).Replace("&", string.Empty).Trim();
            if (string.Equals(text, "Cancel", StringComparison.OrdinalIgnoreCase)) return ch;
        }

        foreach (var ch in children)
        {
            if (string.Equals(ch.ClassName, "Button", StringComparison.OrdinalIgnoreCase) && ch.ControlId == 2) return ch;
        }

        return null;
    }

    /// <summary>Full verdict for one window: window tests plus (only when the window tests pass) the content tests.</summary>
    public static SaveAsRejection Evaluate(SaveAsCandidate c, IReadOnlyList<SaveAsChild> children, IReadOnlyCollection<IntPtr>? baselineHandles) =>
        CheckWindow(c, baselineHandles) | CheckChildren(children);

    /// <summary>The first accepted dialog among <paramref name="all"/> (z-order), or null. <paramref name="childrenOf"/> is only called for windows that pass the window tests.</summary>
    public static SaveAsDetection? Find(
        IReadOnlyList<SaveAsCandidate> all, IReadOnlyCollection<IntPtr>? baselineHandles, Func<IntPtr, IReadOnlyList<SaveAsChild>> childrenOf)
    {
        foreach (var candidate in all)
        {
            if (CheckWindow(candidate, baselineHandles) != SaveAsRejection.None) continue;

            var children = childrenOf(candidate.Handle);
            if (CheckChildren(children) != SaveAsRejection.None) continue;

            var save = FindSaveButton(children);
            if (save is null) continue;
            return new SaveAsDetection(candidate, FindFileNameEdit(children), save.Value, FindCancelButton(children));
        }

        return null;
    }
}

/// <summary>An accepted Save As dialog and its own control handles. <paramref name="FileNameEdit"/> is null when an Edit exists but none could be identified as the file-name box.</summary>
public sealed record SaveAsDetection(SaveAsCandidate Window, SaveAsChild? FileNameEdit, SaveAsChild SaveButton, SaveAsChild? CancelButton);

/// <summary>One row of the "window table" logged when the dialog is not found.</summary>
public sealed record SaveAsWindowRow(SaveAsCandidate Window, string ProcessName, IReadOnlyList<SaveAsChild>? Children);

/// <summary>
/// The table of every visible top-level window logged on a timeout. Window
/// titles are written ONLY when they are in <see cref="SaveAsDialogDetector.AllowedTitles"/>
/// (otherwise "&lt;hidden, N chars&gt;"); Button captions only when they are in
/// <see cref="ButtonCaptionAllowlist"/>. No other text from a screen is ever logged.
/// </summary>
public static class SaveAsWindowTable
{
    public const string Prefix = "save-as: ";

    public static readonly IReadOnlyList<string> ButtonCaptionAllowlist = new[]
    {
        "Save", "Cancel", "OK", "Open", "Yes", "No", "Close", "Retry", "Apply", "Export", "Replace", "Browse Folders", "Hide Folders", "Help"
    };

    public const int MaxRows = 150;
    public const int MaxDescendantItems = 40;

    public static string TitleText(string? title)
    {
        var t = title ?? string.Empty;
        return SaveAsDialogDetector.IsAllowedTitle(t) ? $"'{t.Trim()}'" : $"<hidden, {t.Length} chars>";
    }

    public static string CaptionText(string? caption)
    {
        var c = (caption ?? string.Empty);
        var plain = c.Replace("&", string.Empty).Trim();
        foreach (var allowed in ButtonCaptionAllowlist)
        {
            if (string.Equals(plain, allowed, StringComparison.OrdinalIgnoreCase)) return $"'{c.Trim()}'";
        }

        return $"<hidden, {c.Length} chars>";
    }

    public static string DescribeRejection(SaveAsRejection failure)
    {
        if (failure == SaveAsRejection.None) return "ACCEPTED";
        var names = new List<string>();
        foreach (SaveAsRejection flag in Enum.GetValues(typeof(SaveAsRejection)))
        {
            if (flag != SaveAsRejection.None && failure.HasFlag(flag)) names.Add(flag.ToString());
        }

        return string.Join("+", names);
    }

    public static string FormatRow(SaveAsWindowRow row, IReadOnlyCollection<IntPtr>? baseline)
    {
        var c = row.Window;
        var snapshot = baseline is null ? "unknown" : (baseline.Contains(c.Handle) ? "yes" : "no");
        var line = $"{Prefix}  hwnd=0x{c.Handle.ToInt64():X} class='{c.ClassName}' process='{row.ProcessName}' pid={c.ProcessId} ownerPid={c.OwnerProcessId} "
                   + $"title={TitleText(c.Title)} in_pre_click_snapshot={snapshot}";
        if (SaveAsDialogMatcher.IsDialogClass(c.ClassName) && row.Children is not null)
        {
            var verdict = SaveAsDialogDetector.Evaluate(c, row.Children, baseline);
            line += $" verdict={DescribeRejection(verdict)} descendants=[{DescribeDescendants(row.Children)}]";
        }

        return line;
    }

    /// <summary>Descendant classes (with the control id of Edits) and the captions of Buttons (allowlisted captions only).</summary>
    public static string DescribeDescendants(IReadOnlyList<SaveAsChild> children)
    {
        var items = new List<string>();
        foreach (var ch in children.Take(MaxDescendantItems))
        {
            if (string.Equals(ch.ClassName, "Button", StringComparison.OrdinalIgnoreCase)) items.Add($"Button id={ch.ControlId} caption={CaptionText(ch.Text)}");
            else if (SaveAsDialogDetector.IsEditClass(ch.ClassName)) items.Add($"Edit id={ch.ControlId}");
            else items.Add(ch.ClassName);
        }

        if (children.Count > MaxDescendantItems) items.Add($"... +{children.Count - MaxDescendantItems} more");
        return string.Join("; ", items);
    }

    public static IReadOnlyList<string> Format(IReadOnlyList<SaveAsWindowRow> rows, IReadOnlyCollection<IntPtr>? baseline, int pioneerProcessId)
    {
        var lines = new List<string>
        {
            $"{Prefix}window table: {rows.Count} visible top-level windows (pre-click snapshot {(baseline is null ? "unavailable" : baseline.Count + " windows")}; pioneer main pid={pioneerProcessId}); titles are hidden unless one of: {string.Join(", ", SaveAsDialogDetector.AllowedTitles.Select(t => "'" + t + "'"))}."
        };
        lines.AddRange(rows.Take(MaxRows).Select(r => FormatRow(r, baseline)));
        if (rows.Count > MaxRows) lines.Add($"{Prefix}  ... +{rows.Count - MaxRows} more windows not listed");
        return lines;
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
