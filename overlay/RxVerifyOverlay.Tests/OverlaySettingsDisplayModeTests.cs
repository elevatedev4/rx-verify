using System.Text.Json;
using RxVerifyOverlay.Models;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for OverlaySettings.DisplayMode (see Models/
/// OverlaySettings.cs DisplayMode) — the Separate/Integrated toggle added
/// for INTEGRATED DISPLAY MODE. Pure in-memory JSON round-trip checks, no
/// filesystem/UIA/engine/PHI involved — mirrors the existing
/// OverlaySettingsVerificationMethodTests.cs pattern for Method exactly.
/// </summary>
public class OverlaySettingsDisplayModeTests
{
    [Fact]
    public void DefaultsToSeparate()
    {
        var settings = new OverlaySettings();
        Assert.Equal(DisplayMode.Separate, settings.DisplayMode);
    }

    [Fact]
    public void SerializesDisplayModeAsAReadableString()
    {
        var settings = new OverlaySettings { DisplayMode = DisplayMode.Integrated };

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });

        Assert.Contains("\"DisplayMode\": \"Integrated\"", json);
    }

    [Fact]
    public void RoundTripsThroughJson()
    {
        var original = new OverlaySettings { DisplayMode = DisplayMode.Integrated };
        var json = JsonSerializer.Serialize(original);

        var restored = JsonSerializer.Deserialize<OverlaySettings>(json);

        Assert.NotNull(restored);
        Assert.Equal(DisplayMode.Integrated, restored!.DisplayMode);
    }

    [Fact]
    public void OldSettingsJsonWithoutDisplayModeKeyDefaultsToSeparate()
    {
        // Mirrors a real settings.json written before this field existed
        // — an installation upgrading mid-shift must never silently land
        // in Integrated mode it never opted into.
        const string legacyJson = "{\"Method\":\"Ocr\",\"EngineCliPath\":\"C:\\\\dist\\\\cli.js\",\"NodeExecutable\":\"node\"}";

        var restored = JsonSerializer.Deserialize<OverlaySettings>(legacyJson);

        Assert.NotNull(restored);
        Assert.Equal(DisplayMode.Separate, restored!.DisplayMode);
    }
}
