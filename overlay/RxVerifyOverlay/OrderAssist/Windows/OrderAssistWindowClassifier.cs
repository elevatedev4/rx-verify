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
