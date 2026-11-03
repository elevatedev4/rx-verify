using System.Text.Json;
using RxVerifyOverlay.Models;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>An auto-switched Order mode must not be what an incidental Save() persists (OverlaySettings.OrderAssistPersistedOverride).</summary>
public class OverlaySettingsAutoOrderModePersistTests
{
    private static bool PersistedOrderAssist(OverlaySettings settings)
    {
        using var doc = JsonDocument.Parse(settings.ToJsonForSave());
        return doc.RootElement.GetProperty("OrderAssistEnabled").GetBoolean();
    }

    [Fact]
    public void WithoutAnOverrideTheLiveValueIsPersisted()
    {
        var settings = new OverlaySettings { OrderAssistEnabled = true };
        Assert.True(PersistedOrderAssist(settings));
    }

    [Fact]
    public void OverrideIsWrittenInsteadOfTheLiveValueAndLiveValueIsRestored()
    {
        var settings = new OverlaySettings { OrderAssistEnabled = true, OrderAssistPersistedOverride = false };

        Assert.False(PersistedOrderAssist(settings));
        Assert.True(settings.OrderAssistEnabled);
    }

    [Fact]
    public void OverrideItselfIsNeverSerialized()
    {
        var settings = new OverlaySettings { OrderAssistEnabled = true, OrderAssistPersistedOverride = false };
        Assert.DoesNotContain("OrderAssistPersistedOverride", settings.ToJsonForSave());
    }
}
