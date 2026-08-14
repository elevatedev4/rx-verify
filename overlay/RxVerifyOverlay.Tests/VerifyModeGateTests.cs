using RxVerifyOverlay.Integrated;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for VerifyModeGate — the owner's Order/Verify mode
/// EXCLUSIVITY spec ("activating 'Order mode' instead of Verify mode").
/// </summary>
public class VerifyModeGateTests
{
    [Fact]
    public void SuppressesVerifyBoxesWhenOrderAssistIsEnabled()
    {
        Assert.True(VerifyModeGate.ShouldSuppressVerifyBoxes(orderAssistEnabled: true));
    }

    [Fact]
    public void DoesNotSuppressVerifyBoxesWhenOrderAssistIsDisabled()
    {
        Assert.False(VerifyModeGate.ShouldSuppressVerifyBoxes(orderAssistEnabled: false));
    }
}
