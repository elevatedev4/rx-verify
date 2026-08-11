using RxVerifyOverlay.Models;

namespace RxVerifyOverlay.Integrated;

/// <summary>
/// Verdict -&gt; box-color mapping for the integrated boxes layer — a
/// deliberately BINARY collapse of the separate window's 3-color
/// (green/yellow/red) rendering, per the owner's explicit spec: Yellow
/// collapses to the same red "check it" box as Red, since drawing 3
/// distinct outline colors over live PioneerRx fields is more visual
/// noise than the integrated view is meant to add — the point is a fast
/// glance ("matches" vs. "check it"), not a 3-way read. MainWindow.xaml
/// (the separate window) is UNCHANGED and keeps rendering all 3 colors;
/// this mapping only ever feeds IntegratedBoxesWindow.
/// </summary>
public static class BoxColorMapper
{
    /// <summary>True (draw a GREEN box) only for VerdictStatus.Green; false (draw a RED box) for Yellow, Red, or any other status value.</summary>
    public static bool IsGreenBox(VerdictStatus status) => status == VerdictStatus.Green;
}
