using System;

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

    public static RowNormalizerMode Resolve(string? setting) =>
        string.Equals(setting?.Trim(), LegacyName, StringComparison.OrdinalIgnoreCase)
            ? RowNormalizerMode.Legacy
            : RowNormalizerMode.Local;
}
