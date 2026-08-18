using RxVerifyOverlay.Integrated;
using RxVerifyOverlay.Uia;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for PreCheckModeGate (Integrated/PreCheckModeGate.cs) — the
/// pure decision behind Will's 2026-08-18 ask, verbatim: "RxVerify verify
/// mode should only do checks when in Pre-Check mode (from title bar), not
/// when in other modes, like Edit Rx".
/// </summary>
public class PreCheckModeGateTests
{
    [Fact]
    public void RunsVerifyChecksInPreCheckMode()
    {
        Assert.True(PreCheckModeGate.ShouldRunVerifyChecks(RxScreenMode.PreCheck));
    }

    [Fact]
    public void DoesNotRunVerifyChecksInEditRxMode()
    {
        Assert.False(PreCheckModeGate.ShouldRunVerifyChecks(RxScreenMode.EditRx));
    }

    [Fact]
    public void DoesNotRunVerifyChecksInNewRxMode()
    {
        Assert.False(PreCheckModeGate.ShouldRunVerifyChecks(RxScreenMode.NewRx));
    }

    [Fact]
    public void DefaultsToActiveWhenModeIsUnknown()
    {
        // Deliberate choice: an unreadable/unclassifiable title (the only
        // realistic way Unknown happens once a window is actually
        // attached — see RxScreenMode's own doc) defaults to ACTIVE, not
        // suppressed, so a transient UIA read hiccup can never look like
        // the whole app silently breaking on a legitimate Pre-Check
        // screen.
        Assert.True(PreCheckModeGate.ShouldRunVerifyChecks(RxScreenMode.Unknown));
    }
}
