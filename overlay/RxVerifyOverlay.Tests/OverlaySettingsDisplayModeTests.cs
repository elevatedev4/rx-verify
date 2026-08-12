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
    public void DefaultsToIntegrated()
    {
        // Flipped 2026-08-12: the owner uses Integrated daily and asked
        // for it to be what a fresh install/machine starts in (see
        // Models/OverlaySettings.cs DisplayMode doc).
        var settings = new OverlaySettings();
        Assert.Equal(DisplayMode.Integrated, settings.DisplayMode);
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
    public void ExplicitSeparateChoiceSurvivesTheDefaultBeingIntegrated()
    {
        // The default flipping to Integrated must never mean a pharmacist
        // who deliberately chose Separate gets overridden back to
        // Integrated on next load — an explicit "DisplayMode":"Separate"
        // key in settings.json always wins over whatever the class-level
        // default is.
        const string json = "{\"DisplayMode\":\"Separate\",\"Method\":\"Ocr\"}";

        var restored = JsonSerializer.Deserialize<OverlaySettings>(json);

        Assert.NotNull(restored);
        Assert.Equal(DisplayMode.Separate, restored!.DisplayMode);
    }

    [Fact]
    public void OldSettingsJsonWithoutDisplayModeKeyDefaultsToIntegrated()
    {
        // Mirrors a real settings.json written before this field existed
        // — such a file predates ever having made a Separate/Integrated
        // choice at all, so it now lands on today's default (Integrated,
        // flipped 2026-08-12) the same as a brand-new settings.json would.
        // A settings.json that DOES contain an explicit "DisplayMode" key
        // (i.e. the pharmacist already made a choice, see
        // RoundTripsThroughJson above) is unaffected either way — this
        // test is only about the missing-key case.
        const string legacyJson = "{\"Method\":\"Ocr\",\"EngineCliPath\":\"C:\\\\dist\\\\cli.js\",\"NodeExecutable\":\"node\"}";

        var restored = JsonSerializer.Deserialize<OverlaySettings>(legacyJson);

        Assert.NotNull(restored);
        Assert.Equal(DisplayMode.Integrated, restored!.DisplayMode);
    }
}
