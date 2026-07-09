using System.Text.Json;
using RxVerifyOverlay.Models;
using Xunit;

namespace RxVerifyOverlay.Tests;

/// <summary>
/// Unit tests for OverlaySettings.Method (see Models/OverlaySettings.cs
/// VerificationMethod) — the runtime-selectable OCR-vs-Escript-tab
/// toggle added to combine "Verify" and "VerifyOCR" into one app. Pure
/// in-memory JSON round-trip checks, no filesystem/UIA/engine/PHI
/// involved.
/// </summary>
public class OverlaySettingsVerificationMethodTests
{
    [Fact]
    public void DefaultsToOcr()
    {
        var settings = new OverlaySettings();
        Assert.Equal(VerificationMethod.Ocr, settings.Method);
    }

    [Fact]
    public void SerializesMethodAsAReadableString()
    {
        var settings = new OverlaySettings { Method = VerificationMethod.Uia };

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });

        Assert.Contains("\"Method\": \"Uia\"", json);
    }

    [Fact]
    public void RoundTripsThroughJson()
    {
        var original = new OverlaySettings { Method = VerificationMethod.Uia };
        var json = JsonSerializer.Serialize(original);

        var restored = JsonSerializer.Deserialize<OverlaySettings>(json);

        Assert.NotNull(restored);
        Assert.Equal(VerificationMethod.Uia, restored!.Method);
    }

    [Fact]
    public void OldSettingsJsonWithoutMethodKeyDefaultsToOcr()
    {
        // Mirrors a real settings.json written before this field existed
        // — just EngineCliPath/NodeExecutable, no "Method" key at all.
        const string legacyJson = "{\"EngineCliPath\":\"C:\\\\dist\\\\cli.js\",\"NodeExecutable\":\"node\"}";

        var restored = JsonSerializer.Deserialize<OverlaySettings>(legacyJson);

        Assert.NotNull(restored);
        Assert.Equal(VerificationMethod.Ocr, restored!.Method);
    }
}
