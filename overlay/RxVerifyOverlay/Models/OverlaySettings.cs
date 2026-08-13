using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RxVerifyOverlay.Models;

/// <summary>
/// Which source-reading path RefreshAsync uses (see
/// ViewModels/OverlayViewModel.cs RefreshAsync). Ocr is the default —
/// screen-capture + local OCR of the Escript tab, no tab switch. Uia
/// reads the Escript tab's structured UIA tree directly (the original
/// "Verify" behavior, pre-VerifyOCR).
/// </summary>
public enum VerificationMethod
{
    Ocr,
    Uia
}

/// <summary>
/// Which visual presentation the overlay uses — see OverlaySettings.DisplayMode.
/// Separate is the original/default behavior: a standalone always-on-top
/// window (MainWindow) with its own verdict table, completely independent
/// of PioneerRx's own window. Integrated draws per-field verdict boxes
/// directly over the live PioneerRx window plus a small control panel in
/// its ribbon — see Integrated/IntegratedOverlayCoordinator.cs.
/// </summary>
public enum DisplayMode
{
    Separate,
    Integrated
}

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
    /// Which source-reading path to use — see VerificationMethod doc.
    /// Serialized as a STRING (e.g. "Ocr"/"Uia") rather than a raw int so
    /// settings.json stays human-readable, and an old settings file
    /// written before this field existed deserializes with the default
    /// (Ocr) rather than failing.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public VerificationMethod Method { get; set; } = VerificationMethod.Ocr;

    /// <summary>
    /// Which presentation to show — see DisplayMode doc. Integrated (verdict
    /// boxes drawn directly over PioneerRx) is the default — the owner uses
    /// Integrated daily and asked for it to be what a fresh install/machine
    /// starts in, rather than requiring an opt-in click every time. Separate
    /// remains fully available and is what the toggle switches back to;
    /// serialized as a readable string for the same reason as Method above.
    /// Switchable at runtime from either MainWindow's own toggle or the
    /// in-Pioneer control box's toggle (Integrated/ControlBoxWindow.cs) —
    /// both write through IntegratedOverlayCoordinator.SetDisplayMode so
    /// there is exactly one place that persists this and updates both UIs
    /// (audited 2026-08-12: confirmed no other code path writes DisplayMode
    /// — see FallbackSeparateWindowRule's doc, which never touches settings,
    /// only Show/Hide window visibility).
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DisplayMode DisplayMode { get; set; } = DisplayMode.Integrated;

    /// <summary>
    /// Full path to rx-verify's compiled CLI entrypoint, e.g.
    /// "C:\Users\will\claude\rx-verify\dist\cli.js". See rx-verify's
    /// README + this app's README "Configuration" for how to build it.
    /// </summary>
    public string EngineCliPath { get; set; } = "";

    /// <summary>Path to node.exe, or just "node" if it's on PATH (the common case).</summary>
    public string NodeExecutable { get; set; } = "node";

    /// <summary>
    /// VerifyOCR capture-region override (see Ocr/EscriptImageCapture.cs
    /// ResolveCaptureRegion). False (the default) means "auto": use the
    /// center Escript pane's live on-screen bounds (AutomationId
    /// cntTabControl), falling back to the whole PioneerRx window if that
    /// can't be found. Set true and fill in the four fields below only if
    /// auto-detection doesn't land on the right part of Will's screen —
    /// exposed via MainWindow.xaml's "Engine settings" expander, same
    /// place as the CLI/node paths.
    /// </summary>
    public bool UseExplicitCaptureRegion { get; set; }

    /// <summary>Screen X of the top-left corner of the capture region, in pixels — only used when UseExplicitCaptureRegion is true.</summary>
    public int CaptureRegionLeft { get; set; }

    /// <summary>Screen Y of the top-left corner of the capture region, in pixels — only used when UseExplicitCaptureRegion is true.</summary>
    public int CaptureRegionTop { get; set; }

    /// <summary>Capture region width in pixels — only used when UseExplicitCaptureRegion is true.</summary>
    public int CaptureRegionWidth { get; set; }

    /// <summary>Capture region height in pixels — only used when UseExplicitCaptureRegion is true.</summary>
    public int CaptureRegionHeight { get; set; }

    /// <summary>
    /// Bearer key for HQ's dedicated, low-privilege /api/rxverify-reports
    /// endpoint (see HQ-ENDPOINT-SPEC.md at the repo root) — lets this
    /// workstation CREATE pharmacist-submitted "Report error…" corrections
    /// only. Deliberately NOT the full Manager HQ secret: rx-verify has no
    /// cloud of its own and must never embed that broader credential in a
    /// client that ships to a pharmacy workstation (see
    /// Reporting/RxReportSubmitter.cs).
    ///
    /// DEFAULT/UNSET BEHAVIOR: empty string (the default) means "Report
    /// error…" is hidden entirely on every verdict bar — see
    /// Integrated/IntegratedOverlayCoordinator.cs UpdateBoxes
    /// (reportingEnabled). There is deliberately no reporting affordance
    /// shown on a workstation this hasn't been configured for yet, rather
    /// than showing a button that can only ever queue locally forever.
    /// No settings-UI field for this yet (out of scope for the branch that
    /// added it) — set by hand-editing settings.json until HQ's coder side
    /// ships the endpoint and issues a real key.
    /// </summary>
    public string RxVerifyReportKey { get; set; } = "";

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
