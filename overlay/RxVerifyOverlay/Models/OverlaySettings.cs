using System;
using System.IO;
using System.Text.Json;

namespace RxVerifyOverlay.Models;

/// <summary>
/// The two paths every workstation setup needs, persisted locally so
/// Will doesn't have to re-enter them every launch. Stored as plain JSON
/// in %AppData%\RxVerifyOverlay\settings.json — contains ZERO patient
/// data, just file-system paths, so there's no PHI concern in this file
/// itself.
/// </summary>
public sealed class OverlaySettings
{
    /// <summary>
    /// Full path to rx-verify's compiled CLI entrypoint, e.g.
    /// "C:\Users\will\claude\rx-verify\dist\cli.js". See rx-verify's
    /// README + this app's README "Configuration" for how to build it.
    /// </summary>
    public string EngineCliPath { get; set; } = "";

    /// <summary>Path to node.exe, or just "node" if it's on PATH (the common case).</summary>
    public string NodeExecutable { get; set; } = "node";

    private static string SettingsFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RxVerifyOverlay", "settings.json");

    public static OverlaySettings Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var json = File.ReadAllText(SettingsFilePath);
                var loaded = JsonSerializer.Deserialize<OverlaySettings>(json);
                if (loaded is not null) return loaded;
            }
        }
        catch
        {
            // Corrupt/unreadable settings file -> fall through to defaults
            // rather than block the app from starting.
        }

        return new OverlaySettings();
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(SettingsFilePath)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsFilePath, json);
    }

    /// <summary>
    /// Best-effort auto-detection of rx-verify's compiled CLI entrypoint
    /// (dist/cli.js) so a fresh workstation with an empty/stale
    /// EngineCliPath doesn't have to be configured by hand before first
    /// use. The overlay is always built inside the repo, at
    /// &lt;repoRoot&gt;/overlay/RxVerifyOverlay/bin/&lt;cfg&gt;/&lt;tfm&gt;/, so walking
    /// up from AppContext.BaseDirectory and checking for dist/cli.js at
    /// each level reliably finds &lt;repoRoot&gt;/dist/cli.js without any
    /// hardcoded path depth. Pure (no WPF deps) so it's unit-testable;
    /// startDir defaults to AppContext.BaseDirectory in real use and is
    /// overridable in tests.
    /// </summary>
    public static string ResolveDefaultCliPath(string? startDir = null)
    {
        DirectoryInfo? dir;
        try
        {
            dir = new DirectoryInfo(startDir ?? AppContext.BaseDirectory);
        }
        catch
        {
            return "";
        }

        // Guard against pathological loops (shouldn't happen with real
        // filesystem parent chains, but cheap insurance) in addition to
        // the natural termination when Parent becomes null at the root.
        const int maxLevels = 64;
        for (var i = 0; dir is not null && i < maxLevels; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "dist", "cli.js");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "";
    }
}
