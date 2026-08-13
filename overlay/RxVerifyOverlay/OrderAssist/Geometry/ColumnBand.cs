namespace RxVerifyOverlay.OrderAssist.Geometry;

/// <summary>
/// One resolved header column: its own raw header-text extent
/// (<see cref="Left"/>/<see cref="Right"/>) plus the wider PARTITION it
/// owns for bucketing data-row words (<see cref="PartitionLeft"/>/
/// <see cref="PartitionRight"/> — the midpoint to each neighboring
/// column, so a right- or left-aligned value that doesn't line up
/// exactly under its own (often narrower) header text still lands in the
/// right column, never its neighbor's). See ColumnResolver, the only
/// producer of these.
/// </summary>
public sealed record ColumnBand(string Label, double Left, double Right, double PartitionLeft, double PartitionRight);
