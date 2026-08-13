namespace RxVerifyOverlay.OrderAssist.Geometry;

/// <summary>
/// A bounding box in the OCR/capture-region coordinate space (plain
/// doubles, NOT DIPs, NOT screen-absolute) — the space every OcrWord in
/// this module already lives in (see Ocr/WindowsMediaOcrEngine.cs: "these
/// boxes remain relative to the captured region, not the full screen").
/// OrderAssistCoordinator is the one place that later converts this into
/// a physical screen rect (by adding the target window's own origin) and
/// then a DIP rect (Integrated/DpiRectConverter.cs) for actually drawing
/// a highlight — every pure class in this OrderAssist/Geometry and
/// Decisions folder stays in this one simple, DPI-agnostic space so it
/// can be unit-tested with plain numbers.
/// </summary>
public readonly record struct RowRect(double Left, double Top, double Right, double Bottom);
