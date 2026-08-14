using RxVerifyOverlay.OrderAssist;
using Xunit;

namespace RxVerifyOverlay.Tests.OrderAssist;

/// <summary>
/// Unit tests for TickGenerationGate — the pure decision behind
/// OrderAssistCoordinator's fix for the "disabling mid-tick can re-show a
/// stale highlight" race (see that class's own doc): a tick's captured
/// generation must match the CURRENT generation, checked again
/// immediately before ever touching the overlay's visible state.
/// </summary>
public class TickGenerationGateTests
{
    [Fact]
    public void SameGenerationIsStillCurrent()
    {
        Assert.True(TickGenerationGate.IsStillCurrent(capturedGeneration: 3, currentGeneration: 3));
    }

    [Fact]
    public void ACurrentGenerationAheadOfTheCapturedOneIsNoLongerCurrent()
    {
        // The real-world case: SetEnabled ran (bumping the generation)
        // while a tick captured at generation 3 was still awaiting its
        // capture/OCR pass.
        Assert.False(TickGenerationGate.IsStillCurrent(capturedGeneration: 3, currentGeneration: 4));
    }

    [Fact]
    public void MultipleEnableDisableCyclesInBetweenAreAlsoNotCurrent()
    {
        Assert.False(TickGenerationGate.IsStillCurrent(capturedGeneration: 3, currentGeneration: 6));
    }

    [Fact]
    public void ACapturedGenerationAheadOfCurrentFailsClosedToo()
    {
        // Should never happen in practice (a generation only ever
        // increases), but the gate must fail CLOSED (never show) rather
        // than assume "not behind" means "still fine".
        Assert.False(TickGenerationGate.IsStillCurrent(capturedGeneration: 5, currentGeneration: 4));
    }
}
