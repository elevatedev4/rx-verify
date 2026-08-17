using System;
using System.Windows;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.Reporting;

namespace RxVerifyOverlay.Integrated;

/// <summary>
/// "Report error…" dialog — see the XAML header doc for why this is a
/// normal (non-click-through, activatable) window, unlike its siblings in
/// this folder. Owns exactly one RxReportSubmitter (constructed per-dialog
/// against the live OverlaySettings so a freshly-saved RxVerifyReportKey
/// is always picked up without needing to relaunch) and never touches
/// EngineClient/OverlayViewModel directly — <paramref name="engineBuild"/>/
/// <paramref name="commit"/> are handed in already-resolved by
/// MainWindow.xaml.cs, same separation ControlBoxWindow already keeps.
/// </summary>
public sealed partial class ReportErrorWindow : Window
{
    private readonly VerdictFieldInfo _field;
    private readonly string? _engineBuild;
    private readonly string? _commit;
    private readonly RxReportSubmitter _submitter;

    public ReportErrorWindow(VerdictFieldInfo field, string? engineBuild, string? commit, OverlaySettings settings)
    {
        InitializeComponent();

        _field = field;
        _engineBuild = engineBuild;
        _commit = commit;
        _submitter = new RxReportSubmitter(settings);

        FieldNameText.Text = field.DisplayName;
        StatusText.Text = $"Status: {field.Status}";
        SourceText.Text = $"Source: {field.SourceValue}";
        EnteredText.Text = $"Entered: {field.EnteredValue}";
        ExplanationText.Text = field.Explanation;
        ExplanationText.Visibility = string.IsNullOrEmpty(field.Explanation) ? Visibility.Collapsed : Visibility.Visible;

        CorrectionTextBox.Focus();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Owner's request (2026-08-17): the window must go away the instant
    /// Submit is clicked, not linger for the send/queue round trip — so
    /// this builds the payload (Reporting/RxReportBuilder — the one place
    /// the "NO patient fields in the payload" redaction rule is enforced)
    /// from the form controls FIRST, then closes immediately, then kicks
    /// the actual send off as a detached background task. The payload is a
    /// plain captured value and _submitter only holds settings (no UI
    /// refs), so nothing the background task touches is disposed by the
    /// Close() above it.
    /// </summary>
    private void OnSubmitClick(object sender, RoutedEventArgs e)
    {
        var payload = RxReportBuilder.Build(_field, CorrectionTextBox.Text, _engineBuild, _commit, DateTime.UtcNow);
        Close();

        _ = SubmitInBackgroundAsync(payload);
    }

    /// <summary>
    /// Fire-and-forget: runs after the window above is already closed, so
    /// it must never touch a UI control on this window — payload (already
    /// captured) and _submitter (settings only) are the only things it
    /// touches. SubmitOrQueueAsync itself never throws for the ordinary
    /// "HQ unreachable" case — it falls back to the local queue and that's
    /// silent by design (see its doc's FAIL SOFT section; it retries
    /// later). The only thing worth an error popup is the rarer case where
    /// it couldn't even queue the report (e.g. disk I/O writing
    /// pending-reports.jsonl) — that's a genuine "this correction is gone"
    /// failure, so it gets a real popup via Application.Current.Dispatcher
    /// (this window is already closed by the time this runs, so nothing
    /// on it is a safe UI-thread handle to marshal onto) using
    /// MessageBoxOptions.DefaultDesktopOnly, same no-owner/no-activation
    /// pattern IntegratedBoxesWindow.ShowReportingDisabledNotice already
    /// uses to guarantee it lands in front of Pioneer without an Owner
    /// window to anchor to.
    /// </summary>
    private async System.Threading.Tasks.Task SubmitInBackgroundAsync(RxReportPayload payload)
    {
        try
        {
            await _submitter.SubmitOrQueueAsync(payload);
        }
        catch (Exception ex)
        {
            Application.Current.Dispatcher.Invoke(() =>
                MessageBox.Show(
                    $"This report couldn't be sent OR saved locally, so it's lost: {ex.Message}",
                    "Rx Verify — report failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning,
                    MessageBoxResult.OK,
                    MessageBoxOptions.DefaultDesktopOnly));
        }
    }
}
