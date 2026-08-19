using System;
using System.Windows;
using RxVerifyOverlay.Diagnostics;
using RxVerifyOverlay.Models;
using RxVerifyOverlay.Ocr;
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
    /// <summary>Shown on screen in place of a patient field's real Source/Entered (VerdictFieldInfo.IsPatientField) — Reporting/RxReportBuilder.cs redacts to RxLogFormatter.RedactedValue ("[redacted]") independently for the payload; this is a separate, more explanatory on-screen string since the dialog is a human-facing UI, not the wire format.</summary>
    private const string PatientFieldDisplayPlaceholder = "[hidden — patient field]";

    private readonly VerdictFieldInfo _field;
    private readonly string? _engineBuild;
    private readonly string? _commit;
    private readonly string _sourceInputMode;
    private readonly bool _reportingEnabled;
    private readonly RxReportSubmitter _submitter;

    /// <param name="reportingEnabled">
    /// ReportErrorRequestInfo.ReportingEnabled — OverlaySettings.RxVerifyReportKey
    /// non-empty as of IntegratedBoxesWindow's last SetBoxes call. 2026-08-18
    /// ("right-click must work on EVERY field"): this dialog now ALWAYS
    /// opens, even when this is false — see NotSetUpNoteText/SubmitButton
    /// below, which is how a missing key is now surfaced instead of the
    /// right-click silently doing nothing.
    /// </param>
    public ReportErrorWindow(VerdictFieldInfo field, string? engineBuild, string? commit, OverlaySettings settings, bool reportingEnabled)
    {
        InitializeComponent();

        _field = field;
        _engineBuild = engineBuild;
        _commit = commit;
        _reportingEnabled = reportingEnabled;
        // Diagnostic-only (2026-08-17 fix round, item 2 — see
        // Reporting/RxReportPayload.cs SourceInputMode's doc). Resolved
        // ONCE here (not re-read from `settings` later) — matches
        // engineBuild/commit's existing "captured at construction, not
        // re-derived at submit time" pattern, and avoids holding onto the
        // whole mutable OverlaySettings object as a field just for this.
        _sourceInputMode = settings.Method.ToString().ToLowerInvariant();
        _submitter = new RxReportSubmitter(settings);

        FieldNameText.Text = field.DisplayName;
        StatusText.Text = $"Status: {field.Status}";
        // 2026-08-18: patient fields show a fixed placeholder on screen
        // instead of the real value (see PatientFieldDisplayPlaceholder's
        // own doc) — this dialog used to never even open for these 3
        // fields; now it does, so what WAS a redaction only inside the
        // submitted payload also has to hold on screen.
        SourceText.Text = $"Source: {(field.IsPatientField ? PatientFieldDisplayPlaceholder : field.SourceValue)}";
        EnteredText.Text = $"Entered: {(field.IsPatientField ? PatientFieldDisplayPlaceholder : field.EnteredValue)}";
        ExplanationText.Text = field.Explanation;
        ExplanationText.Visibility = string.IsNullOrEmpty(field.Explanation) ? Visibility.Collapsed : Visibility.Visible;

        // 2026-08-19 policy change (Will verbatim: "it won't let me type
        // anything in the box... Fix that"): CorrectionTextBox is now
        // ENABLED for patient fields too — see PatientFieldNoteText's own
        // XAML doc and Reporting/RxReportBuilder.cs's PatientFieldCorrectionGuard
        // for the actual enforcement (typed text is sent as-is UNLESS it
        // contains a significant fragment of this field's own real
        // captured value, in which case Build still withholds it exactly
        // like it always did before this change).
        PatientFieldNoteText.Visibility = field.IsPatientField ? Visibility.Visible : Visibility.Collapsed;

        // Live feedback for the same guard, re-run on every keystroke —
        // see OnCorrectionTextChanged. Only wired for patient fields; a
        // non-patient field's correction was never redacted and isn't
        // gated by this guard at all, so there's nothing to check there.
        if (field.IsPatientField)
        {
            CorrectionTextBox.TextChanged += OnCorrectionTextChanged;
        }

        // 2026-08-18: missing report key no longer suppresses the
        // right-click — this dialog opens regardless, but Submit is
        // disabled and the reason is spelled out inline (see XAML doc on
        // NotSetUpNoteText) instead of the click doing nothing at all.
        NotSetUpNoteText.Visibility = _reportingEnabled ? Visibility.Collapsed : Visibility.Visible;
        SubmitButton.IsEnabled = _reportingEnabled;

        // 2026-08-19: focus the Correction box regardless of field type —
        // patient fields get real keyboard focus here too now (see class
        // doc's "check all fields" item: every OTHER field already
        // focused/accepted typing correctly — CorrectionTextBox.IsEnabled
        // was the ONLY per-field gate anywhere in this window, and it's
        // gone now).
        CorrectionTextBox.Focus();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// UI-ONLY feedback (2026-08-19) — see PatientFieldNoteText/
    /// CorrectionGuardNoteText's own XAML docs and PatientFieldCorrectionGuard's
    /// own doc for why this is never trusted as the actual safety decision
    /// (RxReportBuilder.Build re-runs the identical check authoritatively
    /// at submit time, regardless of what this shows).
    /// </summary>
    private void OnCorrectionTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var tripped = PatientFieldCorrectionGuard.ContainsPatientValueFragment(CorrectionTextBox.Text, _field.SourceValue, _field.EnteredValue);
        CorrectionGuardNoteText.Visibility = tripped ? Visibility.Visible : Visibility.Collapsed;
    }

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
    ///
    /// logTail (2026-08-17 fix round — Will verbatim: "Make sure the
    /// RxVerify error reports are sending the logs... the HIPAA-free logs
    /// obviously"): read + filtered HERE, synchronously, before Close() —
    /// same "capture everything the background task needs before the
    /// window goes away" posture as the rest of this method. OcrLogger.
    /// TryReadAllLines/LogTailBuilder.BuildSafeTail are both best-effort
    /// (never throw), so a slow/locked/missing log file degrades to no
    /// logTail rather than blocking or failing the submit.
    /// </summary>
    private void OnSubmitClick(object sender, RoutedEventArgs e)
    {
        // Belt-and-suspenders: SubmitButton.IsEnabled = false already
        // prevents a normal click from reaching here when no report key is
        // configured (see constructor) — this early-out just makes sure a
        // future change that somehow re-enables the button (or fires this
        // handler some other way) still can't submit nothing/queue a
        // report that will never be retried in a meaningful way.
        if (!_reportingEnabled) return;

        var logTail = LogTailBuilder.BuildSafeTail(OcrLogger.TryReadAllLines());
        var payload = RxReportBuilder.Build(_field, CorrectionTextBox.Text, _engineBuild, _commit, DateTime.UtcNow, _sourceInputMode, logTail);
        Close();

        _ = SubmitInBackgroundAsync(payload);
    }

    /// <summary>
    /// Fire-and-forget: runs after the window above is already closed, so
    /// it must never touch a UI control on this window — payload (already
    /// captured) and _submitter (settings only) are the only things it
    /// touches. SubmitOrQueueAsync never throws (review round 2: it used to
    /// be checked via try/catch here, but nothing on the path it wraps —
    /// RxReportSubmitter's own catch, PendingReportsQueue.Enqueue's own
    /// catch — could ever actually throw into it, making that catch dead
    /// code); the ordinary "HQ unreachable" case falls back to the local
    /// queue and returns Queued, silent by design (see its doc's FAIL SOFT
    /// section; it retries later). The only outcome worth an error popup is
    /// ReportSubmitOutcome.Failed — it couldn't even queue the report
    /// locally (e.g. disk I/O writing pending-reports.jsonl) — a genuine
    /// "this correction is gone" case, so it gets a real popup via
    /// Application.Current.Dispatcher (this window is already closed by
    /// the time this runs, so nothing on it is a safe UI-thread handle to
    /// marshal onto) using MessageBoxOptions.DefaultDesktopOnly, same
    /// no-owner/no-activation pattern
    /// IntegratedBoxesWindow.ShowReportingDisabledNotice already uses to
    /// guarantee it lands in front of Pioneer without an Owner window to
    /// anchor to.
    /// </summary>
    private async System.Threading.Tasks.Task SubmitInBackgroundAsync(RxReportPayload payload)
    {
        var outcome = await _submitter.SubmitOrQueueAsync(payload);
        if (outcome != ReportSubmitOutcome.Failed) return;

        Application.Current.Dispatcher.Invoke(() =>
            MessageBox.Show(
                "This report couldn't be sent or saved locally, so it's lost.",
                "Rx Verify — report failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning,
                MessageBoxResult.OK,
                MessageBoxOptions.DefaultDesktopOnly));
    }
}
