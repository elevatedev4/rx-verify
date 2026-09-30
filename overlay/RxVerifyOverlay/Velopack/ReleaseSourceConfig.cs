using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

// Namespace is "VelopackIntegration", not "Velopack" — this folder is
// named Velopack/ (matching the approved plan's file paths, mirroring
// vaccine-assist's own layout), but a C# namespace segment literally
// named "Velopack" nested under RxVerifyOverlay would SHADOW the Velopack
// NuGet package's own root "Velopack" namespace for every file anywhere
// under RxVerifyOverlay.* (including Program.cs and App.xaml.cs), because
// unqualified name lookup finds "RxVerifyOverlay.Velopack" as a member of
// the enclosing "RxVerifyOverlay" namespace before it ever considers the
// global "Velopack" — breaking `using Velopack;`/`Velopack.VelopackApp`
// everywhere, not just in this folder. Same reason applies to
// S3UpdateSource.cs and VelopackUpdater.cs.
namespace RxVerifyOverlay.VelopackIntegration;

/// <summary>
/// Read-only S3 credentials/location for the Velopack update channel,
/// mirroring vaccine-assist's own G-Q5 installer channel (private bucket,
/// read-only IAM key embedded in the app). Loaded from
/// Velopack/release-source.json — deliberately NOT committed (see this
/// repo's .gitignore and the committed release-source.example.json
/// sibling with placeholders) since it carries a live AWS access key,
/// even a read-only one.
///
/// Worst case if this key is extracted from the unsigned exe: someone can
/// read the installer files from the release bucket. Accepted risk at
/// this stage, same as vaccine-assist's equivalent channel.
/// </summary>
public sealed class ReleaseSourceConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [JsonPropertyName("bucket")]
    public string Bucket { get; set; } = "";

    [JsonPropertyName("region")]
    public string Region { get; set; } = "";

    [JsonPropertyName("prefix")]
    public string Prefix { get; set; } = "";

    [JsonPropertyName("accessKeyId")]
    public string AccessKeyId { get; set; } = "";

    [JsonPropertyName("secretAccessKey")]
    public string SecretAccessKey { get; set; } = "";

    /// <summary>
    /// Loads and validates the config at <paramref name="path"/>. Returns
    /// null (never throws) for every failure mode — missing file, invalid
    /// JSON, or a file that parses but is missing a required field — since
    /// every caller's contract is "no config means no update checks,"
    /// never a startup crash. <paramref name="log"/> is called exactly
    /// once per failure reason so a workstation with no/bad config is
    /// diagnosable from the overlay's own log without ever surfacing to
    /// the user.
    /// </summary>
    public static ReleaseSourceConfig? Load(string path, Action<string> log)
    {
        if (!File.Exists(path))
        {
            log($"[ReleaseSourceConfig] no config at '{path}' — Velopack update checks are disabled for this build.");
            return null;
        }

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            log($"[ReleaseSourceConfig] could not read '{path}': {ex.GetType().Name}: {ex.Message} — Velopack update checks are disabled.");
            return null;
        }

        ReleaseSourceConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<ReleaseSourceConfig>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            log($"[ReleaseSourceConfig] malformed JSON in '{path}': {ex.Message} — Velopack update checks are disabled.");
            return null;
        }

        if (config is null ||
            string.IsNullOrWhiteSpace(config.Bucket) ||
            string.IsNullOrWhiteSpace(config.Region) ||
            string.IsNullOrWhiteSpace(config.AccessKeyId) ||
            string.IsNullOrWhiteSpace(config.SecretAccessKey))
        {
            log($"[ReleaseSourceConfig] '{path}' is missing a required field (bucket/region/accessKeyId/secretAccessKey) — Velopack update checks are disabled.");
            return null;
        }

        return config;
    }
}
