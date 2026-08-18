namespace RxVerifyOverlay.Integrated;

/// <summary>
/// Payload for IntegratedBoxesWindow/IntegratedOverlayCoordinator's
/// ReportErrorRequested event (RXVERIFY-TROUBLESHOOT, 2026-08 —
/// "position the dialog on the monitor/at the cursor where he
/// right-clicked"). Previously this event carried a bare VerdictFieldInfo;
/// <see cref="ClickPointPhysical"/> is added so MainWindow.xaml.cs's
/// OpenReportErrorDialog can position ReportErrorWindow near the actual
/// click, on the actual monitor, on a 2-monitor workstation — a plain
/// WindowStartupLocation="CenterScreen" (the previous behavior, still the
/// XAML fallback if positioning ever fails) has no way to know which of
/// two monitors "the screen" should mean.
///
/// <see cref="ReportingEnabled"/> (2026-08-18, "right-click must work on
/// EVERY field") carries IntegratedBoxesWindow's own SetBoxes-time
/// snapshot of OverlaySettings.RxVerifyReportKey being non-empty straight
/// through to Integrated/ReportErrorWindow.xaml.cs — a right-click used
/// to never even raise this event when it was false (see
/// RightClickOutcomeClassifier's git history); now the dialog always
/// opens, and this is what tells it to disable Submit and show the "not
/// set up" note instead of silently swallowing the click.
/// </summary>
public readonly record struct ReportErrorRequestInfo(VerdictFieldInfo Field, System.Drawing.Point ClickPointPhysical, bool ReportingEnabled);
