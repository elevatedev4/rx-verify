using RxVerifyOverlay.Integrated;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for RxIdentityGate (Integrated/RxIdentityGate.cs) — the
/// pure staleness check behind the "immediate clear on Rx switch" fix
/// (round 4 addendum, item 7).
/// </summary>
public class RxIdentityGateTests
{
    [Fact]
    public void SameIdentityIsNotStale()
    {
        Assert.False(RxIdentityGate.IsStale("1234567", "1234567"));
    }

    [Fact]
    public void DifferentIdentitiesAreStale()
    {
        // THE CORE FIX: PioneerRx just switched to a different Rx number
        // than the one the displayed boxes were verified against.
        Assert.True(RxIdentityGate.IsStale("7654321", "1234567"));
    }

    [Fact]
    public void BothNullIsNotStale()
    {
        // Nothing attached, nothing ever verified — covered by the other
        // visibility gates (isAttached/hasVerifiableContent), not this one.
        Assert.False(RxIdentityGate.IsStale(null, null));
    }

    [Fact]
    public void CurrentKnownWithNoVerdictsYetIsStale()
    {
        // PioneerRx is showing a real Rx, but nothing has been verified
        // for it yet (e.g. verdicts were just cleared) — must not show
        // boxes for a DIFFERENT (or no) Rx's stale verdicts.
        Assert.True(RxIdentityGate.IsStale("1234567", null));
    }

    [Fact]
    public void VerdictsKnownWithNoCurrentAttachIsStale()
    {
        Assert.True(RxIdentityGate.IsStale(null, "1234567"));
    }
}
