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

    public class DetectorTests
    {
        private static HashSet<IntPtr> Baseline(params long[] handles) => new(handles.Select(h => new IntPtr(h)));

        private static SaveAsChild Child(long handle, string cls, int id, string text = "", long parent = 0) =>
            new(new IntPtr(handle), new IntPtr(parent), cls, id, text);

        // The Vista-style common dialog's Win32 descendants (control ids from the Windows SDK).
        private static List<SaveAsChild> RealSaveDialogChildren(string saveCaption = "&Save") => new()
        {
            Child(9, "ReBarWindow32", 0),
            Child(10, "UniversalSearchBand", 0, parent: 9),
            Child(16, "Edit", 0, parent: 10),            // the toolbar's search box
            Child(11, "ComboBoxEx32", 1148),
            Child(12, "ComboBox", 1148, parent: 11),
            Child(13, "Edit", 1001, parent: 12),         // File name:
            Child(14, "Button", 1, saveCaption),
            Child(15, "Button", 2, "Cancel")
        };

        private static SaveAsRejection Check(SaveAsCandidate c, IReadOnlyList<SaveAsChild> children, IReadOnlyCollection<IntPtr>? baseline) =>
            SaveAsDialogDetector.Evaluate(c, children, baseline);

        // The screenshot: a standard "Save As" dialog, new since the click.
        [Fact]
        public void TheScreenshotsDialogIsAccepted()
        {
            var dialog = Window("Save As", handle: 5, owner: 2, ownerPid: PioneerPid);

            Assert.Equal(SaveAsRejection.None, Check(dialog, RealSaveDialogChildren(), Baseline(1, 2)));
        }

        // Round 2's pid/owner gate rejected exactly this: the dialog lives in another process with no Pioneer owner.
        [Fact]
        public void ADialogOwnedByADifferentProcessThanPioneerIsStillAccepted()
        {
            var dialog = Window("Save As", pid: OtherPid, handle: 5, owner: 0, ownerPid: 0);

            Assert.Equal(SaveAsRejection.None, Check(dialog, RealSaveDialogChildren(), Baseline(1, 2)));
        }

        [Fact]
        public void ForegroundOwnerAndExcludedHandlesPlayNoPartInTheDecision()
        {
            // CheckWindow has no foreground / owner / pid / exclusion inputs at all; two different owners give the same verdict.
            var a = Window("Save As", handle: 5, owner: 7, ownerPid: OtherPid);
            var b = Window("Save As", handle: 5, owner: 0, ownerPid: 0);

            Assert.Equal(SaveAsDialogDetector.CheckWindow(a, Baseline(1)), SaveAsDialogDetector.CheckWindow(b, Baseline(1)));
        }

        [Theory]
        [InlineData("&Save")]
        [InlineData("Save")]
        [InlineData("S&ave")]
        [InlineData("&SAVE")]
        public void ASaveButtonWithOrWithoutTheMnemonicAmpersandIsRecognised(string caption)
        {
            var dialog = Window("Save As", handle: 5);
            var children = RealSaveDialogChildren(caption);

            Assert.Equal(SaveAsRejection.None, Check(dialog, children, Baseline(1)));
            Assert.Equal(new IntPtr(14), SaveAsDialogDetector.FindSaveButton(children)!.Value.Handle);
        }

        [Fact]
        public void TheFileNameEditNestedInAComboIsFoundByIdAndByComboParentAndTheSearchBoxIsNeverChosen()
        {
            Assert.Equal(new IntPtr(13), SaveAsDialogDetector.FindFileNameEdit(RealSaveDialogChildren())!.Value.Handle);

            // No usable id: the Edit under the ComboBoxEx32 / ComboBox is still identified through its combo parent.
            var noIds = new List<SaveAsChild>
            {
                Child(9, "ReBarWindow32", 0), Child(10, "UniversalSearchBand", 0, parent: 9), Child(16, "Edit", 0, parent: 10),
                Child(11, "ComboBoxEx32", 0), Child(12, "ComboBox", 0, parent: 11), Child(13, "Edit", 0, parent: 12),
                Child(14, "Button", 1, "&Save")
            };
            Assert.Equal(new IntPtr(13), SaveAsDialogDetector.FindFileNameEdit(noIds)!.Value.Handle);

            // Legacy dialog: id 1152.
            var legacy = new List<SaveAsChild> { Child(1, "Edit", 7), Child(2, "Edit", 1152), Child(3, "Button", 1, "&Save") };
            Assert.Equal(new IntPtr(2), SaveAsDialogDetector.FindFileNameEdit(legacy)!.Value.Handle);
        }

        [Fact]
        public void ADialogWhoseOnlyEditIsUnidentifiedIsStillDetectedButHasNoFileNameEdit()
        {
            var children = new List<SaveAsChild> { Child(1, "Edit", 7), Child(2, "Button", 1, "&Save") };

            Assert.Equal(SaveAsRejection.None, Check(Window("Save As", handle: 5), children, Baseline(1)));
            Assert.Null(SaveAsDialogDetector.FindFileNameEdit(children));
        }

        [Fact]
        public void AnEditInsideTheSearchBoxOnlyIsNeverTheFileNameEdit()
        {
            var children = new List<SaveAsChild>
            {
                Child(9, "ReBarWindow32", 0), Child(10, "UniversalSearchBand", 0, parent: 9), Child(16, "Edit", 1001, parent: 10),
                Child(14, "Button", 1, "&Save")
            };

            Assert.Null(SaveAsDialogDetector.FindFileNameEdit(children));
        }

        // The review's blockers: other new windows of Pioneer must never be accepted.
        [Fact]
        public void APreExistingUnrelatedDialogIsRejectedAsNotNew()
        {
            var old = Window("Save As", handle: 5);

            Assert.Equal(SaveAsRejection.NotNew, Check(old, RealSaveDialogChildren(), Baseline(5)));
            Assert.Equal(SaveAsRejection.NotNew, Check(old, RealSaveDialogChildren(), baseline: null));
        }

        [Fact]
        public void ANewDialogWithADisallowedTitleIsRejectedEvenWithAnEditAndASaveButton()
        {
            foreach (var title in new[] { "Enter Quantity", "Confirm Save As", "Export to PDF", "Save As - Jane Doe", "", "Rx 1234567 - Jane Doe" })
            {
                var rejection = Check(Window(title, handle: 5), RealSaveDialogChildren(), Baseline(1));
                Assert.Equal(SaveAsRejection.TitleNotAllowed, rejection);
            }
        }

        [Theory]
        [InlineData("Save As")]
        [InlineData("Save Print Output As")]
        [InlineData("Export")]
        [InlineData("Save")]
        [InlineData("save as")]
        [InlineData("  Save As ")]
        public void EveryAllowlistedTitleIsAccepted(string title)
        {
            Assert.Equal(SaveAsRejection.None, Check(Window(title, handle: 5), RealSaveDialogChildren(), Baseline(1)));
        }

        [Fact]
        public void ANewSaveAsTitledDialogWithoutAnEditOrWithoutASaveButtonIsRejected()
        {
            var dialog = Window("Save As", handle: 5);

            var noEdit = new List<SaveAsChild> { Child(1, "Static", 65535, "Done"), Child(2, "Button", 1, "&Save") };
            Assert.Equal(SaveAsRejection.NoEditChild, Check(dialog, noEdit, Baseline(1)));

            var noSave = new List<SaveAsChild> { Child(1, "Edit", 1001), Child(2, "Button", 1, "OK"), Child(3, "Button", 2, "Cancel") };
            Assert.Equal(SaveAsRejection.NoSaveButton, Check(dialog, noSave, Baseline(1)));

            var saveAndClose = new List<SaveAsChild> { Child(1, "Edit", 1001), Child(2, "Button", 1, "Save and Close") };
            Assert.Equal(SaveAsRejection.NoSaveButton, Check(dialog, saveAndClose, Baseline(1)));

            var staticSave = new List<SaveAsChild> { Child(1, "Edit", 1001), Child(2, "Static", 1, "Save") };
            Assert.Equal(SaveAsRejection.NoSaveButton, Check(dialog, staticSave, Baseline(1)));
        }

        [Fact]
        public void InvisibleHandleZeroAndNonDialogClassWindowsAreRejected()
        {
            Assert.True(Check(Window("Save As", handle: 5, visible: false), RealSaveDialogChildren(), Baseline(1)).HasFlag(SaveAsRejection.NotVisible));
            Assert.True(Check(Window("Save As", handle: 0), RealSaveDialogChildren(), Baseline(1)).HasFlag(SaveAsRejection.NotVisible));
            Assert.True(Check(Window("Save As", className: "WindowsForms10.Window.8.app.0", handle: 5), RealSaveDialogChildren(), Baseline(1)).HasFlag(SaveAsRejection.NotDialogClass));
        }

        [Fact]
        public void FindReturnsTheNewDialogAndOnlyInspectsWindowsThatPassTheWindowTests()
        {
            var all = new List<SaveAsCandidate>
            {
                Window("Run Financial Reports", className: "WindowsForms10.Window.8.app.0", handle: 1),
                Window("Save As", handle: 2),                       // pre-existing: in the baseline
                Window("Notice", handle: 3),                        // new but not allowlisted
                Window("Save As", pid: OtherPid, handle: 4),        // the real one, another process
                Window("Save As", handle: 6)                        // a second candidate: first one wins
            };
            var inspected = new List<long>();

            var found = SaveAsDialogDetector.Find(all, Baseline(1, 2), h =>
            {
                inspected.Add(h.ToInt64());
                return RealSaveDialogChildren();
            });

            Assert.NotNull(found);
            Assert.Equal(new IntPtr(4), found!.Window.Handle);
            Assert.Equal(new IntPtr(13), found.FileNameEdit!.Value.Handle);
            Assert.Equal(new IntPtr(14), found.SaveButton.Handle);
            Assert.Equal(new IntPtr(15), found.CancelButton!.Value.Handle);
            Assert.Equal(new List<long> { 4 }, inspected);
        }

        [Fact]
        public void FindReturnsNullWhenNothingQualifies()
        {
            var all = new List<SaveAsCandidate> { Window("Save As", handle: 2), Window("Notice", handle: 3) };

            Assert.Null(SaveAsDialogDetector.Find(all, Baseline(2), _ => RealSaveDialogChildren()));
            Assert.Null(SaveAsDialogDetector.Find(new List<SaveAsCandidate>(), Baseline(), _ => RealSaveDialogChildren()));
        }

        [Fact]
        public void CancelButtonIsFoundByCaptionThenByControlId2()
        {
            Assert.Equal(new IntPtr(15), SaveAsDialogDetector.FindCancelButton(RealSaveDialogChildren())!.Value.Handle);

            var byId = new List<SaveAsChild> { Child(1, "Button", 1, "&Save"), Child(2, "Button", 2, "Annuler") };
            Assert.Equal(new IntPtr(2), SaveAsDialogDetector.FindCancelButton(byId)!.Value.Handle);

            Assert.Null(SaveAsDialogDetector.FindCancelButton(new List<SaveAsChild> { Child(1, "Button", 1, "&Save") }));
        }
    }

    public class WindowTableTests
    {
        private static SaveAsChild Child(long handle, string cls, int id, string text = "", long parent = 0) =>
            new(new IntPtr(handle), new IntPtr(parent), cls, id, text);

        private static List<string> Table(IReadOnlyList<SaveAsWindowRow> rows, params long[] baseline) =>
            SaveAsWindowTable.Format(rows, new HashSet<IntPtr>(baseline.Select(h => new IntPtr(h))), PioneerPid).ToList();

        [Fact]
        public void APioneerWindowWithAPatientLikeTitleIsNeverLoggedOnlyItsLength()
        {
            const string title = "Rx 1234567 - DOE, JANE";
            var rows = new List<SaveAsWindowRow>
            {
                new(Window(title, className: "WindowsForms10.Window.8.app.0", handle: 0x30), "PioneerRx", null),
                new(Window("Inbox - patient@example.test - Mail", className: "MailWnd", pid: OtherPid, handle: 0x31), "mail", null)
            };

            var text = string.Join("\n", Table(rows, 0x30));

            Assert.Contains($"title=<hidden, {title.Length} chars>", text);
            foreach (var word in new[] { "1234567", "DOE", "JANE", "Rx ", "patient", "Inbox", "Mail -" })
            {
                Assert.DoesNotContain(word, text);
            }
        }

        [Fact]
        public void AllowlistedTitlesAreShownAndEveryRowCarriesClassProcessPidsAndSnapshotFlag()
        {
            var rows = new List<SaveAsWindowRow>
            {
                new(Window("Save As", handle: 0x40, pid: OtherPid, ownerPid: 0), "PioneerHelper", new List<SaveAsChild>()),
                new(Window("Run Financial Reports", className: "WindowsForms10.Window.8.app.0", handle: 0x41, ownerPid: 0), "PioneerRx", null)
            };

            var lines = Table(rows, 0x41);

            Assert.Contains("hwnd=0x40 class='#32770' process='PioneerHelper' pid=9999 ownerPid=0 title='Save As' in_pre_click_snapshot=no", lines[1]);
            Assert.Contains("hwnd=0x41 class='WindowsForms10.Window.8.app.0' process='PioneerRx' pid=1234 ownerPid=0 title=<hidden, 21 chars> in_pre_click_snapshot=yes", lines[2]);
            Assert.DoesNotContain("Run Financial Reports", string.Join("\n", lines));
        }

        [Fact]
        public void ADialogRowShowsTheVerdictDescendantClassesAndButtonCaptions()
        {
            var children = new List<SaveAsChild>
            {
                Child(1, "ComboBoxEx32", 1148), Child(2, "ComboBox", 1148, parent: 1), Child(3, "Edit", 1001, parent: 2),
                Child(4, "Button", 1, "&Save"), Child(5, "Button", 2, "Cancel"), Child(6, "Button", 9, "Print for DOE, JANE")
            };
            var rows = new List<SaveAsWindowRow> { new(Window("Save As", handle: 0x50), "PioneerRx", children) };

            var line = Table(rows)[1];

            Assert.Contains("verdict=ACCEPTED", line);
            Assert.Contains("ComboBoxEx32; ComboBox; Edit id=1001", line);
            Assert.Contains("Button id=1 caption='&Save'", line);
            Assert.Contains("Button id=2 caption='Cancel'", line);
            Assert.Contains("caption=<hidden, 19 chars>", line);
            Assert.DoesNotContain("DOE", line);
        }

        [Fact]
        public void APreExistingQualifyingDialogShowsNotNewSoTheNextLogExplainsAMiss()
        {
            var children = new List<SaveAsChild> { Child(3, "Edit", 1001), Child(4, "Button", 1, "&Save") };
            var rows = new List<SaveAsWindowRow> { new(Window("Save As", handle: 0x50), "PioneerRx", children) };

            var line = Table(rows, 0x50)[1];

            Assert.Contains("in_pre_click_snapshot=yes", line);
            Assert.Contains("verdict=NotNew", line);
        }

        [Fact]
        public void AMissingSnapshotIsStatedNotGuessed()
        {
            var rows = new List<SaveAsWindowRow> { new(Window("x", handle: 1), "p", null) };

            var lines = SaveAsWindowTable.Format(rows, null, PioneerPid);

            Assert.Contains("pre-click snapshot unavailable", lines[0]);
            Assert.Contains("in_pre_click_snapshot=unknown", lines[1]);
        }

        [Fact]
        public void TheHeaderListsTheAllowlistAndTheRowCountIsCapped()
        {
            var rows = Enumerable.Range(1, SaveAsWindowTable.MaxRows + 5)
                .Select(i => new SaveAsWindowRow(Window("t", handle: i), "p", null))
                .ToList();

            var lines = Table(rows);

            Assert.Contains("'Save As', 'Save Print Output As', 'Export', 'Save'", lines[0]);
            Assert.Equal(1 + SaveAsWindowTable.MaxRows + 1, lines.Count);
            Assert.Contains("+5 more windows", lines[^1]);
        }

        [Fact]
        public void EveryLineStartsWithThePrefix()
        {
            var rows = new List<SaveAsWindowRow> { new(Window("Save As", handle: 0x50), "PioneerRx", new List<SaveAsChild>()) };

            Assert.All(Table(rows), line => Assert.StartsWith("save-as: ", line));
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
        public bool BringToForegroundWorks = true;
        public bool SetTextWorks = true;
        public string? TextAfterSet;           // null = echo what was set
        public bool ClickWorks = true;
        public bool TypeWorks = true;
        public string? TextAfterTyping;        // null = echo what was typed
        public bool LoseForegroundWhileTyping;
        public bool AltSWorks = true;
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

        public bool BringToForeground(IntPtr handle)
        {
            Calls.Add("BringToForeground");
            if (BringToForegroundWorks) Foreground = handle;
            return BringToForegroundWorks;
        }

        public bool TypeFileName(string text)
        {
            Calls.Add("Type");
            _text = TextAfterTyping ?? text;
            TextAfterSet = null;
            if (LoseForegroundWhileTyping) Foreground = IntPtr.Zero;
            return TypeWorks;
        }

        public bool SendAltS() { Calls.Add("Alt+S"); return AltSWorks; }

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

        private static SaveAsEntryResult Run(FakeHost host, List<string>? log = null, IntPtr? edit = null) =>
            SaveAsActions.EnterNameAndSave(host, Dialog, edit ?? Edit, SaveButton, Target, m => (log ?? new List<string>()).Add(m));

        private static void AssertNoKeysAndNoClose(FakeHost host)
        {
            Assert.DoesNotContain("Type", host.Calls);
            Assert.DoesNotContain("Alt+S", host.Calls);
            Assert.DoesNotContain("Alt+F4", host.Calls);
            Assert.DoesNotContain("WM_CLOSE", host.Calls);
        }

        [Fact]
        public void WritesTheNameReadsItBackThenClicksSaveAndSendsNoKeys()
        {
            var host = new FakeHost();

            Assert.Equal(SaveAsEntryResult.Clicked, Run(host));

            Assert.True(host.Calls.IndexOf("SetText") < host.Calls.IndexOf("GetText"));
            Assert.True(host.Calls.IndexOf("GetText") < host.Calls.IndexOf("Click"));
            AssertNoKeysAndNoClose(host);
        }

        [Fact]
        public void TheDirectRouteNeedsNoForegroundAndStillSendsNoKeys()
        {
            var host = new FakeHost { Foreground = new IntPtr(555) };

            Assert.Equal(SaveAsEntryResult.Clicked, Run(host));

            Assert.DoesNotContain("BringToForeground", host.Calls);
            AssertNoKeysAndNoClose(host);
        }

        [Fact]
        public void AReadBackThatDoesNotMatchFallsBackToTypingAndAltSWhileTheDialogIsForeground()
        {
            var host = new FakeHost { TextAfterSet = "something else" };

            Assert.Equal(SaveAsEntryResult.ClickedByKeyboard, Run(host));

            Assert.DoesNotContain("Click", host.Calls);
            Assert.True(host.Calls.IndexOf("Type") < host.Calls.IndexOf("Alt+S"));
            Assert.DoesNotContain("Alt+F4", host.Calls);
            Assert.DoesNotContain("WM_CLOSE", host.Calls);
        }

        [Fact]
        public void TheKeyboardFallbackNeverSendsAKeyWhenTheDialogCannotBeBroughtToTheForeground()
        {
            var host = new FakeHost { TextAfterSet = "something else", Foreground = new IntPtr(555), BringToForegroundWorks = false };

            Assert.Equal(SaveAsEntryResult.NotForeground, Run(host));

            Assert.Contains("BringToForeground", host.Calls);
            AssertNoKeysAndNoClose(host);
            Assert.DoesNotContain("Click", host.Calls);
        }

        [Fact]
        public void TheKeyboardFallbackBringsTheDialogForwardOnceAndThenMayType()
        {
            var host = new FakeHost { SetTextWorks = false, Foreground = new IntPtr(555) };

            Assert.Equal(SaveAsEntryResult.ClickedByKeyboard, Run(host));

            Assert.Equal(1, host.Calls.Count(c => c == "BringToForeground"));
            Assert.True(host.Calls.IndexOf("BringToForeground") < host.Calls.IndexOf("Type"));
        }

        [Fact]
        public void LosingTheForegroundWhileTypingStopsBeforeAltS()
        {
            var host = new FakeHost { SetTextWorks = false, LoseForegroundWhileTyping = true };

            Assert.Equal(SaveAsEntryResult.KeyboardFailed, Run(host));

            Assert.Contains("Type", host.Calls);
            Assert.DoesNotContain("Alt+S", host.Calls);
        }

        [Fact]
        public void ATypedNameThatReadsBackWrongIsNotSaved()
        {
            var host = new FakeHost { SetTextWorks = false, TextAfterTyping = "wrong" };

            Assert.Equal(SaveAsEntryResult.KeyboardFailed, Run(host));

            Assert.DoesNotContain("Alt+S", host.Calls);
        }

        [Fact]
        public void WhenNoFileNameEditWasIdentifiedNothingIsWrittenToAnyEditAndTheKeyboardRouteRuns()
        {
            var host = new FakeHost();

            Assert.Equal(SaveAsEntryResult.ClickedByKeyboard, Run(host, edit: IntPtr.Zero));

            Assert.DoesNotContain("SetText", host.Calls);
            Assert.DoesNotContain("GetText", host.Calls);
            Assert.True(host.Calls.IndexOf("Type") < host.Calls.IndexOf("Alt+S"));
        }

        [Fact]
        public void AClickThatCannotBeDeliveredAfterAVerifiedNameFallsBackToAltS()
        {
            var host = new FakeHost { ClickWorks = false };

            Assert.Equal(SaveAsEntryResult.ClickedByKeyboard, Run(host));

            Assert.DoesNotContain("Type", host.Calls);   // the name was already verified
            Assert.Contains("Alt+S", host.Calls);
        }

        [Fact]
        public void AnAltSThatCannotBeSentIsAFailureNotASuccess()
        {
            var host = new FakeHost { ClickWorks = false, AltSWorks = false };

            Assert.Equal(SaveAsEntryResult.ClickFailed, Run(host));
        }

        [Theory]
        [InlineData("gone")]
        [InlineData("hidden")]
        [InlineData("otherProcess")]
        [InlineData("otherClass")]
        public void ADialogThatChangedAfterItWasAcceptedGetsNothingAtAll(string change)
        {
            var host = new FakeHost();
            switch (change)
            {
                case "gone": host.WindowExists = false; break;
                case "hidden": host.Visible = false; break;
                case "otherProcess": host.Pid = OtherPid; break;
                case "otherClass": host.Class = "WindowsForms10.Window.8.app.0"; break;
            }

            Assert.Equal(SaveAsEntryResult.StaleWindow, Run(host));

            Assert.DoesNotContain("SetText", host.Calls);
            Assert.DoesNotContain("Click", host.Calls);
            AssertNoKeysAndNoClose(host);
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
            AssertNoKeysAndNoClose(host);
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
