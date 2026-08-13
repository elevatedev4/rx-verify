using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RxVerifyOverlay.Models;

namespace RxVerifyOverlay.Reporting;

/// <summary>Which path a submit attempt actually took — Integrated/ReportErrorWindow.xaml.cs uses this to tell the pharmacist "sent" vs. "saved, will send later" without exposing transport detail.</summary>
public enum ReportSubmitOutcome
{
    SentToHq,
    Queued
}

/// <summary>
/// POSTs one RxReportPayload to HQ's dedicated /api/rxverify-reports
/// endpoint (see HQ-ENDPOINT-SPEC.md at the repo root), authenticated with
/// the workstation's OverlaySettings.RxVerifyReportKey — a NEW, dedicated,
/// low-privilege secret that can only create reports, deliberately NOT the
/// full Manager HQ bearer secret (rx-verify has no cloud of its own and
/// must never embed that broader credential in a client shipped to a
/// pharmacy workstation).
///
/// FAIL SOFT (branch brief, hard requirement): a missing key, unreachable
/// endpoint, timeout, or non-2xx response NEVER surfaces an error to the
/// pharmacist mid-shift — it queues the report locally instead (see
/// PendingReportsQueue) and moves on silently. There is no user-facing
/// "report submission failed" state; Integrated/ReportErrorWindow.xaml.cs
/// only ever shows "Sent" or "Saved — will send later", both success-shaped
/// confirmations, because from the pharmacist's point of view the
/// correction IS captured either way.
///
/// Deliberately NOT unit tested (no HTTP-mocking infrastructure exists in
/// this test project, and this class is thin plumbing around HttpClient +
/// PendingReportsQueue, both already covered on their own — see
/// RxVerifyOverlay.Tests/PendingReportsQueueTests.cs and
/// RxReportBuilderTests.cs) — same "pure logic tested, transport plumbing
/// isn't" split as Engine/EngineClient.cs (ParseResponseLine/
/// TryParseHandshakeLine are tested; the process-spawning transport around
/// them isn't).
/// </summary>
public sealed class RxReportSubmitter
{
    /// <summary>Shared across every submitter instance (matches the .NET-recommended HttpClient lifetime pattern — one long-lived instance, never one-per-call) — this app creates at most one RxReportSubmitter per OverlayViewModel rebuild (see MainWindow.xaml.cs OnSaveSettingsClick), so a static instance avoids leaking a new one on every settings save.</summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// HQ's dedicated report-intake endpoint (see HQ-ENDPOINT-SPEC.md) —
    /// a fixed, public URL (not a secret; the bearer key is what's
    /// sensitive), same "Manager HQ base URL" every other project in
    /// Will's registry already points at.
    /// </summary>
    private const string EndpointUrl = "https://manager-hq.vercel.app/api/rxverify-reports";

    private readonly OverlaySettings _settings;

    public RxReportSubmitter(OverlaySettings settings) => _settings = settings;

    /// <summary>
    /// Submits one report — POSTs to HQ if a report key is configured and
    /// the request succeeds, otherwise queues it locally. Never throws:
    /// every failure mode (missing key, DNS/timeout/network error,
    /// non-2xx response) is caught and treated as "queue it" — see class
    /// doc's FAIL SOFT section.
    /// </summary>
    public async Task<ReportSubmitOutcome> SubmitOrQueueAsync(RxReportPayload payload)
    {
        if (string.IsNullOrWhiteSpace(_settings.RxVerifyReportKey))
        {
            PendingReportsQueue.Enqueue(payload);
            return ReportSubmitOutcome.Queued;
        }

        try
        {
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            using var request = new HttpRequestMessage(HttpMethod.Post, EndpointUrl)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.RxVerifyReportKey);

            using var response = await Http.SendAsync(request).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return ReportSubmitOutcome.SentToHq;
            }
        }
        catch
        {
            // Endpoint missing/unreachable/DNS/timeout/etc — fail soft,
            // see class doc. Falls through to the queue below either way.
        }

        PendingReportsQueue.Enqueue(payload);
        return ReportSubmitOutcome.Queued;
    }

    /// <summary>
    /// Re-attempts every locally-queued report — call once at app startup
    /// (see MainWindow.xaml.cs constructor) so a report captured on a
    /// workstation with no key configured yet (or during a network blip)
    /// eventually reaches HQ once connectivity/configuration is fixed,
    /// without the pharmacist having to do anything. A no-op (leaves the
    /// queue file untouched) when no key is configured yet — nothing would
    /// succeed, and DequeueAll is destructive, so calling it here would
    /// otherwise silently re-queue-and-lose nothing but do pointless I/O;
    /// simplest to just skip it entirely until there's a key to try.
    /// </summary>
    public async Task RetryPendingAsync()
    {
        if (string.IsNullOrWhiteSpace(_settings.RxVerifyReportKey)) return;

        var pending = PendingReportsQueue.DequeueAll();
        foreach (var payload in pending)
        {
            await SubmitOrQueueAsync(payload).ConfigureAwait(false); // re-queues on failure, identical to a fresh submit
        }
    }
}
