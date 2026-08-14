using System.Collections.Generic;
using System.Linq;
using RxVerifyOverlay.OrderAssist.Parsing;

namespace RxVerifyOverlay.OrderAssist.Decisions;

/// <summary>Which package-size bucket a Catalog Item Substitution row's Shipping Size cell falls into — see PackageClassifier.Classify.</summary>
public enum PackageClass
{
    /// <summary>Package quantity failed to parse (blank/OCR noise/unexpected format) — NEVER treated as either bucket, per the owner's fail-closed posture: an unreadable size can't be trusted to pick a badge.</summary>
    Unknown,
    Large,
    Small
}

/// <summary>
/// Will's round-2 "best large package vs best small package" rule: split
/// catalog rows into LARGE (package quantity &gt;= <see cref="LargePackageThreshold"/>,
/// e.g. 500- or 1000-count) vs SMALL (e.g. 30- or 100-count) using the
/// Shipping Size column (see PackageQuantityParser for how the numeric
/// quantity is pulled out of that column's free-text cell), then finds the
/// cheapest-by-Rebate-Cost-Per-Unit row within each bucket independently.
/// </summary>
public static class PackageClassifier
{
    /// <summary>Named per the owner's own examples ("500, 1000, etc" vs "30, 100, etc") -- LARGE is inclusive of exactly 500.</summary>
    public const decimal LargePackageThreshold = 500m;

    /// <summary>One catalog row's Shipping Size + Rebate Cost Per Unit text, already extracted via Scanning/CatalogSubstitutionScanner — mirrors SubstitutionRecommender.CatalogRowInput's shape/ownership split (this class never touches OCR geometry).</summary>
    public sealed record PackageRowInput(int RowIndex, string ShippingSizeText, string RebateCostPerUnitText);

    /// <summary>Each pick is null when that bucket has no row with BOTH a readable package quantity and a readable cost -- includes "no row of that class exists at all" (the owner's "if all rows are one class, only that class's best shows" case falls out of this naturally).</summary>
    public sealed record PackageClassPicks(int? BestLargeRowIndex, int? BestSmallRowIndex);

    public static PackageClass Classify(decimal? packageQuantity)
    {
        if (packageQuantity is null) return PackageClass.Unknown;
        return packageQuantity.Value >= LargePackageThreshold ? PackageClass.Large : PackageClass.Small;
    }

    /// <summary>
    /// Ties within a bucket resolve to the lowest RowIndex -- same
    /// deterministic, no-other-signal-available tiebreak as
    /// SubstitutionRecommender.Evaluate uses for its own cheapest-secondary
    /// pick.
    /// </summary>
    public static PackageClassPicks FindBestPerClass(IReadOnlyList<PackageRowInput> rows)
    {
        var parsed = rows
            .Select(r => new
            {
                r.RowIndex,
                Class = Classify(PackageQuantityParser.Parse(r.ShippingSizeText)),
                Cost = CurrencyParser.Parse(r.RebateCostPerUnitText)
            })
            .Where(p => p.Cost is not null)
            .ToList();

        int? BestOf(PackageClass targetClass) => parsed
            .Where(p => p.Class == targetClass)
            .OrderBy(p => p.Cost!.Value)
            .ThenBy(p => p.RowIndex)
            .Select(p => (int?)p.RowIndex)
            .FirstOrDefault();

        return new PackageClassPicks(BestOf(PackageClass.Large), BestOf(PackageClass.Small));
    }
}
