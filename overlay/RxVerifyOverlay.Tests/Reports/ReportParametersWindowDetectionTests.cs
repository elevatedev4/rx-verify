using System;
using System.Collections.Generic;
using RxVerifyOverlay.Reports;
using Xunit;

namespace RxVerifyOverlay.Tests.Reports;

/// <summary>
/// Unit tests for Reports/ReportParametersWindowDetection.cs — the two
/// pure decisions behind PioneerReportDriver's W-T92 round 6 fix (Will's
/// build da39140 run: the "Report Parameters" popup opened but the old
/// title match never recognized it). No FlaUI/UIA/Windows involved —
/// everything is plain IntPtr/NativeWindowSnapshot values and int process
/// ids. These same assertions were also run in an isolated scratch net8.0
/// xunit project (this Mac has no Windows test host to run the real
/// net8.0-windows10.0.19041.0 RxVerifyOverlay.Tests project against).
/// </summary>
public class ReportParametersWindowDetectionTests
{
    private static readonly IntPtr Main = new(100);
    private static readonly IntPtr Popup = new(200);
    private static readonly IntPtr OtherPopup = new(300);

    public class NewWindowDetectorTests
    {
        [Fact]
        public void DetectReturnsNoneWhenNoNewHandleAppeared()
        {
            var before = new HashSet<IntPtr> { Main };
            var after = new List<NativeWindowSnapshot>
            {
                new(Main, "WindowsForms10.Window.8", 12, 800, 600, IntPtr.Zero)
            };

            var result = NewWindowDetector.Detect(before, after);

            Assert.Equal(NewWindowDetectionResult.None, result);
        }

        [Fact]
        public void DetectReturnsNewWindowAppearedForAHandleNotInBefore()
        {
            var before = new HashSet<IntPtr> { Main };
            var after = new List<NativeWindowSnapshot>
            {
                new(Main, "WindowsForms10.Window.8", 12, 800, 600, IntPtr.Zero),
                new(Popup, "WindowsForms10.Window.8", 0, 400, 300, Main)
            };

            var result = NewWindowDetector.Detect(before, after);

            Assert.Equal(NewWindowDetectionResult.NewWindowAppeared, result);
        }

        [Fact]
        public void DetectIsTitleAgnostic_ZeroLengthTitleStillCountsAsNew()
        {
            // GOAL brief step 1: the whole point of this fix — Will's build
            // da39140 run had the popup open with a title UIA either
            // exposed as empty or never picked up at all. TitleLength=0
            // must not stop detection.
            var before = new HashSet<IntPtr> { Main };
            var after = new List<NativeWindowSnapshot>
            {
                new(Popup, "#32770", 0, 400, 300, Main)
            };

            var result = NewWindowDetector.Detect(before, after);

            Assert.Equal(NewWindowDetectionResult.NewWindowAppeared, result);
        }

        [Fact]
        public void NewWindowsExcludesZeroHandles()
        {
            var before = new HashSet<IntPtr>();
            var after = new List<NativeWindowSnapshot>
            {
                new(IntPtr.Zero, "SomeGhostWindow", 0, 0, 0, IntPtr.Zero)
            };

            var newWindows = NewWindowDetector.NewWindows(before, after);

            Assert.Empty(newWindows);
        }

        [Fact]
        public void NewWindowsReturnsEveryHandleNotInBefore_InOrder()
        {
            var before = new HashSet<IntPtr> { Main };
            var after = new List<NativeWindowSnapshot>
            {
                new(Main, "Main", 12, 800, 600, IntPtr.Zero),
                new(Popup, "Popup", 0, 400, 300, Main),
                new(OtherPopup, "OtherPopup", 5, 200, 100, Main)
            };

            var newWindows = NewWindowDetector.NewWindows(before, after);

            Assert.Equal(2, newWindows.Count);
            Assert.Equal(Popup, newWindows[0].Handle);
            Assert.Equal(OtherPopup, newWindows[1].Handle);
        }

        [Fact]
        public void NewWindowsReturnsEmptyWhenAfterIsEmpty()
        {
            var before = new HashSet<IntPtr> { Main };
            var after = new List<NativeWindowSnapshot>();

            var newWindows = NewWindowDetector.NewWindows(before, after);

            Assert.Empty(newWindows);
        }

        // --- Review fix (BLOCKING, round 6 review): a transient window
        // (tooltip/dropdown/IME/tiny chrome) must never count as "the row
        // opened" - that would reproduce the exact original title-match
        // false positive, just for a different reason. ---

        [Fact]
        public void DetectIgnoresATooltipClassWindow()
        {
            var before = new HashSet<IntPtr> { Main };
            var after = new List<NativeWindowSnapshot>
            {
                new(Popup, "tooltips_class32", 0, 200, 40, Main)
            };

            var result = NewWindowDetector.Detect(before, after);

            Assert.Equal(NewWindowDetectionResult.None, result);
            Assert.Empty(NewWindowDetector.NewWindows(before, after));
        }

        [Fact]
        public void DetectIgnoresATinyWindow()
        {
            var before = new HashSet<IntPtr> { Main };
            var after = new List<NativeWindowSnapshot>
            {
                new(Popup, "WindowsForms10.Window.8", 0, 20, 20, Main)
            };

            var result = NewWindowDetector.Detect(before, after);

            Assert.Equal(NewWindowDetectionResult.None, result);
            Assert.Empty(NewWindowDetector.NewWindows(before, after));
        }

        [Fact]
        public void NewWindowsSelectsTheRealPopupOverAnAccompanyingTooltip()
        {
            var before = new HashSet<IntPtr> { Main };
            var after = new List<NativeWindowSnapshot>
            {
                new(Popup, "WindowsForms10.Window.8", 0, 600, 400, Main),
                new(OtherPopup, "tooltips_class32", 0, 180, 32, Main)
            };

            var result = NewWindowDetector.Detect(before, after);
            var newWindows = NewWindowDetector.NewWindows(before, after);

            Assert.Equal(NewWindowDetectionResult.NewWindowAppeared, result);
            Assert.Single(newWindows);
            Assert.Equal(Popup, newWindows[0].Handle);
        }

        [Fact]
        public void NewWindowsPrefersTheLargerOfTwoRealNewWindows()
        {
            var smaller = new IntPtr(400);
            var before = new HashSet<IntPtr> { Main };
            var after = new List<NativeWindowSnapshot>
            {
                new(smaller, "WindowsForms10.Window.8", 0, 300, 200, Main),
                new(Popup, "WindowsForms10.Window.8", 0, 600, 400, Main)
            };

            var newWindows = NewWindowDetector.NewWindows(before, after);

            Assert.Equal(2, newWindows.Count);
            Assert.Equal(Popup, newWindows[0].Handle);
            Assert.Equal(smaller, newWindows[1].Handle);
        }

        [Theory]
        [InlineData("Auto-Suggest Dropdown")]
        [InlineData("ComboLBox")]
        [InlineData("MSCTFIME UI")]
        [InlineData("BroadcastEventWindow.4.0")]
        [InlineData("GDI+ Hook Window Class")]
        public void DetectIgnoresEveryKnownTransientClassSubstring(string className)
        {
            var before = new HashSet<IntPtr> { Main };
            var after = new List<NativeWindowSnapshot>
            {
                new(Popup, className, 0, 400, 300, Main)
            };

            Assert.Equal(NewWindowDetectionResult.None, NewWindowDetector.Detect(before, after));
        }

        [Fact]
        public void IgnoredTransientWindowsReturnsOnlyTheFilteredOnes()
        {
            var before = new HashSet<IntPtr> { Main };
            var after = new List<NativeWindowSnapshot>
            {
                new(Main, "WindowsForms10.Window.8", 12, 800, 600, IntPtr.Zero),
                new(Popup, "WindowsForms10.Window.8", 0, 600, 400, Main),
                new(OtherPopup, "tooltips_class32", 0, 180, 32, Main)
            };

            var ignored = NewWindowDetector.IgnoredTransientWindows(before, after);

            Assert.Single(ignored);
            Assert.Equal(OtherPopup, ignored[0].Handle);
        }
    }

    public class TransientWindowFilterTests
    {
        [Theory]
        [InlineData("tooltips_class32")]
        [InlineData("Auto-Suggest Dropdown")]
        [InlineData("ComboLBox")]
        [InlineData("MSCTFIME UI")]
        [InlineData("IME Window")]
        [InlineData("BroadcastEventWindow.4.0")]
        [InlineData("GDI+ Hook Window Class")]
        public void IsTransientTrueForKnownChromeClassesRegardlessOfCase(string className)
        {
            var window = new NativeWindowSnapshot(new IntPtr(200), className.ToUpperInvariant(), 0, 400, 300, IntPtr.Zero);

            Assert.True(TransientWindowFilter.IsTransient(window));
        }

        [Theory]
        [InlineData(99, 300)]
        [InlineData(300, 99)]
        [InlineData(0, 0)]
        public void IsTransientTrueBelowTheMinimumDimension(int width, int height)
        {
            var window = new NativeWindowSnapshot(new IntPtr(200), "WindowsForms10.Window.8", 0, width, height, IntPtr.Zero);

            Assert.True(TransientWindowFilter.IsTransient(window));
        }

        [Fact]
        public void IsTransientFalseForARealSizedDialogWithAnOrdinaryClass()
        {
            var window = new NativeWindowSnapshot(new IntPtr(200), "WindowsForms10.Window.8", 0, 600, 400, IntPtr.Zero);

            Assert.False(TransientWindowFilter.IsTransient(window));
        }

        [Fact]
        public void IsTransientFalseExactlyAtTheMinimumDimension()
        {
            var window = new NativeWindowSnapshot(new IntPtr(200), "WindowsForms10.Window.8", 0, 100, 100, IntPtr.Zero);

            Assert.False(TransientWindowFilter.IsTransient(window));
        }
    }

    public class NativeWindowSnapshotLogTests
    {
        [Fact]
        public void FormatNeverIncludesATitle()
        {
            var window = new NativeWindowSnapshot(new IntPtr(200), "WindowsForms10.Window.8", 37, 400, 300, new IntPtr(100));

            var line = NativeWindowSnapshotLog.Format(window);

            Assert.Contains("class='WindowsForms10.Window.8'", line);
            Assert.Contains("title_len=37", line);
            Assert.Contains("size=400x300", line);
            Assert.Contains("owner=0x64", line);
            Assert.Contains("handle=0xC8", line);
            // The whole point of TitleLength-only logging (GOAL brief step
            // 1: "never titles, they can contain patient data") — the
            // formatted line can never contain a title= field with text.
            Assert.DoesNotContain("title='", line);
        }

        [Fact]
        public void FormatIgnoredTransientMatchesTheRequestedShape()
        {
            var window = new NativeWindowSnapshot(new IntPtr(200), "tooltips_class32", 0, 180, 32, new IntPtr(100));

            var line = NativeWindowSnapshotLog.FormatIgnoredTransient(window);

            Assert.Equal("ignored transient: tooltips_class32 (180x32)", line);
        }
    }

    public class ForegroundOwnershipRuleTests
    {
        [Fact]
        public void SameProcessIdStillCountsAsFocused()
        {
            // GOAL brief step 2: "any window of the Pioneer PID (main
            // window, the popup, its dialogs) counts as focused" — the
            // popup is a DIFFERENT window but the SAME process.
            Assert.True(ForegroundOwnershipRule.IsStillFocused(foregroundProcessId: 4242, pioneerProcessId: 4242));
        }

        [Fact]
        public void DifferentProcessIdIsNotFocused()
        {
            Assert.False(ForegroundOwnershipRule.IsStillFocused(foregroundProcessId: 9999, pioneerProcessId: 4242));
        }

        [Fact]
        public void UnknownPioneerProcessIdIsNeverConsideredFocused()
        {
            // _pioneerProcessId is 0 before FindMainWindow ever sets it —
            // must never accidentally match a foreground pid of 0 too.
            Assert.False(ForegroundOwnershipRule.IsStillFocused(foregroundProcessId: 0, pioneerProcessId: 0));
        }

        [Fact]
        public void ZeroForegroundProcessIdIsNotFocused()
        {
            Assert.False(ForegroundOwnershipRule.IsStillFocused(foregroundProcessId: 0, pioneerProcessId: 4242));
        }
    }
}
