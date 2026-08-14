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
/// </summary>
public readonly record struct ReportErrorRequestInfo(VerdictFieldInfo Field, System.Drawing.Point ClickPointPhysical);
