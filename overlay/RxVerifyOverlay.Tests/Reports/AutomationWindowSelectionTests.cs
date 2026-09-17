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
}
