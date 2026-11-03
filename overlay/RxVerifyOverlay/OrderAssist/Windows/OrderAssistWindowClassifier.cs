using System;

namespace RxVerifyOverlay.OrderAssist.Windows;

/// <summary>Which of the two Order Assist target Pioneer windows (if either) a title belongs to — see OrderAssistWindowClassifier.Classify.</summary>
public enum OrderAssistWindowKind
{
    None,
    CreateRecommendedOrders,
    CatalogSubstitution
}

/// <summary>
/// Title-prefix matching for the two Pioneer windows this module watches
/// — confirmed exact titles from the owner's reference screenshots (see
/// branch brief): "Create Recommended Orders" and "Recommended Order -
/// Catalog Item Substitution Selection". StartsWith (not exact-equals),
/// case-insensitive, mirrors the SAME pattern Uia/PioneerRxWindow.cs
/// already uses for its own target window titles (FieldMap.
/// TargetWindowTitlePrefixes) — tolerant of Pioneer appending anything
/// after the base title (e.g. a suffix) without needing to be re-verified
/// against every possible Pioneer build/theme.
/// </summary>
public static class OrderAssistWindowClassifier
{
    public const string CreateRecommendedOrdersTitle = "Create Recommended Orders";
    public const string CatalogSubstitutionTitle = "Recommended Order - Catalog Item Substitution Selection";

    /// <summary>
    /// Title prefixes of ANY Pioneer order screen, for automatic Order/Verify
    /// mode switching (<see cref="IsOrderScreenTitle"/>): the Create
    /// Recommended Orders window and every "Recommended Order - ..." dialog
    /// (Catalog Item Substitution Selection and siblings). Deliberately
    /// broader than <see cref="Classify"/>, which only knows the two windows
    /// the scanners can read: staying in Order mode while ANY order dialog
    /// is up avoids a Verify/Order flap when Pioneer opens a sibling dialog.
    /// </summary>
    public static readonly string[] OrderScreenTitlePrefixes =
    {
        "Create Recommended Order",
        "Recommended Order",
    };

    /// <summary>True when <paramref name="windowTitle"/> (trimmed) starts, case-insensitively, with one of <see cref="OrderScreenTitlePrefixes"/>. Null/blank never match. Pure; the title is only compared, never stored or logged (it can carry patient data).</summary>
    public static bool IsOrderScreenTitle(string? windowTitle)
    {
        if (string.IsNullOrWhiteSpace(windowTitle)) return false;

        var trimmed = windowTitle.Trim();
        foreach (var prefix in OrderScreenTitlePrefixes)
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static OrderAssistWindowKind Classify(string? windowTitle)
    {
        if (string.IsNullOrWhiteSpace(windowTitle)) return OrderAssistWindowKind.None;

        var trimmed = windowTitle.Trim();

        if (trimmed.StartsWith(CatalogSubstitutionTitle, StringComparison.OrdinalIgnoreCase))
        {
            return OrderAssistWindowKind.CatalogSubstitution;
        }

        if (trimmed.StartsWith(CreateRecommendedOrdersTitle, StringComparison.OrdinalIgnoreCase))
        {
            return OrderAssistWindowKind.CreateRecommendedOrders;
        }

        return OrderAssistWindowKind.None;
    }
}
