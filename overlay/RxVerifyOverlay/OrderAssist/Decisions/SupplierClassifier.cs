using System;

namespace RxVerifyOverlay.OrderAssist.Decisions;

/// <summary>
/// McKesson vs "everything else" (secondary) classification for one
/// Supplier cell's OCR'd text — per the owner's spec verbatim: "The
/// Supplier column shows the wholesaler ... Our primary wholesaler is
/// McKesson". Case-insensitive Contains (not exact-equals) so a supplier
/// cell rendered as e.g. "McKesson Corp" or with stray OCR whitespace
/// still classifies correctly; every OTHER supplier name seen in the
/// owner's reference screenshot (IPC, ParMed, ANDA, TopRx, and various
/// manufacturer names in the Catalog Substitution grid) contains no
/// substring of "mckesson", so this can't misfire on the secondaries.
/// </summary>
public static class SupplierClassifier
{
    public static bool IsMcKesson(string? supplierText) =>
        !string.IsNullOrWhiteSpace(supplierText) &&
        supplierText.Contains("mckesson", StringComparison.OrdinalIgnoreCase);
}
