using System.Text.Json;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.OrderAssist.Ocr;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>Runtime fallback setting for the row normalizer, and persistence of automatic Order/Verify switches.</summary>
public class OverlaySettingsRowNormalizerAndAutoSwitchTests
{
    private static bool PersistedOrderAssist(OverlaySettings settings)
    {
        using var doc = JsonDocument.Parse(settings.ToJsonForSave());
        return doc.RootElement.GetProperty("OrderAssistEnabled").GetBoolean();
    }

    [Fact]
    public void RowNormalizerDefaultsToLocal()
    {
        Assert.Equal("local", new OverlaySettings().OrderAssistRowNormalizer);
        Assert.Equal(RowNormalizerMode.Local, RowNormalizerModes.Resolve(new OverlaySettings().OrderAssistRowNormalizer));
    }

    [Theory]
    [InlineData("legacy", RowNormalizerMode.Legacy)]
    [InlineData("LEGACY", RowNormalizerMode.Legacy)]
    [InlineData("  Legacy ", RowNormalizerMode.Legacy)]
    [InlineData("local", RowNormalizerMode.Local)]
    [InlineData("", RowNormalizerMode.Local)]
    [InlineData(null, RowNormalizerMode.Local)]
    [InlineData("legcy", RowNormalizerMode.Local)] // typo -> default, never an unintended path
    public void ResolveMapsTheSettingToAMode(string? setting, RowNormalizerMode expected)
    {
        Assert.Equal(expected, RowNormalizerModes.Resolve(setting));
    }

    [Fact]
    public void LegacyValueRoundTripsThroughSettingsJson()
    {
        var loaded = JsonSerializer.Deserialize<OverlaySettings>("{\"OrderAssistRowNormalizer\":\"legacy\"}");
        Assert.NotNull(loaded);
        Assert.Equal(RowNormalizerMode.Legacy, RowNormalizerModes.Resolve(loaded!.OrderAssistRowNormalizer));

        var oldFile = JsonSerializer.Deserialize<OverlaySettings>("{}");
        Assert.Equal("local", oldFile!.OrderAssistRowNormalizer);
    }

    [Fact]
    public void AutoSwitchToOrderIsNotPersisted()
    {
        var settings = new OverlaySettings { OrderAssistEnabled = false };

        var saveNow = settings.ApplyAutoOrderModeSwitch(toOrder: true);

        Assert.False(saveNow);
        Assert.True(settings.OrderAssistEnabled);
        Assert.False(PersistedOrderAssist(settings)); // an incidental Save() still writes Verify
    }

    [Fact]
    public void AutoRevertOfAManualOrderPersistsVerifySoTheAppDoesNotRelaunchInOrder()
    {
        // Manual Order pick was saved as true. The order screen opens (no
        // switch, already Order) and closes -> auto-revert to Verify.
        var settings = new OverlaySettings { OrderAssistEnabled = true };

        var saveNow = settings.ApplyAutoOrderModeSwitch(toOrder: false);

        Assert.True(saveNow);
        Assert.False(settings.OrderAssistEnabled);
        Assert.False(PersistedOrderAssist(settings)); // what Save() writes now
        Assert.Null(settings.OrderAssistPersistedOverride);
    }

    [Fact]
    public void AutoOrderThenAutoRevertEndsPersistedAsVerify()
    {
        var settings = new OverlaySettings { OrderAssistEnabled = false };
        settings.ApplyAutoOrderModeSwitch(toOrder: true);
        var saveNow = settings.ApplyAutoOrderModeSwitch(toOrder: false);

        Assert.True(saveNow);
        Assert.False(PersistedOrderAssist(settings));
    }

    [Fact]
    public void ManualOrderPickWithNoAutoRevertStaysPersistedAsOrder()
    {
        var settings = new OverlaySettings();
        // MainWindow's dropdown handler: live = true, override cleared, then Save().
        settings.OrderAssistEnabled = true;
        settings.OrderAssistPersistedOverride = null;

        Assert.True(PersistedOrderAssist(settings));
    }
}
