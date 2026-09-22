using System;
using System.Collections.Generic;
using RxVerifyOverlay.Reports;
using Xunit;

namespace RxVerifyOverlay.Tests.Reports;

/// <summary>
/// Unit tests for Reports/AutomationWindowSelection.cs - the two pure
/// decisions behind PioneerReportDriver's two review-flagged safety
/// fixes. No FlaUI/UIA/Windows involved; everything is plain (title,
/// pid, hwnd) tuples and IntPtr comparisons.
/// </summary>
public class AutomationWindowSelectionTests
{
    private static readonly IntPtr PioneerSaveAs = new(100);
    private static readonly IntPtr OtherAppSaveAs = new(200);
    private static readonly IntPtr PioneerSaveAsTwo = new(300);
    private const int PioneerPid = 1234;
    private const int OtherPid = 9999;

    public class SaveAsWindowSelectorTests
    {
        [Fact]
        public void IgnoresASameTitledWindowFromADifferentProcess()
        {
            var candidates = new List<WindowCandidate>
            {
                new("Save As", OtherPid, OtherAppSaveAs)
            };

            var chosen = SaveAsWindowSelector.Choose(candidates, PioneerPid, foregroundHandle: OtherAppSaveAs);

            Assert.Null(chosen);
        }

        [Fact]
        public void ChoosesTheOnlyPioneerOwnedCandidate()
        {
            var candidates = new List<WindowCandidate>
            {
                new("Save As", PioneerPid, PioneerSaveAs)
            };

            var chosen = SaveAsWindowSelector.Choose(candidates, PioneerPid, foregroundHandle: IntPtr.Zero);

            Assert.NotNull(chosen);
            Assert.Equal(PioneerSaveAs, chosen!.Value);
        }

        [Fact]
        public void PrefersTheForegroundCandidateWhenMultiplePioneerOwnedMatch()
        {
            var candidates = new List<WindowCandidate>
            {
                new("Save As", PioneerPid, PioneerSaveAs),
                new("Save As", PioneerPid, PioneerSaveAsTwo)
            };

            var chosen = SaveAsWindowSelector.Choose(candidates, PioneerPid, foregroundHandle: PioneerSaveAsTwo);

            Assert.NotNull(chosen);
            Assert.Equal(PioneerSaveAsTwo, chosen!.Value);
        }

        [Fact]
        public void RefusesToGuessBetweenMultipleNonForegroundCandidates()
        {
            var candidates = new List<WindowCandidate>
            {
                new("Save As", PioneerPid, PioneerSaveAs),
                new("Save As", PioneerPid, PioneerSaveAsTwo)
            };

            var chosen = SaveAsWindowSelector.Choose(candidates, PioneerPid, foregroundHandle: new IntPtr(999));

            Assert.Null(chosen);
        }

        [Fact]
        public void IgnoresPioneerOwnedWindowsWithoutSaveAsInTheTitle()
        {
            var candidates = new List<WindowCandidate>
            {
                new("PioneerRx - Main", PioneerPid, PioneerSaveAs)
            };

            var chosen = SaveAsWindowSelector.Choose(candidates, PioneerPid, foregroundHandle: PioneerSaveAs);

            Assert.Null(chosen);
        }

        [Fact]
        public void NoCandidatesAtAllReturnsNull()
        {
            var chosen = SaveAsWindowSelector.Choose(new List<WindowCandidate>(), PioneerPid, foregroundHandle: IntPtr.Zero);

            Assert.Null(chosen);
        }

        [Fact]
        public void PrefersThePioneerOwnedCandidateEvenWhenAnUnrelatedAppsWindowIsForeground()
        {
            // The whole point of blocker 1: a foreground "Save As" window
            // from a DIFFERENT app must never be chosen, even if it is
            // literally the OS foreground window right now.
            var candidates = new List<WindowCandidate>
            {
                new("Save As", OtherPid, OtherAppSaveAs),
                new("Save As", PioneerPid, PioneerSaveAs)
            };

            var chosen = SaveAsWindowSelector.Choose(candidates, PioneerPid, foregroundHandle: OtherAppSaveAs);

            Assert.NotNull(chosen);
            Assert.Equal(PioneerSaveAs, chosen!.Value);
        }
    }

    public class PreviewCloseDecisionTests
    {
        private static readonly IntPtr PreviewHandle = new(500);
        private static readonly IntPtr MainWindowHandle = new(600);

        [Fact]
        public void NeverFallsBackWhenThePrimaryCloseAlreadySucceeded()
        {
            var shouldFallBack = PreviewCloseDecision.ShouldFallBackToAltF4(
                primaryCloseSucceeded: true, previewHandle: PreviewHandle, foregroundHandle: PreviewHandle);

            Assert.False(shouldFallBack);
        }

        [Fact]
        public void FallsBackWhenForegroundIsExactlyThePreviewHandle()
        {
            var shouldFallBack = PreviewCloseDecision.ShouldFallBackToAltF4(
                primaryCloseSucceeded: false, previewHandle: PreviewHandle, foregroundHandle: PreviewHandle);

            Assert.True(shouldFallBack);
        }

        [Fact]
        public void NeverFallsBackWhenForegroundIsTheMainWindowInstead()
        {
            // The exact bug blocker 2 describes: focus reverted to
            // Pioneer's main window after Save As closed.
            var shouldFallBack = PreviewCloseDecision.ShouldFallBackToAltF4(
                primaryCloseSucceeded: false, previewHandle: PreviewHandle, foregroundHandle: MainWindowHandle);

            Assert.False(shouldFallBack);
        }

        [Fact]
        public void NeverFallsBackWhenThePreviewHandleIsUnknown()
        {
            var shouldFallBack = PreviewCloseDecision.ShouldFallBackToAltF4(
                primaryCloseSucceeded: false, previewHandle: null, foregroundHandle: MainWindowHandle);

            Assert.False(shouldFallBack);
        }

        [Fact]
        public void NeverFallsBackWhenThePreviewHandleIsZero()
        {
            var shouldFallBack = PreviewCloseDecision.ShouldFallBackToAltF4(
                primaryCloseSucceeded: false, previewHandle: IntPtr.Zero, foregroundHandle: MainWindowHandle);

            Assert.False(shouldFallBack);
        }

        [Fact]
        public void NeverFallsBackWhenForegroundIsUnknown()
        {
            var shouldFallBack = PreviewCloseDecision.ShouldFallBackToAltF4(
                primaryCloseSucceeded: false, previewHandle: PreviewHandle, foregroundHandle: IntPtr.Zero);

            Assert.False(shouldFallBack);
        }
    }

    /// <summary>
    /// W-T92 review fix (BLOCKING): the pure half of "does this window
    /// look like the report preview" - shared by FindPreviewWindow and
    /// TryFindFallbackPreviewWindow (PioneerReportDriver.cs) so a new
    /// window that appears after F12 is never mislabelled "the preview"
    /// just because it's new and Pioneer-owned; it has to actually expose
    /// one of the two toolbar affordances.
    /// </summary>
    public class PreviewWindowAffordancesTests
    {
        [Fact]
        public void HasAffordanceWhenOnlyPrintImmediatelyIsPresent()
        {
            Assert.True(PreviewWindowAffordances.HasAffordance(hasPrintImmediately: true, hasExportToPdf: false));
        }

        [Fact]
        public void HasAffordanceWhenOnlyExportToPdfIsPresent()
        {
            Assert.True(PreviewWindowAffordances.HasAffordance(hasPrintImmediately: false, hasExportToPdf: true));
        }

        [Fact]
        public void HasAffordanceWhenBothArePresent()
        {
            Assert.True(PreviewWindowAffordances.HasAffordance(hasPrintImmediately: true, hasExportToPdf: true));
        }

        [Fact]
        public void NoAffordanceWhenNeitherIsPresent()
        {
            // The exact bug the blocker describes: an unrelated dialog is a
            // new Pioneer-owned window too, but it has neither toolbar icon.
            Assert.False(PreviewWindowAffordances.HasAffordance(hasPrintImmediately: false, hasExportToPdf: false));
        }
    }

    /// <summary>Round 2: PioneerReportDriver.OpenRibbonScreen's post-strategy confirmation check.</summary>
    public class RibbonScreenConfirmationTests
    {
        [Fact]
        public void BuildConfirmationHintsIncludesTheScreenNameAndTheRunPrefixedForm()
        {
            var hints = RibbonScreenConfirmation.BuildConfirmationHints("Financial Reports");

            Assert.Equal(new[] { "Financial Reports", "Run Financial Reports" }, hints);
        }

        [Fact]
        public void NameContainsAnyHint_TrueForExactMatch()
        {
            var hints = RibbonScreenConfirmation.BuildConfirmationHints("Financial Reports");

            Assert.True(RibbonScreenConfirmation.NameContainsAnyHint("Financial Reports", hints));
        }

        [Fact]
        public void NameContainsAnyHint_TrueForTheRunPrefixedTitle()
        {
            var hints = RibbonScreenConfirmation.BuildConfirmationHints("Financial Reports");

            Assert.True(RibbonScreenConfirmation.NameContainsAnyHint("Run Financial Reports", hints));
        }

        [Fact]
        public void NameContainsAnyHint_TrueForASubstringMatch()
        {
            var hints = RibbonScreenConfirmation.BuildConfirmationHints("Financial Reports");

            Assert.True(RibbonScreenConfirmation.NameContainsAnyHint("PioneerRx - Run Financial Reports (2026)", hints));
        }

        [Fact]
        public void NameContainsAnyHint_IsCaseInsensitive()
        {
            var hints = RibbonScreenConfirmation.BuildConfirmationHints("Financial Reports");

            Assert.True(RibbonScreenConfirmation.NameContainsAnyHint("run financial reports", hints));
        }

        [Fact]
        public void NameContainsAnyHint_FalseForAnUnrelatedTitle()
        {
            var hints = RibbonScreenConfirmation.BuildConfirmationHints("Financial Reports");

            Assert.False(RibbonScreenConfirmation.NameContainsAnyHint("Payments", hints));
        }

        [Fact]
        public void NameContainsAnyHint_FalseForNullOrEmptyName()
        {
            var hints = RibbonScreenConfirmation.BuildConfirmationHints("Financial Reports");

            Assert.False(RibbonScreenConfirmation.NameContainsAnyHint(null, hints));
            Assert.False(RibbonScreenConfirmation.NameContainsAnyHint(string.Empty, hints));
        }
    }

    /// <summary>
    /// Round 3: PioneerReportDriver.FindMainWindow's pure ranking decision
    /// — the fix for the owner's first real run resolving to pid 24828,
    /// handle=0x0, name='&lt;untitled&gt;' (a Pioneer helper process with no
    /// UI) instead of the real shell (pid 20664).
    /// </summary>
    public class MainWindowSelectorTests
    {
        private static MainWindowCandidate Candidate(
            string title = "Run Financial Reports",
            int processId = 20664,
            long handle = 0xA025E,
            string className = "WindowsForms10.Window.8.app.0.37e3228_r7_ad1",
            int width = 1200,
            int height = 800,
            bool isPioneerProcess = true,
            bool isPrecheckFamily = false) =>
            new(title, processId, new IntPtr(handle), className, width, height, isPioneerProcess, isPrecheckFamily);

        [Fact]
        public void ReturnsNullWhenThereAreNoCandidates()
        {
            Assert.Null(MainWindowSelector.Choose(new List<MainWindowCandidate>()));
        }

        [Fact]
        public void RejectsAHelperWindowWithAZeroHandle()
        {
            // The exact round-3 bug: pid 24828, handle=0x0, name='<untitled>'.
            var helper = Candidate(title: "", processId: 24828, handle: 0, isPioneerProcess: true);

            var chosen = MainWindowSelector.Choose(new List<MainWindowCandidate> { helper });

            Assert.Null(chosen);
        }

        [Fact]
        public void RejectsACandidateWithAnEmptyTitleEvenWithANonZeroHandle()
        {
            var candidate = Candidate(title: "", handle: 0x1234);

            var chosen = MainWindowSelector.Choose(new List<MainWindowCandidate> { candidate });

            Assert.Null(chosen);
        }

        [Fact]
        public void RejectsThePrecheckFamilyWindow()
        {
            var precheck = Candidate(title: "Edit Rx - 123456", isPrecheckFamily: true);

            var chosen = MainWindowSelector.Choose(new List<MainWindowCandidate> { precheck });

            Assert.Null(chosen);
        }

        [Fact]
        public void RejectsANonPioneerWindowWhoseTitleDoesNotMentionPioneer()
        {
            var other = Candidate(title: "Notepad", isPioneerProcess: false);

            var chosen = MainWindowSelector.Choose(new List<MainWindowCandidate> { other });

            Assert.Null(chosen);
        }

        [Fact]
        public void AcceptsATitleContainingPioneerEvenWhenNotThePioneerProcess()
        {
            var candidate = Candidate(title: "PioneerRx - Some Screen", isPioneerProcess: false, className: "SomeOtherClass");

            var chosen = MainWindowSelector.Choose(new List<MainWindowCandidate> { candidate });

            Assert.NotNull(chosen);
        }

        [Fact]
        public void PicksTheRealShellOverTheZeroHandleHelperProcess()
        {
            // The exact round-3 scenario: helper pid 24828 (handle 0x0,
            // untitled) enumerated ALONGSIDE the real shell pid 20664.
            var helper = Candidate(title: "", processId: 24828, handle: 0);
            var realShell = Candidate(title: "Run Financial Reports", processId: 20664, handle: 0xA025E);

            var chosen = MainWindowSelector.Choose(new List<MainWindowCandidate> { helper, realShell });

            Assert.NotNull(chosen);
            Assert.Equal(20664, chosen!.Value.ProcessId);
        }

        [Fact]
        public void PrefersTheLargestWindowsForms10WindowAmongEligibleCandidates()
        {
            var smaller = Candidate(handle: 0x1, width: 400, height: 300);
            var larger = Candidate(handle: 0x2, width: 1200, height: 800);

            var chosen = MainWindowSelector.Choose(new List<MainWindowCandidate> { smaller, larger });

            Assert.NotNull(chosen);
            Assert.Equal(new IntPtr(0x2), chosen!.Value.Handle);
        }

        [Fact]
        public void FallsBackToTheLargestEligibleCandidateWhenNoneIsWindowsForms10()
        {
            var a = Candidate(handle: 0x1, className: "SomeOtherToolkit.Window", width: 400, height: 300);
            var b = Candidate(handle: 0x2, className: "SomeOtherToolkit.Window", width: 1200, height: 800);

            var chosen = MainWindowSelector.Choose(new List<MainWindowCandidate> { a, b });

            Assert.NotNull(chosen);
            Assert.Equal(new IntPtr(0x2), chosen!.Value.Handle);
        }

        [Fact]
        public void PrefersAWindowsForms10CandidateOverALargerNonWindowsForms10Candidate()
        {
            var largeOtherToolkit = Candidate(handle: 0x1, className: "SomeOtherToolkit.Window", width: 5000, height: 5000);
            var smallerWinForms = Candidate(handle: 0x2, className: "WindowsForms10.Window.8.app.0.37e3228_r7_ad1", width: 800, height: 600);

            var chosen = MainWindowSelector.Choose(new List<MainWindowCandidate> { largeOtherToolkit, smallerWinForms });

            Assert.NotNull(chosen);
            Assert.Equal(new IntPtr(0x2), chosen!.Value.Handle);
        }
    }

    public class MainWindowTitleRedactionTests
    {
        [Fact]
        public void ReturnsUntitledPlaceholderForEmptyOrNullTitle()
        {
            Assert.Equal("<untitled>", MainWindowTitleRedaction.RedactIfNeeded(null));
            Assert.Equal("<untitled>", MainWindowTitleRedaction.RedactIfNeeded(""));
        }

        [Theory]
        [InlineData("Run Financial Reports")]
        [InlineData("Financial Reports")]
        [InlineData("Run Payments")]
        [InlineData("Payments")]
        public void DoesNotRedactAKnownScreenName(string screenName)
        {
            Assert.Equal(screenName, MainWindowTitleRedaction.RedactIfNeeded(screenName));
        }

        [Fact]
        public void RedactsAnUnknownTitle()
        {
            var result = MainWindowTitleRedaction.RedactIfNeeded("Edit Rx - John Smith - 12345");

            Assert.Equal(UiaNameRedaction.RedactedName, result);
            Assert.DoesNotContain("John Smith", result);
        }
    }

    public class MainWindowCandidateLogTests
    {
        [Fact]
        public void IncludesPidHandleClassAndTitleLengthAlways()
        {
            var candidate = new MainWindowCandidate("Edit Rx - John Smith", 24828, new IntPtr(0), "SomeClass", 0, 0, true, false);

            var line = MainWindowCandidateLog.Format(candidate);

            Assert.Contains("pid=24828", line);
            Assert.Contains("handle=0x0", line);
            Assert.Contains("class='SomeClass'", line);
            Assert.Contains($"title_len={candidate.Title.Length}", line);
            Assert.DoesNotContain("John Smith", line);
            Assert.Contains(UiaNameRedaction.RedactedName, line);
        }

        [Fact]
        public void IncludesTheLiteralTitleWhenItIsAKnownScreenName()
        {
            var candidate = new MainWindowCandidate("Run Financial Reports", 20664, new IntPtr(0xA025E), "WindowsForms10.Window.8", 1200, 800, true, false);

            var line = MainWindowCandidateLog.Format(candidate);

            Assert.Contains("Run Financial Reports", line);
        }
    }
}
