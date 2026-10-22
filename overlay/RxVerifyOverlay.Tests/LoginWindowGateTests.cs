using RxVerifyOverlay.Integrated;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for LoginWindowGate / LoginGateState (Integrated/LoginWindowGate.cs)
/// — the pure title matcher and show/hide state machine behind "hide the
/// overlay while PioneerRx's login window is visible". Synthetic titles only.
/// </summary>
public class LoginWindowGateTests
{
    [Theory]
    [InlineData("Logon to PioneerRx")]
    [InlineData("logon to pioneerrx")]
    [InlineData("LOGON TO PIONEERRX")]
    [InlineData("PioneerRx - Logon to PioneerRx (Test Store)")]
    [InlineData("  Logon to PioneerRx  ")]
    public void MatchesTitlesContainingTheMarkerIgnoringCase(string title)
    {
        Assert.True(LoginWindowGate.IsLoginTitle(title));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("PioneerRx")]
    [InlineData("Pre-Check Rx - 1234567 - Test, Patient")]
    [InlineData("Logon to Something Else")]
    [InlineData("Logon PioneerRx")]
    public void DoesNotMatchOtherOrEmptyTitles(string? title)
    {
        Assert.False(LoginWindowGate.IsLoginTitle(title));
    }

    [Fact]
    public void AnyLoginTitleFindsAMatchAmongManyAndToleratesNulls()
    {
        Assert.True(LoginWindowGate.AnyLoginTitle(new string?[] { null, "", "Notepad", "Logon to PioneerRx" }));
        Assert.False(LoginWindowGate.AnyLoginTitle(new string?[] { null, "", "Notepad" }));
        Assert.False(LoginWindowGate.AnyLoginTitle(System.Array.Empty<string?>()));
        Assert.False(LoginWindowGate.AnyLoginTitle(null));
    }

    [Fact]
    public void ShouldHideOnlyOnPositiveDetection()
    {
        Assert.True(LoginWindowGate.ShouldHideOverlay(true));
        Assert.False(LoginWindowGate.ShouldHideOverlay(false));
        Assert.False(LoginWindowGate.ShouldHideOverlay(null)); // detection error -> shown
    }

    [Fact]
    public void StartsShown()
    {
        Assert.False(new LoginGateState().IsHidden);
    }

    [Fact]
    public void LoginAppearsHidesThenLoginGoneRestores()
    {
        var state = new LoginGateState();

        Assert.Equal(LoginGateTransition.None, state.Observe(false));
        Assert.False(state.IsHidden);

        Assert.Equal(LoginGateTransition.Hide, state.Observe(true));
        Assert.True(state.IsHidden);

        Assert.Equal(LoginGateTransition.None, state.Observe(true));
        Assert.True(state.IsHidden);

        Assert.Equal(LoginGateTransition.Restore, state.Observe(false));
        Assert.False(state.IsHidden);
    }

    [Fact]
    public void DetectionErrorWhileHiddenRestoresTheOverlay()
    {
        var state = new LoginGateState();
        state.Observe(true);

        Assert.Equal(LoginGateTransition.Restore, state.Observe(null));
        Assert.False(state.IsHidden);
    }

    [Fact]
    public void DetectionErrorWhileShownStaysShown()
    {
        var state = new LoginGateState();

        Assert.Equal(LoginGateTransition.None, state.Observe(null));
        Assert.False(state.IsHidden);
    }

    [Fact]
    public void LoginCanReappearAfterARestore()
    {
        var state = new LoginGateState();
        state.Observe(true);
        state.Observe(false);

        Assert.Equal(LoginGateTransition.Hide, state.Observe(true));
        Assert.True(state.IsHidden);
    }

    [Theory]
    [InlineData("chrome")]
    [InlineData("Chrome")]
    [InlineData("msedge.exe")]
    [InlineData("firefox")]
    [InlineData("brave")]
    [InlineData("opera")]
    [InlineData("iexplore")]
    [InlineData("OUTLOOK.EXE")]
    public void BrowserAndMailProcessesAreExcluded(string name)
    {
        Assert.True(LoginWindowGate.IsExcludedProcessName(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("PioneerRx")]
    [InlineData("Pioneer.Client")]
    [InlineData("chromeish")]
    [InlineData("notepad")]
    public void OtherOrUnknownProcessesAreNotExcluded(string? name)
    {
        // Unknown must never exclude: a failed lookup cannot disable the feature.
        Assert.False(LoginWindowGate.IsExcludedProcessName(name));
    }

    [Fact]
    public void CandidateRequiresVisibleNotMinimizedNotCloakedAndMatchingTitle()
    {
        const string login = "Logon to PioneerRx";

        Assert.True(LoginWindowGate.IsLoginWindowCandidate(true, false, false, login));
        Assert.False(LoginWindowGate.IsLoginWindowCandidate(false, false, false, login));
        Assert.False(LoginWindowGate.IsLoginWindowCandidate(true, true, false, login)); // minimized
        Assert.False(LoginWindowGate.IsLoginWindowCandidate(true, false, true, login)); // cloaked
        Assert.False(LoginWindowGate.IsLoginWindowCandidate(true, false, false, "Notepad"));
        Assert.False(LoginWindowGate.IsLoginWindowCandidate(true, false, false, null));
    }
}
