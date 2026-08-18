using System;
using System.Threading.Tasks;
using System.Windows;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.Reporting;

namespace RxVerifyOverlay.Integrated;

/// <summary>
/// "Send feedback" dialog — see the XAML header doc for why this is a
/// normal (non-click-through, activatable) window, same posture as its
/// sibling Integrated/ReportErrorWindow.xaml.cs. Owns exactly one
/// FeedbackSubmitter (constructed per-dialog against the live
/// OverlaySettings, same "freshly-saved RxVerifyReportKey always picked up"
/// reasoning as ReportErrorWindow's own _submitter).
/// </summary>
public sealed partial class FeedbackWindow : Window
{
    private readonly string? _engineBuild;
    private readonly string? _commit;
    private readonly bool _reportingEnabled;
    private readonly FeedbackSubmitter _submitter;

    public FeedbackWindow(string? engineBuild, string? commit, OverlaySettings settings)
    {
        InitializeComponent();

        _engineBuild = engineBuild;
        _commit = commit;
        _reportingEnabled = !string.IsNullOrWhiteSpace(settings.RxVerifyReportKey);
        _submitter = new FeedbackSubmitter(settings);

        NotSetUpNoteText.Visibility = _reportingEnabled ? Visibility.Collapsed : Visibility.Visible;
        SendButton.IsEnabled = _reportingEnabled;

        MessageTextBox.Focus();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Same instant-close-then-submit-in-background pattern as
    /// Integrated/ReportErrorWindow.xaml.cs OnSubmitClick — see that
    /// method's own doc for the full reasoning (owner's request: the
    /// window must go away the instant Send is clicked, not linger for the
    /// round trip). No log tail attached here at all — see
    /// Reporting/FeedbackPayload.cs's own doc.
    /// </summary>
    private void OnSendClick(object sender, RoutedEventArgs e)
    {
        // Belt-and-suspenders — see ReportErrorWindow.xaml.cs OnSubmitClick's
        // identical guard for why this can't normally be reached while
        // SendButton.IsEnabled is false.
        if (!_reportingEnabled) return;

        var payload = FeedbackBuilder.Build(MessageTextBox.Text, _engineBuild, _commit, DateTime.UtcNow);
        Close();

        _ = SubmitInBackgroundAsync(payload);
    }

    /// <summary>
    /// Fire-and-forget — runs after the window above is already closed, so
    /// it must never touch a UI control on this window. Unlike
    /// ReportErrorWindow's SubmitInBackgroundAsync, there is no failure
    /// popup here at all — see FeedbackSubmitter's own doc for why a
    /// failed send is simply a silent no-op for feedback.
    /// </summary>
    private async Task SubmitInBackgroundAsync(FeedbackPayload payload)
    {
        await _submitter.SubmitAsync(payload);
    }
}
