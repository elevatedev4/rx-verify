using System;
using System.Collections.Generic;
using RxVerifyOverlay.Reports;
using Xunit;

namespace RxVerifyOverlay.Tests.Reports;

/// <summary>
/// Unit tests for Reports/UiaDiagnostics.cs — the pure formatting/capping
/// logic behind PioneerReportDriver.WriteUiaDiagnosticDump. No FlaUI/UIA/
/// Windows involved; everything is plain UiaElementSnapshot/
/// TopLevelWindowSnapshot records.
/// </summary>
public class UiaDiagnosticsTests
{
    public class FormatElementDumpTests
    {
        [Fact]
        public void SkipsElementsWithAnEmptyOrNullName()
        {
            var elements = new List<UiaElementSnapshot>
            {
                new("Button", "", "id1", "class1", 1),
                new("Button", "Run", "id2", "class2", 1)
            };

            var lines = UiaDumpFormatter.FormatElementDump(elements, maxLines: 150);

            Assert.Single(lines);
            Assert.Contains("Run", lines[0]);
        }

        [Fact]
        public void IncludesControlTypeNameIdAndClassOnEachLine()
        {
            var elements = new List<UiaElementSnapshot> { new("Button", "Run Financial Reports", "uxRun", "WindowsForms10.BUTTON", 2) };

            var lines = UiaDumpFormatter.FormatElementDump(elements, maxLines: 150);

            Assert.Single(lines);
            Assert.Contains("Button", lines[0]);
            Assert.Contains("Run Financial Reports", lines[0]);
            Assert.Contains("uxRun", lines[0]);
            Assert.Contains("WindowsForms10.BUTTON", lines[0]);
        }

        [Fact]
        public void IndentsByDepth()
        {
            var elements = new List<UiaElementSnapshot> { new("Pane", "Deep", "", "", 3) };

            var lines = UiaDumpFormatter.FormatElementDump(elements, maxLines: 150);

            var expectedIndent = new string(' ', 3 * 2);
            Assert.StartsWith(expectedIndent + "Pane", lines[0]);
        }

        [Fact]
        public void CapsAtMaxLinesAndAppendsAnOmittedCountLine()
        {
            var elements = new List<UiaElementSnapshot>();
            for (var i = 0; i < 200; i++)
            {
                elements.Add(new UiaElementSnapshot("Button", $"Item{i}", "", "", 1));
            }

            var lines = UiaDumpFormatter.FormatElementDump(elements, maxLines: 150);

            Assert.Equal(151, lines.Count); // 150 element lines + 1 summary line
            Assert.Contains("50 more", lines[^1]);
        }

        [Fact]
        public void NoOmittedLineWhenUnderTheCap()
        {
            var elements = new List<UiaElementSnapshot> { new("Button", "Run", "", "", 1) };

            var lines = UiaDumpFormatter.FormatElementDump(elements, maxLines: 150);

            Assert.Single(lines);
        }

        [Fact]
        public void EmptyListProducesNoLines()
        {
            var lines = UiaDumpFormatter.FormatElementDump(new List<UiaElementSnapshot>(), maxLines: 150);

            Assert.Empty(lines);
        }

        /// <summary>Review fix (blocker 1, PHI): value-bearing control types must never write their real Name to the dump - see UiaNameRedaction's class doc (WinForms DataGridView cells expose the cell VALUE as Name).</summary>
        [Theory]
        [InlineData("Edit")]
        [InlineData("DataItem")]
        [InlineData("Text")]
        [InlineData("Document")]
        [InlineData("DataGrid")]
        [InlineData("ListItem")]
        [InlineData("TreeItem")]
        [InlineData("Custom")]
        [InlineData("ComboBox")]
        [InlineData("Spinner")]
        [InlineData("Hyperlink")]
        [InlineData("SomeFutureControlTypeThisClassHasNeverSeen")]
        public void RedactsNameForValueBearingOrUnlistedControlTypes(string controlType)
        {
            var elements = new List<UiaElementSnapshot> { new(controlType, "123-45-6789 Jane Doe", "uxField", "class1", 1) };

            var lines = UiaDumpFormatter.FormatElementDump(elements, maxLines: 150);

            Assert.Single(lines);
            Assert.Contains("[redacted]", lines[0]);
            Assert.DoesNotContain("123-45-6789", lines[0]);
            Assert.DoesNotContain("Jane Doe", lines[0]);
        }

        [Fact]
        public void DoesNotRedactNameForAButton()
        {
            var elements = new List<UiaElementSnapshot> { new("Button", "Run Financial Reports", "uxRun", "class1", 1) };

            var lines = UiaDumpFormatter.FormatElementDump(elements, maxLines: 150);

            Assert.Single(lines);
            Assert.Contains("Run Financial Reports", lines[0]);
            Assert.DoesNotContain("[redacted]", lines[0]);
        }

        [Theory]
        [InlineData("TabItem")]
        [InlineData("MenuItem")]
        [InlineData("Window")]
        [InlineData("CheckBox")]
        public void DoesNotRedactNameForOtherKnownSafeChromeControlTypes(string controlType)
        {
            var elements = new List<UiaElementSnapshot> { new(controlType, "Financial Reports", "", "", 1) };

            var lines = UiaDumpFormatter.FormatElementDump(elements, maxLines: 150);

            Assert.Contains("Financial Reports", lines[0]);
        }

        [Fact]
        public void TruncatesASafeNameLongerThanFortyCharacters()
        {
            var longName = new string('A', 100);
            var elements = new List<UiaElementSnapshot> { new("Button", longName, "", "", 1) };

            var lines = UiaDumpFormatter.FormatElementDump(elements, maxLines: 150);

            Assert.Contains(new string('A', 40), lines[0]);
            Assert.DoesNotContain(new string('A', 41), lines[0]);
        }

        /// <summary>Round 3: the SupportedPatterns field added for the row-selection raw-view dump.</summary>
        [Fact]
        public void AppendsSupportedPatternsWhenSet()
        {
            var elements = new List<UiaElementSnapshot> { new("DataItem", "A/R Control Balance by Date Range", "", "", 1, "Invoke,SelectionItem") };

            var lines = UiaDumpFormatter.FormatElementDump(elements, maxLines: 150, e => e.Name);

            Assert.Contains("patterns='Invoke,SelectionItem'", lines[0]);
        }

        [Fact]
        public void OmitsPatternsSuffixWhenNotSet()
        {
            var elements = new List<UiaElementSnapshot> { new("Button", "Run", "", "", 1) };

            var lines = UiaDumpFormatter.FormatElementDump(elements, maxLines: 150);

            Assert.DoesNotContain("patterns=", lines[0]);
        }
    }

    /// <summary>Round 3: the redactor-overload used by PioneerReportDriver's row-selection raw-view dump (a DIFFERENT allowlist than UiaNameRedaction's control-type one — see ReportRowNameRedaction).</summary>
    public class FormatElementDumpWithCustomRedactorTests
    {
        [Fact]
        public void UsesTheSuppliedRedactorInsteadOfUiaNameRedaction()
        {
            // An Edit control would normally be redacted by UiaNameRedaction
            // (value-bearing) - the custom redactor here says otherwise.
            var elements = new List<UiaElementSnapshot> { new("Edit", "Inventory Valuation", "", "", 1) };

            var lines = UiaDumpFormatter.FormatElementDump(elements, maxLines: 150, e => e.Name);

            Assert.Contains("Inventory Valuation", lines[0]);
        }

        [Fact]
        public void StillRespectsTheLineCapAndOmittedCountLine()
        {
            var elements = new List<UiaElementSnapshot>();
            for (var i = 0; i < 5; i++)
            {
                elements.Add(new UiaElementSnapshot("DataItem", $"Row{i}", "", "", 1));
            }

            var lines = UiaDumpFormatter.FormatElementDump(elements, maxLines: 3, e => "[redacted]");

            Assert.Equal(4, lines.Count);
            Assert.Contains("2 more", lines[^1]);
        }
    }

    /// <summary>Direct tests of UiaNameRedaction itself, independent of FormatElementDump's line assembly.</summary>
    public class UiaNameRedactionTests
    {
        [Fact]
        public void IsNameSafeToLog_TrueForButton()
        {
            Assert.True(UiaNameRedaction.IsNameSafeToLog("Button"));
        }

        [Fact]
        public void IsNameSafeToLog_IsCaseInsensitive()
        {
            Assert.True(UiaNameRedaction.IsNameSafeToLog("button"));
        }

        [Fact]
        public void IsNameSafeToLog_FalseForEdit()
        {
            Assert.False(UiaNameRedaction.IsNameSafeToLog("Edit"));
        }

        [Fact]
        public void IsNameSafeToLog_FalseForNullOrEmpty()
        {
            Assert.False(UiaNameRedaction.IsNameSafeToLog(null));
            Assert.False(UiaNameRedaction.IsNameSafeToLog(""));
        }

        [Fact]
        public void RedactIfNeeded_ReturnsThePlaceholderForEdit()
        {
            Assert.Equal(UiaNameRedaction.RedactedName, UiaNameRedaction.RedactIfNeeded("Edit", "some entered value"));
        }

        [Fact]
        public void RedactIfNeeded_ReturnsTheRealNameForButton()
        {
            Assert.Equal("Run", UiaNameRedaction.RedactIfNeeded("Button", "Run"));
        }

        [Fact]
        public void RedactIfNeeded_TruncatesALongSafeNameToFortyChars()
        {
            var longName = new string('B', 60);

            var result = UiaNameRedaction.RedactIfNeeded("Button", longName);

            Assert.Equal(40, result.Length);
        }
    }

    public class FormatTopLevelWindowListTests
    {
        [Fact]
        public void FormatsTitleAndClassForEachWindow()
        {
            var windows = new List<TopLevelWindowSnapshot> { new("PioneerRx - Main", "WindowsForms10.Window.8") };

            var lines = UiaDumpFormatter.FormatTopLevelWindowList(windows);

            Assert.Single(lines);
            Assert.Contains("PioneerRx - Main", lines[0]);
            Assert.Contains("WindowsForms10.Window.8", lines[0]);
        }

        [Fact]
        public void FallsBackToPlaceholdersForBlankTitleOrClass()
        {
            var windows = new List<TopLevelWindowSnapshot> { new("", "") };

            var lines = UiaDumpFormatter.FormatTopLevelWindowList(windows);

            Assert.Contains("<untitled>", lines[0]);
            Assert.Contains("<unknown class>", lines[0]);
        }
    }

    public class FormatMainWindowHeaderTests
    {
        [Fact]
        public void IncludesNameClassPidAndHandle()
        {
            var line = UiaDumpFormatter.FormatMainWindowHeader("PioneerRx - Main", "WindowsForms10.Window.8", 4321, new IntPtr(0xABCD));

            Assert.Contains("PioneerRx - Main", line);
            Assert.Contains("WindowsForms10.Window.8", line);
            Assert.Contains("4321", line);
            Assert.Contains("ABCD", line);
        }

        [Fact]
        public void FallsBackToPlaceholdersForBlankNameOrClass()
        {
            var line = UiaDumpFormatter.FormatMainWindowHeader("", "", 0, IntPtr.Zero);

            Assert.Contains("<untitled>", line);
            Assert.Contains("<unknown class>", line);
        }
    }
}
