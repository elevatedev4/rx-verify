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
        public void ReplacePromptIsRecognisedByTitleOrBodyText()
        {
            Assert.True(SaveAsDialogMatcher.IsReplacePrompt("Confirm Save As", new string[0]));
            Assert.True(SaveAsDialogMatcher.IsReplacePrompt("", new[] { "x.pdf already exists.", "Do you want to replace it?" }));
            Assert.True(SaveAsDialogMatcher.IsReplacePrompt("Export", new[] { "Overwrite existing file?" }));
        }

        [Fact]
        public void OtherYesNoQuestionsAreNotReplacePrompts()
        {
            Assert.False(SaveAsDialogMatcher.IsReplacePrompt("Export", new[] { "Open the exported file now?" }));
            Assert.False(SaveAsDialogMatcher.IsReplacePrompt(null, new string[0]));
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
        public void LineCarriesPrefixPidOwnerClassAndTitle()
        {
            var line = SaveAsCandidateLog.Format(Window("Save As", handle: 0x2A, owner: 0x10, ownerPid: PioneerPid), PioneerPid);

            Assert.StartsWith("save-as: ", line);
            Assert.Contains("handle=0x2A", line);
            Assert.Contains($"pid={PioneerPid}", line);
            Assert.Contains("owner=0x10", line);
            Assert.Contains("class='#32770'", line);
            Assert.Contains("title='Save As'", line);
            Assert.Contains("pioneer=yes", line);
        }

        [Fact]
        public void OtherAppTitlesAreNeverLogged()
        {
            var line = SaveAsCandidateLog.Format(Window("Inbox - patient@example.test - Mail", pid: OtherPid), PioneerPid);

            Assert.DoesNotContain("Inbox", line);
            Assert.DoesNotContain("patient", line);
            Assert.Contains("title='[other-app]'", line);
            Assert.Contains("title_len=", line);
            Assert.Contains("pioneer=no", line);
        }

        [Fact]
        public void PioneerScreenTitlesOutsideTheSaveVocabularyAreRedacted()
        {
            var line = SaveAsCandidateLog.Format(Window("Rx 1234567 - Jane Doe", className: "WindowsForms10.Window.8.app.0"), PioneerPid);

            Assert.DoesNotContain("Jane", line);
            Assert.Contains("title='[redacted]'", line);
        }

        [Theory]
        [InlineData("Open Rx 123 - DOE, JOHN")]
        [InlineData("Print Label - DOE, JANE")]
        public void PioneerTitlesWithOpenOrPrintAreRedacted(string title)
        {
            var line = SaveAsCandidateLog.Format(Window(title, className: "WindowsForms10.Window.8.app.0"), PioneerPid);

            Assert.DoesNotContain("DOE", line);
            Assert.Contains("title='[redacted]'", line);
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
    }
}
