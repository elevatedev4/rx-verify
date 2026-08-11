using RxVerifyOverlay.Integrated;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for PioneerPresence (Integrated/PioneerPresence.cs) — the
/// round-3 fix's "does PioneerRx exist at all" combinator, kept separate
/// from IntegratedVisibilityGate.ShouldShowControlBox's narrower
/// "is PioneerRx the foreground app right now" question.
/// </summary>
public class PioneerPresenceTests
{
    [Fact]
    public void ExistsWhenTheNarrowRxScreenIsAttached()
    {
        Assert.True(PioneerPresence.Exists(
            isRxScreenAttached: true, hasForegroundPioneerWindow: false, hasBackgroundPioneerProcess: false));
    }

    [Fact]
    public void ExistsWhenPioneerIsTheForegroundApp()
    {
        Assert.True(PioneerPresence.Exists(
            isRxScreenAttached: false, hasForegroundPioneerWindow: true, hasBackgroundPioneerProcess: false));
    }

    [Fact]
    public void ExistsWhenOnlyTheBackgroundProcessCheckIsTrue()
    {
        // The launch/alt-tab scenario: nothing is foreground/attached,
        // but the process itself is running somewhere.
        Assert.True(PioneerPresence.Exists(
            isRxScreenAttached: false, hasForegroundPioneerWindow: false, hasBackgroundPioneerProcess: true));
    }

    [Fact]
    public void DoesNotExistWhenAllThreeSignalsAreFalse()
    {
        Assert.False(PioneerPresence.Exists(
            isRxScreenAttached: false, hasForegroundPioneerWindow: false, hasBackgroundPioneerProcess: false));
    }
}
