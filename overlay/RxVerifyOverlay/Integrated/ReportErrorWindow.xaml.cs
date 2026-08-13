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
    /// Builds the payload (Reporting/RxReportBuilder — the one place the
    /// "NO patient fields in the payload" redaction rule is enforced) and
    /// hands it to RxReportSubmitter, which itself never throws / never
    /// surfaces a failure (fail-soft: queues locally instead — see that
    /// class's doc). Both possible outcomes (SentToHq/Queued) are
    /// success-shaped from the pharmacist's point of view, so this never
    /// shows an error state — only which of the two happy paths it took,
    /// then auto-closes.
    /// </summary>
    private async void OnSubmitClick(object sender, RoutedEventArgs e)
    {
        SubmitButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        SubmitStatusText.Text = "Submitting…";
        SubmitStatusText.Foreground = System.Windows.Media.Brushes.Gray;

        var payload = RxReportBuilder.Build(_field, CorrectionTextBox.Text, _engineBuild, _commit, DateTime.UtcNow);
        var outcome = await _submitter.SubmitOrQueueAsync(payload);

        SubmitStatusText.Foreground = System.Windows.Media.Brushes.Green;
        SubmitStatusText.Text = outcome == ReportSubmitOutcome.SentToHq
            ? "Sent."
            : "Saved — will send once connected.";

        await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(1.1));
        Close();
    }
}
