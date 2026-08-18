using RxVerifyOverlay.Uia;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for RxScreenModeClassifier (Uia/RxScreenMode.cs) — the pure
/// title -&gt; RxScreenMode classifier behind Integrated/PreCheckModeGate.cs
/// (branch fix/precheck-mode-gate, 2026-08-18: "RxVerify verify mode
/// should only do checks when in Pre-Check mode (from title bar), not
/// when in other modes, like Edit Rx"). Titles below mirror the confirmed
/// real shape from Uia/FieldMap.cs's own doc ("&lt;Screen Name&gt; -
/// &lt;rx number&gt; - ...") with synthetic rx numbers/drug names only.
/// </summary>
public class RxScreenModeClassifierTests
{
    // ---- Pre-Check ------------------------------------------------

    [Fact]
    public void ClassifiesConfirmedRealPreCheckTitleShape()
    {
        var mode = RxScreenModeClassifier.Classify("Pre-Check Rx - 1234567 - SYNTHETIC DRUG NAME");

        Assert.Equal(RxScreenMode.PreCheck, mode);
    }

    [Theory]
    [InlineData("PRE-CHECK RX - 1234567 - SYNTHETIC")]
    [InlineData("pre-check rx - 1234567 - synthetic")]
    [InlineData("Pre-Check Rx - 1234567 - synthetic")]
    public void PreCheckMatchIsCaseInsensitive(string title)
    {
        Assert.Equal(RxScreenMode.PreCheck, RxScreenModeClassifier.Classify(title));
    }

    [Fact]
    public void MatchesPrecheckWithoutAHyphenToo()
    {
        // Defensive tolerance in case a different Pioneer install/version
        // renders the screen name without the hyphen — see the
        // classifier's own doc for why this isn't a strict prefix match.
        var mode = RxScreenModeClassifier.Classify("PreCheck Rx - 1234567 - SYNTHETIC");

        Assert.Equal(RxScreenMode.PreCheck, mode);
    }

    [Fact]
    public void MatchesPreCheckAsASubstringNotJustAPrefix()
    {
        // Deliberately tolerant of leading/trailing text around the
        // screen name — a prior bug came from over-strict hardcoded title
        // prefixes on a real 2-Pioneer-instance workstation, so this
        // classifier (unlike PioneerRxWindow.TryAttach's own StartsWith
        // gate on FieldMap.TargetWindowTitlePrefixes) uses Contains.
        var mode = RxScreenModeClassifier.Classify("[Instance 2] Pre-Check Rx - 1234567 - SYNTHETIC");

        Assert.Equal(RxScreenMode.PreCheck, mode);
    }

    // ---- Edit Rx ----------------------------------------------------

    [Fact]
    public void ClassifiesConfirmedRealEditRxTitleShape()
    {
        var mode = RxScreenModeClassifier.Classify("Edit Rx - 1234567 - SYNTHETIC DRUG NAME");

        Assert.Equal(RxScreenMode.EditRx, mode);
    }

    [Fact]
    public void EditRxMatchIsCaseInsensitive()
    {
        Assert.Equal(RxScreenMode.EditRx, RxScreenModeClassifier.Classify("EDIT RX - 1234567 - SYNTHETIC"));
    }

    // ---- New Rx -------------------------------------------------------

    [Fact]
    public void ClassifiesNewRxTitleShape()
    {
        var mode = RxScreenModeClassifier.Classify("New Rx - SYNTHETIC");

        Assert.Equal(RxScreenMode.NewRx, mode);
    }

    // ---- Unknown / unreadable ------------------------------------------

    [Fact]
    public void NullTitleIsUnknown()
    {
        Assert.Equal(RxScreenMode.Unknown, RxScreenModeClassifier.Classify(null));
    }

    [Fact]
    public void EmptyTitleIsUnknown()
    {
        Assert.Equal(RxScreenMode.Unknown, RxScreenModeClassifier.Classify(""));
    }

    [Fact]
    public void WhitespaceOnlyTitleIsUnknown()
    {
        Assert.Equal(RxScreenMode.Unknown, RxScreenModeClassifier.Classify("   "));
    }

    [Fact]
    public void UnrelatedTitleIsUnknown()
    {
        // e.g. the Rx queue/search screen, or some other PioneerRx window
        // entirely — PioneerRxWindow.TryAttach never attaches to these in
        // practice (see FieldMap.TargetWindowTitlePrefixes), but the
        // classifier itself must still degrade safely if it ever did.
        Assert.Equal(RxScreenMode.Unknown, RxScreenModeClassifier.Classify("Rx Queue"));
    }

    [Fact]
    public void PreCheckTakesPriorityWhenATitleSomehowContainsMoreThanOnePhrase()
    {
        // Defensive ordering check — see the classifier's own doc for why
        // Pre-Check is checked first.
        var mode = RxScreenModeClassifier.Classify("Pre-Check Rx / Edit Rx - 1234567 - SYNTHETIC");

        Assert.Equal(RxScreenMode.PreCheck, mode);
    }
}
