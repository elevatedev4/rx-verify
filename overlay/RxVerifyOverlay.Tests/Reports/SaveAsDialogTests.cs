using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RxVerifyOverlay.Reports;
using Xunit;

namespace RxVerifyOverlay.Tests.Reports;

/// <summary>
/// W-T92 round 8: the pure half of the Save As step (Reports/SaveAsDialog.cs) -
/// which window counts as Pioneer's Save As dialog, which control is the
/// File name box, the filename/path builder, the completion decision and
/// the log formatting. No FlaUI/UIA/Win32 involved.
/// </summary>
public class SaveAsDialogTests
{
    private const int PioneerPid = 1234;
    private const int OtherPid = 9999;
    private const string Dlg = "#32770";

    private static SaveAsCandidate Window(
        string title, string className = Dlg, int pid = PioneerPid, long handle = 100,
        long owner = 0, int ownerPid = 0, bool visible = true) =>
        new(title, className, pid, new IntPtr(handle), new IntPtr(owner), ownerPid, visible);

    public class MatcherTests
    {
        [Theory]
        [InlineData("Save As")]
        [InlineData("Save")]
        [InlineData("Export")]
        [InlineData("Save File")]
        [InlineData("Export Document")]
        [InlineData("SAVE AS")]
        [InlineData("export to pdf")]
        public void TitleVariantsOnAPioneerDialogAreStrongMatches(string title)
        {
            Assert.Equal(SaveAsMatchTier.Strong, SaveAsDialogMatcher.Classify(Window(title), PioneerPid));
        }

        [Fact]
        public void LocalizedOrEmptyTitleOnADialogClassWindowIsStillACandidate()
        {
            Assert.Equal(SaveAsMatchTier.DialogClass, SaveAsDialogMatcher.Classify(Window("Guardar como"), PioneerPid));
            Assert.Equal(SaveAsMatchTier.DialogClass, SaveAsDialogMatcher.Classify(Window(""), PioneerPid));
        }

        [Fact]
        public void SaveTitleOnANonDialogClassIsTheWeakestTier()
        {
            Assert.Equal(SaveAsMatchTier.TitleOnly, SaveAsDialogMatcher.Classify(Window("Save As", className: "WindowsForms10.Window.8.app.0"), PioneerPid));
        }

        [Fact]
        public void NonDialogClassWithUnrelatedTitleIsNotACandidate()
        {
            Assert.Equal(SaveAsMatchTier.None, SaveAsDialogMatcher.Classify(Window("Run Financial Reports", className: "WindowsForms10.Window.8.app.0"), PioneerPid));
        }

        [Fact]
        public void RejectsAnotherApplicationsSaveAs()
        {
            Assert.Equal(SaveAsMatchTier.None, SaveAsDialogMatcher.Classify(Window("Save As", pid: OtherPid), PioneerPid));
            Assert.Equal(SaveAsMatchTier.None, SaveAsDialogMatcher.Classify(Window("Save As", pid: OtherPid, owner: 5, ownerPid: OtherPid), PioneerPid));
        }

        [Fact]
        public void AcceptsADialogInAnotherProcessThatIsOwnedByAPioneerWindow()
        {
            var dialog = Window("Save As", pid: OtherPid, owner: 5, ownerPid: PioneerPid);
            Assert.True(SaveAsDialogMatcher.BelongsToPioneer(dialog, PioneerPid));
            Assert.Equal(SaveAsMatchTier.Strong, SaveAsDialogMatcher.Classify(dialog, PioneerPid));
        }

        [Fact]
        public void RejectsInvisibleAndHandleZeroWindows()
        {
            Assert.Equal(SaveAsMatchTier.None, SaveAsDialogMatcher.Classify(Window("Save As", visible: false), PioneerPid));
            Assert.Equal(SaveAsMatchTier.None, SaveAsDialogMatcher.Classify(Window("Save As", handle: 0), PioneerPid));
        }

        [Fact]
        public void UnknownPioneerPidMatchesNothing()
        {
            Assert.Equal(SaveAsMatchTier.None, SaveAsDialogMatcher.Classify(Window("Save As", pid: 0), 0));
        }

        [Fact]
        public void RankPutsStrongerTiersFirstThenTheForegroundWindow()
        {
            var weak = Window("Guardar como", handle: 1);
            var strong = Window("Save As", handle: 2);
            var foregroundWeak = Window("Otro", handle: 3);
            var otherApp = Window("Save As", pid: OtherPid, handle: 4);

            var ranked = SaveAsDialogMatcher.Rank(new[] { weak, otherApp, strong, foregroundWeak }, PioneerPid, new IntPtr(3));

            // Strong (2) beats a foreground DialogClass window (3); within DialogClass the foreground one (3) beats (1).
            Assert.Equal(new long[] { 2, 3, 1 }, ranked.Select(c => c.Handle.ToInt64()).ToArray());
        }

        [Fact]
        public void RankNeverIncludesExcludedMainWindowOrPreviewHandles()
        {
            var mainWindow = Window("Save As", handle: 50);
            var preview = Window("Export", handle: 51);
            var dialog = Window("Save As", handle: 52);

            var ranked = SaveAsDialogMatcher.Rank(new[] { mainWindow, preview, dialog }, PioneerPid, new IntPtr(50),
                new[] { new IntPtr(50), new IntPtr(51) });

            Assert.Equal(new long[] { 52 }, ranked.Select(c => c.Handle.ToInt64()).ToArray());
        }

        [Fact]
        public void RankKeepsEnumerationOrderWithinATierAndNeverIncludesOtherApps()
        {
            var a = Window("Save As", handle: 10);
            var b = Window("Save As", handle: 11);
            var otherApp = Window("Save As", pid: OtherPid, handle: 12);

            var ranked = SaveAsDialogMatcher.Rank(new[] { a, otherApp, b }, PioneerPid, IntPtr.Zero);

            Assert.Equal(new long[] { 10, 11 }, ranked.Select(c => c.Handle.ToInt64()).ToArray());
        }

        [Fact]
        public void RankOfNothingIsEmpty()
        {
            Assert.Empty(SaveAsDialogMatcher.Rank(new List<SaveAsCandidate>(), PioneerPid, IntPtr.Zero));
        }

        [Fact]
        public void ReplacePromptCandidatesExcludeTheSaveAsDialogItselfAndOtherApps()
        {
            var saveAs = Window("Save As", handle: 100);
            var prompt = Window("Confirm Save As", handle: 200, owner: 100, ownerPid: PioneerPid);
            var otherAppPrompt = Window("Confirm Save As", pid: OtherPid, handle: 300);
            var notADialog = Window("Confirm Save As", className: "WindowsForms10.Window.8.app.0", handle: 400);

            Assert.False(SaveAsDialogMatcher.IsPossibleOverwritePrompt(saveAs, PioneerPid, saveAs.Handle));
            Assert.True(SaveAsDialogMatcher.IsPossibleOverwritePrompt(prompt, PioneerPid, saveAs.Handle));
            Assert.False(SaveAsDialogMatcher.IsPossibleOverwritePrompt(otherAppPrompt, PioneerPid, saveAs.Handle));
            Assert.False(SaveAsDialogMatcher.IsPossibleOverwritePrompt(notADialog, PioneerPid, saveAs.Handle));
        }

        [Fact]
        public void ReplacePromptMustBeOwnedByTheSaveAsDialogOrTitledConfirmSaveAs()
        {
            var saveAs = Window("Save As", handle: 100);
            var unrelatedPrompt = Window("Patient lookup", handle: 210, owner: 7, ownerPid: PioneerPid);
            var ownedElsewhere = Window("Question", handle: 211, owner: 999, ownerPid: PioneerPid);
            var ownedBySaveAs = Window("Question", handle: 212, owner: 100, ownerPid: PioneerPid);
            var confirmTitleNoOwner = Window("Confirm Save As", handle: 213);

            Assert.False(SaveAsDialogMatcher.IsPossibleOverwritePrompt(unrelatedPrompt, PioneerPid, saveAs.Handle));
            Assert.False(SaveAsDialogMatcher.IsPossibleOverwritePrompt(ownedElsewhere, PioneerPid, saveAs.Handle));
            Assert.True(SaveAsDialogMatcher.IsPossibleOverwritePrompt(ownedBySaveAs, PioneerPid, saveAs.Handle));
            Assert.True(SaveAsDialogMatcher.IsPossibleOverwritePrompt(confirmTitleNoOwner, PioneerPid, saveAs.Handle));
        }

        [Fact]
        public void ReplacePromptNeedsTheSaveAsTieAndReplaceText()
        {
            var saveAs = Window("Save As", handle: 100);
            var ownedPrompt = Window("Question", handle: 200, owner: 100, ownerPid: PioneerPid);
            var confirmPrompt = Window("Confirm Save As", handle: 201);
            var replaceText = new[] { "x.pdf already exists.", "Do you want to replace it?" };

            Assert.True(SaveAsDialogMatcher.IsReplacePrompt(ownedPrompt, saveAs.Handle, replaceText));
            Assert.True(SaveAsDialogMatcher.IsReplacePrompt(confirmPrompt, saveAs.Handle, new[] { "Overwrite existing file?" }));
        }

        [Fact]
        public void TitleAloneIsNeverEnoughForYes()
        {
            var saveAs = Window("Save As", handle: 100);
            var confirmPrompt = Window("Confirm Save As", handle: 201);

            Assert.False(SaveAsDialogMatcher.IsReplacePrompt(confirmPrompt, saveAs.Handle, new string[0]));
            Assert.False(SaveAsDialogMatcher.IsReplacePrompt(confirmPrompt, saveAs.Handle, new[] { "Save changes to this record?" }));
        }

        [Fact]
        public void ConfirmSaveChangesNotOwnedBySaveAsIsNeverAnswered()
        {
            var saveAs = Window("Save As", handle: 100);
            var confirmChanges = Window("Confirm Save Changes", handle: 202, owner: 7, ownerPid: PioneerPid);

            Assert.False(SaveAsDialogMatcher.IsPossibleOverwritePrompt(confirmChanges, PioneerPid, saveAs.Handle));
            Assert.False(SaveAsDialogMatcher.IsReplacePrompt(confirmChanges, saveAs.Handle, new[] { "Do you want to replace the existing note?" }));
        }

        [Fact]
        public void OtherYesNoQuestionsOwnedBySaveAsAreNotReplacePrompts()
        {
            var saveAs = Window("Save As", handle: 100);
            var ownedPrompt = Window("Export", handle: 203, owner: 100, ownerPid: PioneerPid);

            Assert.False(SaveAsDialogMatcher.IsReplacePrompt(ownedPrompt, saveAs.Handle, new[] { "Open the exported file now?" }));
        }

        [Theory]
        [InlineData(SaveAsMatchTier.Strong, true, false, true)]
        [InlineData(SaveAsMatchTier.Strong, false, true, false)]
        [InlineData(SaveAsMatchTier.DialogClass, true, true, true)]
        [InlineData(SaveAsMatchTier.DialogClass, true, false, false)]
        [InlineData(SaveAsMatchTier.TitleOnly, true, true, true)]
        [InlineData(SaveAsMatchTier.TitleOnly, true, false, false)]
        [InlineData(SaveAsMatchTier.None, true, true, false)]
        public void NonStrongTiersAlsoNeedASaveButton(SaveAsMatchTier tier, bool hasField, bool hasSave, bool expected)
        {
            Assert.Equal(expected, SaveAsDialogMatcher.IsAcceptableDialog(tier, hasField, hasSave));
        }
    }

    public class FileNameFieldTests
    {
        private static SaveAsFieldCandidate Edit(string id, string name = "", string cls = "Edit") => new("Edit", id, name, cls);

        private static SaveAsFieldCandidate Combo(string id, string name = "", string cls = "ComboBox") => new("ComboBox", id, name, cls);

        [Fact]
        public void PrefersEditWithAutomationId1001OverAnEarlierSearchBox()
        {
            var fields = new[]
            {
                Edit("SearchEditBox", "Search Downloads", "UniversalSearchBand"),
                Combo("1148", "File name:"),
                Edit("1001", "File name:")
            };

            Assert.Equal(2, SaveAsFileNameFieldMatcher.ChooseIndex(fields));
        }

        [Fact]
        public void FallsBackToEditNamedFileName()
        {
            var fields = new[] { Edit("", "Address"), Edit("", "File name:") };

            Assert.Equal(1, SaveAsFileNameFieldMatcher.ChooseIndex(fields));
        }

        [Fact]
        public void FallsBackToTheFileNameComboWhenNoInnerEditIsExposed()
        {
            var fields = new[] { Edit("", "Search Documents"), Combo("1148", "File name:") };

            Assert.Equal(1, SaveAsFileNameFieldMatcher.ChooseIndex(fields));
        }

        [Fact]
        public void NeverPicksSearchOrAddressControls()
        {
            var fields = new[]
            {
                Edit("1001", "Search Downloads"),
                Edit("", "Address", "Address Band Root"),
                new SaveAsFieldCandidate("Button", "1", "Save", "Button")
            };

            Assert.Null(SaveAsFileNameFieldMatcher.ChooseIndex(fields));
        }

        [Fact]
        public void AnUnidentifiedEditIsNeverTheFileNameBox()
        {
            Assert.Null(SaveAsFileNameFieldMatcher.ChooseIndex(new[] { Edit("", ""), Edit("txtNotes", "Notes"), Combo("", "Payer") }));
            Assert.Equal(0, SaveAsFileNameFieldMatcher.Score(Edit("", "")));
        }

        [Fact]
        public void OnlyIdentifiedFileNameControlsReachTheMinimumScore()
        {
            Assert.True(SaveAsFileNameFieldMatcher.Score(Edit("1001")) >= SaveAsFileNameFieldMatcher.MinimumAcceptedScore);
            Assert.True(SaveAsFileNameFieldMatcher.Score(Edit("", "File name:")) >= SaveAsFileNameFieldMatcher.MinimumAcceptedScore);
            Assert.True(SaveAsFileNameFieldMatcher.Score(Combo("1148")) >= SaveAsFileNameFieldMatcher.MinimumAcceptedScore);
            Assert.True(SaveAsFileNameFieldMatcher.Score(Combo("", "File name:")) >= SaveAsFileNameFieldMatcher.MinimumAcceptedScore);
        }

        [Fact]
        public void NoCandidatesMeansNoField()
        {
            Assert.Null(SaveAsFileNameFieldMatcher.ChooseIndex(new List<SaveAsFieldCandidate>()));
        }

        [Theory]
        [InlineData("Button", "1", "", true)]
        [InlineData("Button", "", "Save", true)]
        [InlineData("Button", "", "&Save", true)]
        [InlineData("Button", "", "save", true)]
        [InlineData("Button", "2", "Cancel", false)]
        [InlineData("Button", "", "Save as type:", false)]
        [InlineData("Edit", "1", "Save", false)]
        public void SaveButtonRecognition(string type, string id, string name, bool expected)
        {
            Assert.Equal(expected, SaveAsFileNameFieldMatcher.IsSaveButton(type, id, name));
        }
    }

    public class PathBuilderTests
    {
        [Fact]
        public void FileNameIsMmDdYyUnderscoreReportNameDotPdf()
        {
            Assert.Equal("09-30-26_Customer A-R Control Balance.pdf",
                SaveAsPathBuilder.BuildFileName(new DateTime(2026, 9, 30), "Customer A-R Control Balance", "pdf"));
        }

        [Fact]
        public void ExtensionMayBeGivenWithOrWithoutTheDot()
        {
            Assert.Equal("01-05-26_Sales Summary.xlsx", SaveAsPathBuilder.BuildFileName(new DateTime(2026, 1, 5), "Sales Summary", ".xlsx"));
            Assert.Equal("01-05-26_Sales Summary", SaveAsPathBuilder.BuildFileName(new DateTime(2026, 1, 5), "Sales Summary", ""));
        }

        [Fact]
        public void InvalidFileNameCharactersAreReplaced()
        {
            Assert.Equal("10-01-26_A-B-C.pdf", SaveAsPathBuilder.BuildFileName(new DateTime(2026, 10, 1), "A/B:C", "pdf"));
            Assert.Equal("Report", SaveAsPathBuilder.SanitizeFileNameComponent("   "));
            Assert.Equal("Report", SaveAsPathBuilder.SanitizeFileNameComponent(null));
        }

        [Fact]
        public void FullPathCombinesFolderAndFileName()
        {
            var folder = Path.Combine(Path.GetTempPath(), "Pioneer Reports", "2026-09");
            var expected = Path.GetFullPath(Path.Combine(folder, "09-30-26_Sales Summary.pdf"));

            Assert.Equal(expected, SaveAsPathBuilder.BuildFullPath(folder, new DateTime(2026, 9, 30), "Sales Summary", "pdf"));
        }

        [Fact]
        public void RunPlanFileNameStillMatchesTheBuilder()
        {
            var entry = ReportCatalog.All.First(e => e.OutputFormat == ReportOutputFormat.Pdf);
            var end = new DateTime(2026, 9, 30);

            Assert.Equal(
                SaveAsPathBuilder.BuildFileName(end, entry.SaveName, "pdf"),
                ReportRunPlan.BuildFileName(entry, new DateTime(2026, 9, 1), end));
        }

        [Fact]
        public void NormalizeAcceptsAnAbsolutePathWithAFileName()
        {
            var path = Path.Combine(Path.GetTempPath(), "x", "..", "09-30-26_Sales Summary.pdf");

            Assert.True(SaveAsPathBuilder.TryNormalize(path, out var normalized, out var problem));
            Assert.Null(problem);
            Assert.Equal(Path.GetFullPath(path), normalized);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("relative\\file.pdf")]
        public void NormalizeRejectsEmptyOrRelativePaths(string? path)
        {
            Assert.False(SaveAsPathBuilder.TryNormalize(path, out var normalized, out var problem));
            Assert.Equal(string.Empty, normalized);
            Assert.False(string.IsNullOrEmpty(problem));
        }

        [Fact]
        public void TextMatchIgnoresCaseAndSurroundingWhitespace()
        {
            Assert.True(SaveAsPathBuilder.TextMatches("  C:\\Out\\A.pdf ", "c:\\out\\a.PDF"));
            Assert.False(SaveAsPathBuilder.TextMatches("A.pdf", "C:\\Out\\A.pdf"));
            Assert.False(SaveAsPathBuilder.TextMatches(null, "A.pdf"));
        }
    }

    public class CompletionTests
    {
        [Theory]
        [InlineData(true, false, 0L, SaveCompletionState.WaitingForDialogToClose)]
        [InlineData(true, true, 500L, SaveCompletionState.WaitingForDialogToClose)]
        [InlineData(false, false, 0L, SaveCompletionState.WaitingForFile)]
        [InlineData(false, true, 0L, SaveCompletionState.WaitingForContent)]
        [InlineData(false, true, 1L, SaveCompletionState.Done)]
        [InlineData(false, true, 123456L, SaveCompletionState.Done)]
        public void DoneOnlyWhenDialogClosedAndFileHasContent(bool dialogOpen, bool exists, long length, SaveCompletionState expected)
        {
            Assert.Equal(expected, SaveCompletion.Evaluate(dialogOpen, exists, length));
        }

        [Fact]
        public void EveryWaitingStateHasADistinctTimeoutReason()
        {
            var reasons = new[]
            {
                SaveCompletion.DescribeTimeout(SaveCompletionState.WaitingForDialogToClose),
                SaveCompletion.DescribeTimeout(SaveCompletionState.WaitingForFile),
                SaveCompletion.DescribeTimeout(SaveCompletionState.WaitingForContent)
            };

            Assert.Equal(3, reasons.Distinct().Count());
        }
    }

    public class LogTests
    {
        [Fact]
        public void LineCarriesPrefixPidOwnerAndClassButNoTitleText()
        {
            var line = SaveAsCandidateLog.Format(Window("Save As", handle: 0x2A, owner: 0x10, ownerPid: PioneerPid), PioneerPid);

            Assert.StartsWith("save-as: ", line);
            Assert.Contains("handle=0x2A", line);
            Assert.Contains($"pid={PioneerPid}", line);
            Assert.Contains("owner=0x10", line);
            Assert.Contains("class='#32770'", line);
            Assert.Contains("pioneer=yes", line);
            Assert.Contains("title_len=7", line);
            Assert.Contains("save_like_title=yes", line);
            Assert.DoesNotContain("title='", line);
            Assert.DoesNotContain("Save As", line);
        }

        [Theory]
        [InlineData("Inbox - patient@example.test - Mail", OtherPid)]
        [InlineData("Rx 1234567 - Jane Doe", PioneerPid)]
        [InlineData("Open Rx 123 - DOE, JOHN", PioneerPid)]
        [InlineData("Print Label - DOE, JANE", PioneerPid)]
        public void NoTitleTextIsEverLogged(string title, int pid)
        {
            var line = SaveAsCandidateLog.Format(Window(title, className: "WindowsForms10.Window.8.app.0", pid: pid), PioneerPid);

            foreach (var word in new[] { "Inbox", "patient", "Jane", "Doe", "DOE", "JOHN", "JANE", "1234567", "Label", "Mail" })
            {
                Assert.DoesNotContain(word, line);
            }

            Assert.Contains($"title_len={title.Length}", line);
        }

        [Fact]
        public void FormatAllListsPioneerWindowsFirstAndHonoursTheCap()
        {
            var windows = new List<SaveAsCandidate>
            {
                Window("a", pid: OtherPid, handle: 1),
                Window("b", pid: OtherPid, handle: 2),
                Window("Save As", handle: 3)
            };

            var lines = SaveAsCandidateLog.FormatAll(windows, PioneerPid, 2);

            Assert.Equal(2, lines.Count);
            Assert.Contains("handle=0x3", lines[0]);
        }

        [Fact]
        public void InventoryLinesCarryClassPidFlagsAndSizeButNoTitleOrControlText()
        {
            var window = Window("Quick Entry - Jane Doe", className: "WindowsForms10.Window.8.app.0", handle: 0x30);
            var failure = SaveAsStrictFailure.NotDialogClass | SaveAsStrictFailure.NoEditChild;

            var windowLine = SaveAsCandidateLog.FormatInventoryWindow(window, true, true, 420, 310, failure);
            var childLine = SaveAsCandidateLog.FormatInventoryChild(new SaveAsChild(new IntPtr(5), new IntPtr(4), "Edit", 1001, "Jane Doe 1234567"));

            Assert.Contains("handle=0x30", windowLine);
            Assert.Contains($"pid={PioneerPid}", windowLine);
            Assert.Contains("new=yes", windowLine);
            Assert.Contains("foreground=yes", windowLine);
            Assert.Contains("size=420x310", windowLine);
            Assert.Contains("rejected_because=NotDialogClass+NoEditChild", windowLine);
            Assert.DoesNotContain("Jane", windowLine);
            Assert.DoesNotContain("Quick Entry", windowLine);
            Assert.Equal("save-as:     control class='Edit' id=1001", childLine);
            Assert.DoesNotContain("Jane", childLine);
            Assert.DoesNotContain("1234567", childLine);
        }

        [Fact]
        public void WaitingSummaryCountsNewWindowsAndNamesTheForegroundClassWithoutTitles()
        {
            var all = new List<SaveAsCandidate>
            {
                Window("Pioneer main - Jane Doe", className: "WindowsForms10.Window.8.app.0", handle: 1),
                Window("Jane Doe export", className: "HwndWrapper[App;;1]", handle: 2),
                Window("Notepad", className: "Notepad", pid: OtherPid, handle: 3)
            };
            var baseline = new HashSet<IntPtr> { new IntPtr(1), new IntPtr(3) };

            var line = SaveAsCandidateLog.FormatWaitingSummary(all, baseline, PioneerPid, new IntPtr(2), 5);

            Assert.Contains("3 visible top-level windows", line);
            Assert.Contains("1 new since the export click (1 Pioneer-related)", line);
            Assert.Contains("foreground class='HwndWrapper[App;;1]'", line);
            Assert.DoesNotContain("Jane", line);
            Assert.DoesNotContain("Notepad ", line);
        }

        [Fact]
        public void WaitingSummaryWithoutABaselineOrForegroundDoesNotThrow()
        {
            var line = SaveAsCandidateLog.FormatWaitingSummary(new List<SaveAsCandidate>(), null, PioneerPid, IntPtr.Zero, 5);

            Assert.Contains("0 new since the export click", line);
            Assert.Contains("foreground=unknown", line);
        }
    }

    public class StrictDetectionTests
    {
        private static HashSet<IntPtr> Baseline(params long[] handles) => new(handles.Select(h => new IntPtr(h)));

        private static SaveAsStrictFailure Check(SaveAsCandidate c, long foreground, IReadOnlyCollection<IntPtr>? baseline, params long[] excluded) =>
            SaveAsStrictMatcher.CheckWindow(c, PioneerPid, new IntPtr(foreground), excluded.Select(h => new IntPtr(h)).ToList(), baseline);

        private static SaveAsChild Child(long handle, string cls, int id, string text = "", long parent = 0) =>
            new(new IntPtr(handle), new IntPtr(parent), cls, id, text);

        // The Vista-style common dialog's Win32 children (control ids from the Windows SDK).
        private static List<SaveAsChild> RealSaveDialogChildren() => new()
        {
            Child(10, "DUIViewWndClassName", 0),
            Child(11, "ComboBoxEx32", 1148),
            Child(12, "ComboBox", 1148, parent: 11),
            Child(13, "Edit", 1001, parent: 12),
            Child(14, "Button", 1, "&Save"),
            Child(15, "Button", 2, "Cancel")
        };

        [Fact]
        public void ADialogThatIsNewForegroundPioneerClass32770WithEditAndSaveIsAccepted()
        {
            var dialog = Window("anything", handle: 5);

            Assert.Equal(SaveAsStrictFailure.None, Check(dialog, 5, Baseline(1, 2), 1, 2));
            Assert.Equal(SaveAsStrictFailure.None, SaveAsStrictMatcher.CheckChildren(RealSaveDialogChildren(), "Save As"));
        }

        // The review's blocker: a new foreground Pioneer window that is a form / alert / message box.
        [Fact]
        public void ANewForegroundClass32770WithNoEditOrSaveChildrenIsRejected()
        {
            var messageBox = Window("Notice", handle: 5);
            var children = new List<SaveAsChild> { Child(1, "Static", 65535, "Something happened"), Child(2, "Button", 2, "OK") };

            Assert.Equal(SaveAsStrictFailure.None, Check(messageBox, 5, Baseline(1)));
            Assert.Equal(
                SaveAsStrictFailure.NoEditChild | SaveAsStrictFailure.NoSaveButton | SaveAsStrictFailure.NoSaveEvidence,
                SaveAsStrictMatcher.CheckChildren(children, "Notice"));
        }

        [Fact]
        public void ADataEntryFormWithAnEditButNoSaveButtonIsRejected()
        {
            var children = new List<SaveAsChild> { Child(1, "Edit", 1001), Child(2, "Button", 2, "Cancel") };

            Assert.Equal(SaveAsStrictFailure.NoSaveButton, SaveAsStrictMatcher.CheckChildren(children, "Save As"));
        }

        [Fact]
        public void ASaveButtonWithNoFileNameEditIsRejected()
        {
            var children = new List<SaveAsChild> { Child(1, "Button", 1, "OK"), Child(2, "Edit", 5000) };

            Assert.True(SaveAsStrictMatcher.CheckChildren(children, "Save As").HasFlag(SaveAsStrictFailure.NoEditChild));
        }

        // The re-review's blocker: a Pioneer input dialog (#32770, default Edit id 1001, OK = id 1).
        [Fact]
        public void AnInputDialogWithEdit1001AndAnOkButtonIsRejected()
        {
            var inputDialog = Window("Enter Quantity", handle: 5);
            var children = new List<SaveAsChild>
            {
                Child(1, "Static", 65535, "Quantity:"),
                Child(2, "Edit", 1001),
                Child(3, "Button", 1, "OK"),
                Child(4, "Button", 2, "Cancel")
            };

            // It is new + foreground + Pioneer + #32770 ...
            Assert.Equal(SaveAsStrictFailure.None, Check(inputDialog, 5, Baseline(1)));
            // ... but it has no Save button and no Save/Export title or shell child.
            var failure = SaveAsStrictMatcher.CheckChildren(children, inputDialog.Title);
            Assert.NotEqual(SaveAsStrictFailure.None, failure);
            Assert.True(failure.HasFlag(SaveAsStrictFailure.NoSaveButton));
            Assert.True(failure.HasFlag(SaveAsStrictFailure.NoSaveEvidence));
        }

        [Fact]
        public void AnInputDialogWithASaveButtonButNoSaveTitleOrShellChildIsStillRejected()
        {
            var children = new List<SaveAsChild> { Child(1, "Edit", 1001), Child(2, "Button", 1, "&Save") };

            Assert.Equal(SaveAsStrictFailure.NoSaveEvidence, SaveAsStrictMatcher.CheckChildren(children, "Enter Quantity"));
        }

        [Theory]
        [InlineData("Save As")]
        [InlineData("Export to PDF")]
        [InlineData("SAVE")]
        public void ASaveOrExportTitleSuppliesTheSaveEvidence(string title)
        {
            var children = new List<SaveAsChild> { Child(1, "Edit", 1001), Child(2, "Button", 1, "&Save") };

            Assert.Equal(SaveAsStrictFailure.None, SaveAsStrictMatcher.CheckChildren(children, title));
        }

        [Theory]
        [InlineData("DUIViewWndClassName")]
        [InlineData("SHELLDLL_DefView")]
        public void AFileDialogShellChildSuppliesTheSaveEvidenceEvenWithALocalizedTitle(string shellClass)
        {
            var children = new List<SaveAsChild> { Child(1, shellClass, 0), Child(2, "Edit", 1001), Child(3, "Button", 1, "Save") };

            Assert.Equal(SaveAsStrictFailure.None, SaveAsStrictMatcher.CheckChildren(children, "Guardar como"));
        }

        [Fact]
        public void ANewForegroundPioneerWindowThatIsNotClass32770IsRejected()
        {
            var form = Window("Save", className: "WindowsForms10.Window.8.app.0", handle: 5);

            Assert.Equal(SaveAsStrictFailure.NotDialogClass, Check(form, 5, Baseline(1)));
        }

        [Fact]
        public void NewAndForegroundBothAreRequired()
        {
            var dialog = Window("Save As", handle: 5);

            Assert.Equal(SaveAsStrictFailure.NotForeground, Check(dialog, 1, Baseline(1)));          // new, not foreground
            Assert.Equal(SaveAsStrictFailure.NotNew, Check(dialog, 5, Baseline(5)));                 // foreground, was there before the click
            Assert.Equal(SaveAsStrictFailure.NotNew, Check(dialog, 5, baseline: null));              // no baseline at all
            Assert.Equal(SaveAsStrictFailure.NotForeground, Check(dialog, 0, Baseline(1)));          // no foreground window known
        }

        [Fact]
        public void OnlyTheForegroundFlagBlocksInspectionSoTheDialogCanBeBroughtForward()
        {
            Assert.True(SaveAsStrictMatcher.IsWorthInspecting(SaveAsStrictFailure.NotForeground));
            Assert.True(SaveAsStrictMatcher.IsWorthInspecting(SaveAsStrictFailure.None));
            Assert.False(SaveAsStrictMatcher.IsWorthInspecting(SaveAsStrictFailure.NotNew | SaveAsStrictFailure.NotForeground));
            Assert.False(SaveAsStrictMatcher.IsWorthInspecting(SaveAsStrictFailure.NotPioneer));
        }

        [Fact]
        public void AdialogInAnotherProcessOwnedByAPioneerWindowIsStillPioneers()
        {
            var dialog = Window("Save As", pid: OtherPid, owner: 2, ownerPid: PioneerPid, handle: 9);

            Assert.Equal(SaveAsStrictFailure.None, Check(dialog, 9, Baseline(1, 2), 1, 2));
        }

        [Fact]
        public void AnotherApplicationsSaveAsIsNeverAccepted()
        {
            var other = Window("Save As", pid: OtherPid, handle: 8);

            Assert.True(Check(other, 8, Baseline(1)).HasFlag(SaveAsStrictFailure.NotPioneer));
        }

        [Fact]
        public void MainWindowPreviewConfirmPromptInvisibleAndHandleZeroAreRejected()
        {
            Assert.True(Check(Window("Save As", handle: 1), 1, Baseline(), 1, 2).HasFlag(SaveAsStrictFailure.Excluded));
            Assert.True(Check(Window("Save As", handle: 2), 2, Baseline(), 1, 2).HasFlag(SaveAsStrictFailure.Excluded));
            Assert.True(Check(Window("Confirm Save As", handle: 5), 5, Baseline()).HasFlag(SaveAsStrictFailure.ConfirmPrompt));
            Assert.True(Check(Window("Save As", handle: 5, visible: false), 5, Baseline()).HasFlag(SaveAsStrictFailure.NotVisible));
            Assert.True(Check(Window("Save As", handle: 0), 0, Baseline()).HasFlag(SaveAsStrictFailure.NotVisible));
        }

        [Fact]
        public void FileNameEditIsFoundById1001ThenLegacy1152ThenAnyEditInsideACombo()
        {
            Assert.Equal(new IntPtr(13), SaveAsStrictMatcher.FindFileNameEdit(RealSaveDialogChildren())!.Value.Handle);

            var legacy = new List<SaveAsChild> { Child(1, "Edit", 7), Child(2, "Edit", 1152) };
            Assert.Equal(new IntPtr(2), SaveAsStrictMatcher.FindFileNameEdit(legacy)!.Value.Handle);

            var inCombo = new List<SaveAsChild> { Child(1, "Edit", 7), Child(2, "ComboBoxEx32", 3), Child(3, "Edit", 9, parent: 2) };
            Assert.Equal(new IntPtr(3), SaveAsStrictMatcher.FindFileNameEdit(inCombo)!.Value.Handle);

            var loose = new List<SaveAsChild> { Child(1, "Edit", 7), Child(2, "Edit", 8) };
            Assert.Null(SaveAsStrictMatcher.FindFileNameEdit(loose));
        }

        [Theory]
        [InlineData(5, "&Save")]
        [InlineData(5, "Save")]
        [InlineData(5, "SAVE")]
        public void SaveButtonIsRecognisedByItsSaveText(int id, string text)
        {
            var children = new List<SaveAsChild> { Child(1, "Button", 2, "Cancel"), Child(2, "Button", id, text) };

            Assert.Equal(new IntPtr(2), SaveAsStrictMatcher.FindSaveButton(children)!.Value.Handle);
        }

        [Fact]
        public void ANonButtonControlWithSaveTextIsNotTheSaveButton()
        {
            Assert.Null(SaveAsStrictMatcher.FindSaveButton(new List<SaveAsChild> { Child(1, "Static", 1, "Save") }));
        }

        [Theory]
        [InlineData("OK")]
        [InlineData("")]
        [InlineData("Save and Close")]
        public void ControlIdOneAloneIsNotTheSaveButton(string text)
        {
            Assert.Null(SaveAsStrictMatcher.FindSaveButton(new List<SaveAsChild> { Child(1, "Button", 1, text) }));
        }

        [Fact]
        public void InventoryListsOnlyNewOrForegroundPioneerWindowsBesidesMainAndPreview()
        {
            var all = new List<SaveAsCandidate>
            {
                Window("main", handle: 1),
                Window("preview", handle: 2),
                Window("modal", handle: 3),
                Window("old", handle: 4),
                Window("notepad", pid: OtherPid, handle: 6),
                Window("hidden", handle: 7, visible: false)
            };

            var rows = SaveAsStrictMatcher.InventoryWindows(all, PioneerPid, new IntPtr(3), new List<IntPtr> { new IntPtr(1), new IntPtr(2) }, Baseline(1, 2, 4, 6));

            var only = Assert.Single(rows);
            Assert.Equal(new IntPtr(3), only.Candidate.Handle);
            Assert.False(only.IsNew && !only.IsForeground);
        }

        [Fact]
        public void DescribeFailureNamesEveryFlag()
        {
            Assert.Equal("nothing", SaveAsCandidateLog.DescribeFailure(SaveAsStrictFailure.None));
            Assert.Equal("NotNew+NotForeground", SaveAsCandidateLog.DescribeFailure(SaveAsStrictFailure.NotNew | SaveAsStrictFailure.NotForeground));
        }
    }

    /// <summary>Records every call so the tests can assert exactly what was (not) sent.</summary>
    private sealed class FakeHost : ISaveAsNativeHost
    {
        public readonly List<string> Calls = new();
        public bool WindowExists = true;
        public bool Visible = true;
        public int Pid = PioneerPid;
        public string Class = Dlg;
        public string EditClass = "Edit";
        public string ButtonClass = "Button";
        public bool EditIsChild = true;
        public bool ButtonIsChild = true;
        public bool EditExists = true;
        public IntPtr Foreground = new(100);
        public bool SetTextWorks = true;
        public string? TextAfterSet;           // null = echo what was set
        public bool ClickWorks = true;
        public bool CloseMakesWindowDisappear = true;
        public bool ForegroundSurvivesClose = true;
        public bool AltF4Closes = false;
        private string _text = string.Empty;

        public bool IsWindow(IntPtr handle)
        {
            Calls.Add("IsWindow");
            return handle == new IntPtr(101) ? EditExists && WindowExists : WindowExists;
        }
        public bool IsVisible(IntPtr handle) => Visible;
        public int ProcessIdOf(IntPtr handle) => Pid;
        public string ClassOf(IntPtr handle) => handle.ToInt64() switch { 101 => EditClass, 102 => ButtonClass, _ => Class };
        public bool IsChildOf(IntPtr parent, IntPtr child) => child.ToInt64() switch { 101 => EditIsChild, 102 => ButtonIsChild, _ => false };
        public bool IsForeground(IntPtr handle) => Foreground == handle;
        public bool SetText(IntPtr edit, string text) { Calls.Add("SetText"); _text = text; return SetTextWorks; }
        public string? GetText(IntPtr edit) { Calls.Add("GetText"); return TextAfterSet ?? _text; }
        public bool ClickButton(IntPtr button) { Calls.Add("Click"); return ClickWorks; }

        public bool PostClose(IntPtr handle)
        {
            Calls.Add("WM_CLOSE");
            if (CloseMakesWindowDisappear) WindowExists = false;
            else if (!ForegroundSurvivesClose) Foreground = IntPtr.Zero;
            return true;
        }

        public bool SendAltF4()
        {
            Calls.Add("Alt+F4");
            if (AltF4Closes) WindowExists = false;
            return true;
        }

        public bool WaitUntil(Func<bool> condition, TimeSpan timeout) => condition();
    }

    public class EnterNameAndSaveTests
    {
        private const string Target = @"C:\Reports\10-31-26_Synthetic Report.pdf";
        private static readonly SaveAsCandidate Dialog = Window("Save As", handle: 100);
        private static readonly IntPtr Edit = new(101);
        private static readonly IntPtr SaveButton = new(102);

        private static SaveAsEntryResult Run(FakeHost host, List<string>? log = null) =>
            SaveAsActions.EnterNameAndSave(host, Dialog, Edit, SaveButton, Target, m => (log ?? new List<string>()).Add(m));

        [Fact]
        public void WritesTheNameReadsItBackThenClicksSaveAndSendsNoKeys()
        {
            var host = new FakeHost();

            Assert.Equal(SaveAsEntryResult.Clicked, Run(host));

            Assert.Contains("SetText", host.Calls);
            Assert.True(host.Calls.IndexOf("SetText") < host.Calls.IndexOf("GetText"));
            Assert.True(host.Calls.IndexOf("GetText") < host.Calls.IndexOf("Click"));
            Assert.DoesNotContain("Alt+F4", host.Calls);
            Assert.DoesNotContain("WM_CLOSE", host.Calls);
        }

        [Fact]
        public void AReadBackThatDoesNotMatchClosesTheDialogAndNeverClicksSave()
        {
            var host = new FakeHost { TextAfterSet = "something else" };

            Assert.Equal(SaveAsEntryResult.TextMismatch, Run(host));

            Assert.DoesNotContain("Click", host.Calls);
            Assert.Contains("WM_CLOSE", host.Calls);
            Assert.DoesNotContain("Alt+F4", host.Calls);
        }

        [Fact]
        public void AReadBackThatCannotBeReadCountsAsAMismatch()
        {
            var host = new FakeHost { TextAfterSet = "" };

            Assert.Equal(SaveAsEntryResult.TextMismatch, Run(host));
            Assert.DoesNotContain("Click", host.Calls);
        }

        [Fact]
        public void ASetTextThatCannotBeDeliveredClosesTheDialogAndNeverClicks()
        {
            var host = new FakeHost { SetTextWorks = false };

            Assert.Equal(SaveAsEntryResult.SetTextFailed, Run(host));
            Assert.DoesNotContain("GetText", host.Calls);
            Assert.DoesNotContain("Click", host.Calls);
            Assert.Contains("WM_CLOSE", host.Calls);
        }

        [Fact]
        public void AClickThatCannotBeDeliveredClosesTheDialog()
        {
            var host = new FakeHost { ClickWorks = false };

            Assert.Equal(SaveAsEntryResult.ClickFailed, Run(host));
            Assert.Contains("WM_CLOSE", host.Calls);
        }

        [Theory]
        [InlineData("gone")]
        [InlineData("hidden")]
        [InlineData("otherProcess")]
        [InlineData("otherClass")]
        [InlineData("notForeground")]
        public void ADialogThatChangedAfterItWasAcceptedGetsNothingAtAll(string change)
        {
            var host = new FakeHost();
            switch (change)
            {
                case "gone": host.WindowExists = false; break;
                case "hidden": host.Visible = false; break;
                case "otherProcess": host.Pid = OtherPid; break;
                case "otherClass": host.Class = "WindowsForms10.Window.8.app.0"; break;
                case "notForeground": host.Foreground = new IntPtr(555); break;
            }

            Assert.Equal(SaveAsEntryResult.StaleWindow, Run(host));

            Assert.DoesNotContain("SetText", host.Calls);
            Assert.DoesNotContain("Click", host.Calls);
            Assert.DoesNotContain("WM_CLOSE", host.Calls);
            Assert.DoesNotContain("Alt+F4", host.Calls);
        }

        [Theory]
        [InlineData("editNotChild")]
        [InlineData("buttonNotChild")]
        [InlineData("editWrongClass")]
        [InlineData("buttonWrongClass")]
        [InlineData("editGone")]
        public void AnEditOrSaveHandleThatIsNoLongerTheRightChildGetsNothing(string change)
        {
            var host = new FakeHost();
            switch (change)
            {
                case "editNotChild": host.EditIsChild = false; break;
                case "buttonNotChild": host.ButtonIsChild = false; break;
                case "editWrongClass": host.EditClass = "Static"; break;
                case "buttonWrongClass": host.ButtonClass = "Edit"; break;
                case "editGone": host.EditExists = false; break;
            }

            Assert.Equal(SaveAsEntryResult.StaleWindow, Run(host));

            Assert.DoesNotContain("SetText", host.Calls);
            Assert.DoesNotContain("Click", host.Calls);
            Assert.DoesNotContain("WM_CLOSE", host.Calls);
        }

        [Fact]
        public void LogLinesNeverContainTheDialogTitle()
        {
            var log = new List<string>();
            Run(new FakeHost { TextAfterSet = "x" }, log);

            Assert.All(log, line => Assert.DoesNotContain("Save As", line));
        }
    }

    public class PreviewCloserTests
    {
        private static readonly IntPtr Preview = new(100);

        private static PreviewCloseOutcome Close(FakeHost host, int pid = PioneerPid) =>
            PreviewCloser.Close(host, Preview, pid, TimeSpan.FromSeconds(1), _ => { });

        [Fact]
        public void ClosesWithAMessageFirstAndSendsNoKeysWhenThatWorks()
        {
            var host = new FakeHost();

            Assert.Equal(PreviewCloseOutcome.ClosedByMessage, Close(host));
            Assert.Contains("WM_CLOSE", host.Calls);
            Assert.DoesNotContain("Alt+F4", host.Calls);
        }

        [Fact]
        public void SendsAltF4OnlyWhenStillOpenAfterTheMessageAndStillForeground()
        {
            var host = new FakeHost { CloseMakesWindowDisappear = false, AltF4Closes = true };

            Assert.Equal(PreviewCloseOutcome.ClosedByAltF4, Close(host));
            Assert.True(host.Calls.IndexOf("WM_CLOSE") < host.Calls.IndexOf("Alt+F4"));
        }

        [Fact]
        public void NeverSendsAltF4WhenThePreviewIsNoLongerForeground()
        {
            var host = new FakeHost { CloseMakesWindowDisappear = false, ForegroundSurvivesClose = false };

            Assert.Equal(PreviewCloseOutcome.StillOpen, Close(host));
            Assert.DoesNotContain("Alt+F4", host.Calls);
        }

        [Fact]
        public void ReportsStillOpenWhenAltF4DoesNotCloseIt()
        {
            var host = new FakeHost { CloseMakesWindowDisappear = false, AltF4Closes = false };

            Assert.Equal(PreviewCloseOutcome.StillOpen, Close(host));
        }

        [Fact]
        public void RefusesAWindowThatIsNotPioneersAndSendsNothing()
        {
            var host = new FakeHost { Pid = OtherPid };

            Assert.Equal(PreviewCloseOutcome.NotPioneerWindow, Close(host));
            Assert.DoesNotContain("WM_CLOSE", host.Calls);
            Assert.DoesNotContain("Alt+F4", host.Calls);
        }

        [Fact]
        public void AWindowThatIsAlreadyGoneOrAZeroHandleIsLeftAlone()
        {
            var gone = new FakeHost { WindowExists = false };
            Assert.Equal(PreviewCloseOutcome.AlreadyClosed, Close(gone));
            Assert.DoesNotContain("WM_CLOSE", gone.Calls);

            var host = new FakeHost();
            Assert.Equal(PreviewCloseOutcome.AlreadyClosed, PreviewCloser.Close(host, IntPtr.Zero, PioneerPid, TimeSpan.FromSeconds(1), _ => { }));
            Assert.Empty(host.Calls);
        }

        [Fact]
        public void AnUnknownPioneerPidRefusesToClose()
        {
            var host = new FakeHost();

            Assert.Equal(PreviewCloseOutcome.NotPioneerWindow, Close(host, pid: 0));
            Assert.DoesNotContain("WM_CLOSE", host.Calls);
        }
    }
}
