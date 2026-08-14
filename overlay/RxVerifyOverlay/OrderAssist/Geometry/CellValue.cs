namespace RxVerifyOverlay.OrderAssist.Geometry;

/// <summary>
/// One data row's reconstructed text (and, when at least one OCR word
/// matched, its tight bounding box) for a single resolved column — see
/// CellValueBucketizer, the only producer. <see cref="Bounds"/> is null
/// for a blank/unreadable cell (no word centered inside the column's
/// partition for this row) — callers must treat that as "unknown", never
/// silently coerce it to a zero/false/empty decision (see
/// Decisions/ZeroQuantityDetector.cs for the specific rule this backs).
/// </summary>
public sealed record CellValue(int RowIndex, string Text, RowRect? Bounds);
