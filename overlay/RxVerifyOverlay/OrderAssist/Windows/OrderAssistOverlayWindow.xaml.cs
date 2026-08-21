using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using RxVerifyOverlay.Integrated;

namespace RxVerifyOverlay.OrderAssist.Windows;

/// <summary>One savings badge, already converted to DIPs — round 3 replaces the old whole-row GreenRowDip/YellowRowDip fill with this small end-of-row tag (see CatalogHighlights' own doc and OrderAssistOverlayWindow.AddSavingsLabel).</summary>
public sealed record SavingsBadgeDip(DipRect RowDip, string SavingsDisplay, bool MeetsThreshold);

/// <summary>
/// Everything Order Assist can draw for one tick of the Catalog Item
/// Substitution window, already converted to DIPs by OrderAssistCoordinator
/// from CatalogSubstitutionScanner.CatalogAnnotations. Every field is
/// independently nullable/optional for the same reason CatalogAnnotations'
/// fields are — see that record's own doc — SetHighlights just draws
/// whatever it's given and skips whatever it isn't.
///
/// ProcessingAnchorDip is round 3's addition (Will: "ok to clear it
/// quickly and add a 'Processing' by the sorted by rebate notice if we're
/// waiting on analysis") — OrderAssistCoordinator only ever populates it on
/// a HighlightStabilityPolicy.Decision.Processing tick (see that class's
/// own doc), positioned at the same column anchor the sort badge itself
/// uses (CatalogSubstitutionScanner.CatalogAnnotations.CostColumnHeaderAnchor)
/// so it reads as "right next to" that notice regardless of whether the
/// sort badge itself is showing that tick.
/// </summary>
public sealed record CatalogHighlights(
    IReadOnlyList<SavingsBadgeDip> SavingsBadges,
    DipRect? BestLargePackageDip,
    string? BestLargePackageLabel,
    DipRect? BestSmallPackageDip,
    string? BestSmallPackageLabel,
    DipRect? SortBadgeAnchorDip,
    string? SortBadgeText,
    bool SortBadgeIsSorted,
    DipRect? ProcessingAnchorDip = null,
    // ROUND 4 (Will: "also highlight the cheapest mckesson item that is
    // being compared in some intuitive color") — the McKesson row
    // SubstitutionRecommender used as its savings baseline, see
    // CatalogSubstitutionScanner.CatalogAnnotations.McKessonBaselineMarker's
    // own doc.
    DipRect? McKessonBaselineDip = null,
    string? McKessonBaselineLabel = null);

/// <summary>
/// Order Assist's own click-through highlight layer — red boxes over
/// zero-quantity cells (Create Recommended Orders window) OR the full set
/// of CatalogHighlights (round 3: a per-row green/yellow savings badge at
/// the end of each qualifying row, best-large/best-small package markers,
/// sort-order badge, "Processing" indicator — Catalog Item Substitution
/// Selection window). The two target windows are never open
/// at the same time (see OrderAssistWindowClassifier), so in practice only
/// one of those two modes is ever populated at once — but nothing here
/// assumes that; SetHighlights just draws whatever it's given.
///
/// DELIBERATELY its own window, entirely separate from Integrated/
/// IntegratedBoxesWindow — this module must stay fully decoupled from the
/// verify flow (see OrderAssistCoordinator's class doc). It reuses only
/// the same GENERIC, verify-agnostic positioning helper
/// (Integrated/NativeWindowPositioning.cs — plain SetWindowPos wrappers
/// with no field/verdict-specific knowledge at all) rather than any of
/// IntegratedBoxesWindow's own field/hover/report-error code.
///
/// CLICK-THROUGH: unlike IntegratedBoxesWindow, this window has NO hover
/// affordance at all — always fully click-through (WS_EX_TRANSPARENT
/// stays set permanently, never toggled), no poll timer, no hotspots.
/// The spec only calls for a passive glance-highlight while the
/// pharmacist works the order screens, never an interactive one. Same
/// WS_EX discipline as IntegratedBoxesWindow otherwise (WS_EX_LAYERED so
/// WS_EX_TRANSPARENT takes effect, WS_EX_NOACTIVATE so Show()/reposition
/// never steals focus from PioneerRx, WS_EX_TOOLWINDOW to stay out of
/// Alt-Tab) and the same "force-transparent before every Show(), clear
/// all state on every Hide()" lessons that window's own doc calls out.
/// </summary>
public sealed partial class OrderAssistOverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    /// <summary>
    /// ROUND 2 (W-T85, Will verbatim: "the items are flashing a bunch
    /// instead of staying solid") — same technique MainWindow.xaml.cs
    /// already uses for the verify flow's own self-occlusion problem (see
    /// that file's OnSourceInitialized/WdaExcludeFromCapture doc): asking
    /// Windows itself to omit this window from any GDI screen capture,
    /// instead of hiding/showing the actual window around every capture.
    /// OrderAssistCoordinator.TickAsync previously did an UNCONDITIONAL
    /// HideAndClear() + ~30ms-plus-OCR-latency delay + re-Show() EVERY
    /// single ~1s tick (the self-occlusion guard — a red/green box left on
    /// screen during a capture would get baked into the OCR'd pixels) —
    /// that's a real, visible on/off pulse roughly once a second, which is
    /// exactly what "flashing instead of staying solid" describes. Once
    /// this exclusion is active, TickAsync skips that hide/show round
    /// trip entirely (mirrors MainWindow.HideForCaptureAsync's own
    /// early-return) and the window can just stay continuously visible.
    /// </summary>
    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    /// <summary>Requires Windows 10 2004+ (build 19041) — same minimum this project already targets (see RxVerifyOverlay.csproj TargetFramework) and the same constant MainWindow.xaml.cs already uses.</summary>
    private const uint WdaExcludeFromCapture = 0x00000011;

    /// <summary>True once SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) has been applied and returned success — see IsExcludedFromCapture.</summary>
    private bool _excludedFromCapture;

    // Filled (not just outlined) box at low opacity for the zero-quantity
    // red cell — legible at a glance without obscuring the OCR'd digit
    // underneath.
    private static readonly SolidColorBrush RedFillBrush = new(Color.FromArgb(0x55, 0xC6, 0x28, 0x28));

    // ROUND 3 (Will: "Always Calculate the savings for each item cheaper
    // than mckesson and display it at the end of the row ... Don't
    // highlight the whole row, just show it at the end") — solid badge
    // colors only now, no more whole-row translucent fill; reused as
    // AddSavingsLabel's Border.Background per savings tier.
    private static readonly SolidColorBrush GreenBorderBrush = new(Color.FromRgb(0x2E, 0x7D, 0x32));
    private static readonly SolidColorBrush YellowBorderBrush = new(Color.FromRgb(0xF9, 0xA8, 0x25));
    private const double GreenBorderThickness = 3;

    // ROUND 4 (Will verbatim: "Try highlighting the whole row in green ...
    // make sure the green highlight covers the whole row and is fairly
    // transparent so it doesn't impede reading") — restores a full-row
    // FILL alongside (never instead of) the round-3 end-of-row % badge.
    // Low alpha (0x30 of 0xFF, ~19%) is the "fairly transparent" part —
    // dark text underneath stays fully legible. Same green/yellow hues as
    // the badge borders above (GreenBorderBrush/YellowBorderBrush), just
    // translucent, so the fill and the badge always read as the same
    // decision rather than two different color systems.
    private static readonly SolidColorBrush GreenRowFillBrush = new(Color.FromArgb(0x30, 0x2E, 0x7D, 0x32));
    private static readonly SolidColorBrush YellowRowFillBrush = new(Color.FromArgb(0x30, 0xF9, 0xA8, 0x25));

    // ROUND 4 (Will: "also highlight the cheapest mckesson item that is
    // being compared in some intuitive color") — a THIRD hue, distinct
    // from green/yellow/red (all already mean a verdict) and from the
    // existing PackageMarkerBorderBrush blue (already means "best
    // large/small package", a different signal). Indigo/violet reads as
    // informational ("this is what everything else is being measured
    // against"), not a verdict of its own.
    private static readonly SolidColorBrush McKessonBaselineFillBrush = new(Color.FromArgb(0x28, 0x5E, 0x35, 0xB1));
    private static readonly SolidColorBrush McKessonBaselineBorderBrush = new(Color.FromRgb(0x5E, 0x35, 0xB1));
    private const double McKessonBaselineBorderThickness = 2;

    // ROUND 4 (Will: "If it doesn't meet the threshold to show good
    // savings, recommend ordering from McKesson by showing an 'Order from
    // McKesson' indicator") — deliberately a muted blue-gray, distinct
    // from every verdict color above; this is guidance ("stick with
    // McKesson"), not a savings result of its own.
    private static readonly SolidColorBrush OrderFromMcKessonBrush = new(Color.FromRgb(0x54, 0x6E, 0x7A));

    // Round-2 "best large vs best small package": an OUTLINE-only marker
    // (no fill) so it reads as a lighter/secondary signal that never
    // competes visually with a green/yellow fill on the same screen —
    // per the owner's own wording, "the other class's best gets a
    // lighter/secondary marker". A blue hue keeps it visually distinct
    // from green/yellow/red, all already spoken for.
    private static readonly SolidColorBrush PackageMarkerBorderBrush = new(Color.FromRgb(0x1E, 0x88, 0xE5));
    private const double PackageMarkerBorderThickness = 2;
    private const double PackageMarkerLabelOffsetDip = 14;

    // Round-2 sort-order badge above the Rebate Cost Per Unit header —
    // reuses the same green/red hues as the row highlights above (sorted
    // reads as the same "good/expected" green, not-sorted as the same
    // "needs attention" red) rather than inventing a third color pairing.
    private static readonly SolidColorBrush SortBadgeSortedBrush = new(Color.FromRgb(0x2E, 0x7D, 0x32));
    private static readonly SolidColorBrush SortBadgeUnsortedBrush = new(Color.FromRgb(0xC6, 0x28, 0x28));
    private const double SortBadgeHeightDip = 18;

    // ROUND 3 (Will: "It's ok to clear it quickly and add a 'Processing'
    // by the sorted by rebate notice if we're waiting on analysis") —
    // deliberately neutral gray, distinct from every other badge color
    // (red/green/yellow/blue all already mean something else), so
    // "Processing" never reads as a verdict of its own.
    private static readonly SolidColorBrush ProcessingBadgeBrush = new(Color.FromRgb(0x60, 0x60, 0x60));
    private const double ProcessingBadgeGapDip = 6;

    private IntPtr _hwnd = IntPtr.Zero;

    public OrderAssistOverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        ForceHitTestTransparent();

        try
        {
            _excludedFromCapture = SetWindowDisplayAffinity(_hwnd, WdaExcludeFromCapture);
        }
        catch
        {
            _excludedFromCapture = false;
        }
    }

    /// <summary>
    /// See SetWindowDisplayAffinity's own doc — OrderAssistCoordinator.
    /// TickAsync checks this to decide whether it can skip the
    /// hide-before-capture round trip entirely. False (the safe default)
    /// until OnSourceInitialized has actually run and confirmed the call
    /// succeeded — a caller checking this before the window's HWND exists,
    /// or on an OS older than Windows 10 2004, correctly falls back to the
    /// hide/show path.
    /// </summary>
    public bool IsExcludedFromCapture => _excludedFromCapture;

    /// <summary>Always click-through — see class doc. A no-op until the native HWND exists; called again right before every Show() (same belt-and-suspenders posture as IntegratedBoxesWindow.ForceHitTestTransparent) so this window can never surface non-transparent regardless of state history.</summary>
    public void ForceHitTestTransparent()
    {
        if (_hwnd == IntPtr.Zero) return;
        var exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    /// <summary>See NativeWindowPositioning.Reposition — physical pixels, matching the target Pioneer window's own bounds exactly.</summary>
    public void RepositionPhysical(int x, int y, int width, int height) => NativeWindowPositioning.Reposition(_hwnd, x, y, width, height);

    /// <summary>See NativeWindowPositioning.MakeTopmost — call once, right after the first Show().</summary>
    public void EnsureTopmost() => NativeWindowPositioning.MakeTopmost(_hwnd);

    /// <summary>
    /// Rebuilds the highlight layer from scratch every call (small element
    /// count every tick — at most a handful of zero-cells or a single
    /// screen's worth of catalog annotations — so a full rebuild is
    /// simplest-correct, same posture as IntegratedBoxesWindow.SetBoxes).
    /// <paramref name="redBoxesDip"/> is every zero-quantity cell this tick
    /// (Create Recommended Orders window); <paramref name="catalog"/> is
    /// every Catalog Item Substitution window annotation, or null when
    /// that window isn't the current target (the two target windows are
    /// never open at once — see class doc). Every DIP rect is already
    /// relative to THIS window's own top-left (see OrderAssistCoordinator's
    /// coordinate-conversion doc) — ready to assign straight to
    /// Canvas.Left/Top.
    /// </summary>
    public void SetHighlights(IReadOnlyList<DipRect> redBoxesDip, CatalogHighlights? catalog)
    {
        HighlightCanvas.Children.Clear();

        foreach (var box in redBoxesDip)
        {
            AddRect(box, RedFillBrush, borderBrush: null);
        }

        if (catalog is null) return;

        // ROUND 3 (Will: "Always Calculate the savings for each item
        // cheaper than mckesson and display it at the end of the row.
        // Below our threshold, show in yellow, above show green.") —
        // ROUND 4 (Will: "Try highlighting the whole row in green ...
        // make sure the green highlight covers the whole row and is
        // fairly transparent" + "the % indicator show the %, always at
        // the right side") — restores a full-row translucent fill
        // alongside the % badge (RowDip now spans the table's own
        // resolved column bands, not just whatever cells had OCR'd text
        // that tick — see CatalogSubstitutionScanner's own round-4 doc —
        // so both the fill and the badge anchor are stable regardless of
        // empty trailing columns). Below-threshold rows also get a small
        // "Order from McKesson" tag (Will, item 6).
        foreach (var badge in catalog.SavingsBadges)
        {
            AddRect(badge.RowDip, badge.MeetsThreshold ? GreenRowFillBrush : YellowRowFillBrush, borderBrush: null);
            AddSavingsLabel(badge.RowDip, badge.SavingsDisplay, badge.MeetsThreshold ? GreenBorderBrush : YellowBorderBrush);

            if (!badge.MeetsThreshold)
            {
                AddOrderFromMcKessonTag(badge.RowDip);
            }
        }

        if (catalog.McKessonBaselineDip is { } mckessonBaseline && !string.IsNullOrWhiteSpace(catalog.McKessonBaselineLabel))
        {
            AddMcKessonBaselineMarker(mckessonBaseline, catalog.McKessonBaselineLabel!);
        }

        if (catalog.BestLargePackageDip is { } bestLarge && !string.IsNullOrWhiteSpace(catalog.BestLargePackageLabel))
        {
            AddPackageMarker(bestLarge, catalog.BestLargePackageLabel!);
        }

        if (catalog.BestSmallPackageDip is { } bestSmall && !string.IsNullOrWhiteSpace(catalog.BestSmallPackageLabel))
        {
            AddPackageMarker(bestSmall, catalog.BestSmallPackageLabel!);
        }

        if (catalog.SortBadgeAnchorDip is { } anchor && !string.IsNullOrWhiteSpace(catalog.SortBadgeText))
        {
            AddSortBadge(anchor, catalog.SortBadgeText!, catalog.SortBadgeIsSorted);
        }

        // ROUND 3 (Will: "It's ok to clear it quickly and add a
        // 'Processing' by the sorted by rebate notice if we're waiting on
        // analysis") — OrderAssistCoordinator only ever sets this on a
        // Decision.Processing tick, alongside every other field above left
        // null/empty (see its own DrawAndShow doc), so this never competes
        // visually with a real, confirmed result.
        if (catalog.ProcessingAnchorDip is { } processingAnchor)
        {
            AddProcessingBadge(processingAnchor);
        }
    }

    /// <summary>Clears every highlight, THEN hides — call instead of a bare Hide() everywhere this window is hidden, so a stale highlight from the previous tick/target-window never lingers into whatever this window gets repositioned over next (same "clear state on hide" lesson as IntegratedBoxesWindow.HideAndResetHover).</summary>
    public void HideAndClear()
    {
        HighlightCanvas.Children.Clear();
        Hide();
    }

    private void AddRect(DipRect dip, Brush fill, Brush? borderBrush, double borderThickness = GreenBorderThickness)
    {
        var border = new Border
        {
            Width = Math.Max(0, dip.Width),
            Height = Math.Max(0, dip.Height),
            Background = fill,
            BorderBrush = borderBrush,
            BorderThickness = borderBrush is null ? new Thickness(0) : new Thickness(borderThickness),
            IsHitTestVisible = false
        };

        Canvas.SetLeft(border, dip.X);
        Canvas.SetTop(border, dip.Y);
        HighlightCanvas.Children.Add(border);
    }

    /// <summary>
    /// ROUND 3 (Will: "display it at the end of the row ... Don't
    /// highlight the whole row, just show it at the end") — a small badge
    /// anchored just to the right of the row's own (uniform-height, see
    /// RowBounds.ComputeUniform) extent, vertically centered on it, colored
    /// by <paramref name="backgroundBrush"/> (green above the savings
    /// threshold, yellow below it — see CatalogSubstitutionScanner.SavingsBadge.MeetsThreshold).
    /// No row fill/border drawn at all anymore — this badge IS the entire
    /// visual signal for one row's savings.
    /// </summary>
    private void AddSavingsLabel(DipRect rowDip, string text, Brush backgroundBrush)
    {
        var label = new Border
        {
            Background = backgroundBrush,
            Padding = new Thickness(4, 1, 4, 1),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                FontSize = 11,
                FontWeight = FontWeights.Bold
            }
        };

        Canvas.SetLeft(label, rowDip.X + rowDip.Width + 4);
        Canvas.SetTop(label, rowDip.Y + Math.Max(0, (rowDip.Height - 16) / 2.0));
        HighlightCanvas.Children.Add(label);
    }

    /// <summary>
    /// ROUND 3 (Will: "It's ok to clear it quickly and add a 'Processing'
    /// by the sorted by rebate notice if we're waiting on analysis") — a
    /// small neutral badge placed just past the same column anchor the
    /// sort badge itself uses (see CatalogHighlights.ProcessingAnchorDip's
    /// own doc), same vertical placement (just above the header) so it
    /// reads as sitting right next to that notice.
    /// </summary>
    private void AddProcessingBadge(DipRect anchorDip)
    {
        var badge = new Border
        {
            Background = ProcessingBadgeBrush,
            Padding = new Thickness(4, 1, 4, 1),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = "Processing…",
                Foreground = Brushes.White,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                FontStyle = FontStyles.Italic
            }
        };

        Canvas.SetLeft(badge, anchorDip.X + anchorDip.Width + ProcessingBadgeGapDip);
        Canvas.SetTop(badge, Math.Max(0, anchorDip.Y - SortBadgeHeightDip));
        HighlightCanvas.Children.Add(badge);
    }

    /// <summary>
    /// ROUND 4 (Will: "If it doesn't meet the threshold to show good
    /// savings, recommend ordering from McKesson by showing an 'Order
    /// from McKesson' indicator") — a small tag stacked just BELOW the
    /// row's own extent (never to the right, like AddSavingsLabel — that
    /// spot is already taken by the % badge, and this tag's own text
    /// width isn't known ahead of layout) at the same X anchor the %
    /// badge itself uses, so the two visually read as one group.
    /// </summary>
    private void AddOrderFromMcKessonTag(DipRect rowDip)
    {
        var tag = new Border
        {
            Background = OrderFromMcKessonBrush,
            Padding = new Thickness(4, 1, 4, 1),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = "Order from McKesson",
                Foreground = Brushes.White,
                FontSize = 9,
                FontWeight = FontWeights.SemiBold
            }
        };

        Canvas.SetLeft(tag, rowDip.X + rowDip.Width + 4);
        Canvas.SetTop(tag, rowDip.Y + rowDip.Height + 2);
        HighlightCanvas.Children.Add(tag);
    }

    /// <summary>
    /// ROUND 4 (Will: "also highlight the cheapest mckesson item that is
    /// being compared in some intuitive color") — a translucent full-row
    /// fill (same "fairly transparent, doesn't impede reading" posture as
    /// the green/yellow savings fills) plus an outline and a small label,
    /// in a THIRD hue distinct from every verdict color already in use —
    /// see McKessonBaselineFillBrush's own doc.
    /// </summary>
    private void AddMcKessonBaselineMarker(DipRect dip, string label)
    {
        AddRect(dip, McKessonBaselineFillBrush, McKessonBaselineBorderBrush, McKessonBaselineBorderThickness);

        var tag = new Border
        {
            Background = McKessonBaselineBorderBrush,
            Padding = new Thickness(4, 1, 4, 1),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = label,
                Foreground = Brushes.White,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold
            }
        };

        Canvas.SetLeft(tag, dip.X + 2);
        Canvas.SetTop(tag, Math.Max(0, dip.Y - PackageMarkerLabelOffsetDip));
        HighlightCanvas.Children.Add(tag);
    }

    /// <summary>
    /// Round-2 best-large/best-small package marker: an OUTLINE-only box
    /// (see PackageMarkerBorderBrush's own doc for why no fill) plus a
    /// small class-label tag anchored INSIDE the row's own top-left corner
    /// — deliberately not to the row's right like AddSavingsLabel, since
    /// up to two of these can be on screen at once (one per package
    /// class) and must never collide with each other or with a savings
    /// badge sitting off to that row's own right.
    /// </summary>
    private void AddPackageMarker(DipRect dip, string label)
    {
        AddRect(dip, Brushes.Transparent, PackageMarkerBorderBrush, PackageMarkerBorderThickness);

        var tag = new Border
        {
            Background = PackageMarkerBorderBrush,
            Padding = new Thickness(4, 1, 4, 1),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = label,
                Foreground = Brushes.White,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold
            }
        };

        Canvas.SetLeft(tag, dip.X + 2);
        Canvas.SetTop(tag, Math.Max(0, dip.Y - PackageMarkerLabelOffsetDip));
        HighlightCanvas.Children.Add(tag);
    }

    /// <summary>
    /// Round-2 sort-order badge: a small colored pill anchored just above
    /// the Rebate Cost Per Unit column's own header extent (<paramref
    /// name="anchorDip"/> — see CatalogSubstitutionScanner.ColumnBadge's
    /// doc for why Top there is already the header band's own top edge,
    /// not the column's data-row extent), left-aligned to the column
    /// rather than centered (no text-measurement pass needed to center
    /// it, and "right above the column" reads fine left-aligned too).
    /// </summary>
    private void AddSortBadge(DipRect anchorDip, string text, bool isSorted)
    {
        var badge = new Border
        {
            Background = isSorted ? SortBadgeSortedBrush : SortBadgeUnsortedBrush,
            Padding = new Thickness(4, 1, 4, 1),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold
            }
        };

        Canvas.SetLeft(badge, anchorDip.X);
        Canvas.SetTop(badge, Math.Max(0, anchorDip.Y - SortBadgeHeightDip));
        HighlightCanvas.Children.Add(badge);
    }
}
