using System;
using RxVerifyOverlay.OrderAssist.Windows;

namespace RxVerifyOverlay.OrderAssist.Ocr;

/// <summary>Which row-normalization path OrderAssistCoordinator runs on each captured bitmap.</summary>
public enum RowNormalizerMode
{
    /// <summary>LocalContrastNormalizer: color-blind local text/background contrast (default).</summary>
    Local,

    /// <summary>LegacyRowHighlightNormalizer: the exact previous chroma/luminance/fill-fraction gate + binarize path.</summary>
    Legacy,
}

/// <summary>
/// Maps the persisted <c>OrderAssistRowNormalizer</c> setting (settings.json,
/// %AppData%\RxVerifyOverlay) to a <see cref="RowNormalizerMode"/>. The
/// pharmacy owner can revert to the old behaviour with a one-line edit
/// (<c>"OrderAssistRowNormalizer": "legacy"</c>) and an app restart -- no
/// redeploy. Anything other than "legacy" (null, blank, typos) is Local, so a
/// bad value can never select an unintended path.
/// </summary>
public static class RowNormalizerModes
{
    public const string LocalName = "local";
    public const string LegacyName = "legacy";

    /// <summary>Setting value that forces the local-contrast normalizer on EVERY order window (diagnostic / opt-in for the Catalog Substitution window).</summary>
    public const string LocalEverywhereName = "local-all";

    public static RowNormalizerMode Resolve(string? setting) =>
        string.Equals(setting?.Trim(), LegacyName, StringComparison.OrdinalIgnoreCase)
            ? RowNormalizerMode.Legacy
            : RowNormalizerMode.Local;
}

public static class RowNormalizerWindowPolicy
{
    /// <summary>
    /// The mode to run for a given order window. Default ("local"): the
    /// local-contrast normalizer for Create Recommended Orders (the screen
    /// the colored-row request was about) but the LEGACY normalizer for
    /// Recommended Order - Catalog Item Substitution Selection, whose pricing
    /// analysis (savings badges, package markers) stopped working in the
    /// pharmacy after the local normalizer shipped (2026-11-03) while the
    /// pre-change pixels worked. "legacy" = legacy everywhere; "local-all" =
    /// local everywhere.
    /// </summary>
    public static RowNormalizerMode Resolve(OrderAssistWindowKind kind, string? setting)
    {
        var trimmed = setting?.Trim();
        if (string.Equals(trimmed, RowNormalizerModes.LegacyName, StringComparison.OrdinalIgnoreCase)) return RowNormalizerMode.Legacy;
        if (string.Equals(trimmed, RowNormalizerModes.LocalEverywhereName, StringComparison.OrdinalIgnoreCase)) return RowNormalizerMode.Local;
        return kind == OrderAssistWindowKind.CatalogSubstitution ? RowNormalizerMode.Legacy : RowNormalizerMode.Local;
    }
}
