using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RxVerifyOverlay.Models;

namespace RxVerifyOverlay.Reporting;

/// <summary>
/// POSTs one FeedbackPayload to HQ's dedicated /api/rxverify-feedback
/// endpoint (branch fix/rightclick-all-feedback-compact, task 2),
/// authenticated with the same low-privilege OverlaySettings.RxVerifyReportKey
/// RxReportSubmitter already uses — a parallel, separate submitter (not a
/// shared base class/generic) because the two payload shapes and endpoints
/// are independent and this one is intentionally simpler.
///
/// UNLIKE RxReportSubmitter, this has NO local store-and-forward queue and
/// NO "genuinely lost" error popup: feedback is a nice-to-have note, not a
/// pharmacist's correction to a verdict that could otherwise silently be
/// lost forever, so a failed send here (missing key, unreachable endpoint,
/// non-2xx) is simply a no-op — Integrated/FeedbackWindow.xaml.cs's Send
/// button is already disabled whenever the key is unset (see its own doc),
/// so a failure that reaches this class at all is a genuine network
/// hiccup, not a first-time-setup gap — not worth interrupting the
/// pharmacist's shift for.
/// </summary>
public sealed class FeedbackSubmitter
{
    /// <summary>Shared across every submitter instance — see RxReportSubmitter.Http's own doc for the same "one long-lived instance" reasoning.</summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>HQ's dedicated feedback-intake endpoint — see RxReportSubmitter.EndpointUrl's own doc for why this is a fixed, public (non-secret) URL.</summary>
    private const string EndpointUrl = "https://manager-hq.vercel.app/api/rxverify-feedback";

    private readonly OverlaySettings _settings;

    public FeedbackSubmitter(OverlaySettings settings) => _settings = settings;

    /// <summary>
    /// Never throws — every failure mode (missing key, DNS/timeout/network
    /// error, non-2xx response) is caught and simply returns false; see
    /// class doc for why that's fine here, unlike RxReportSubmitter's
    /// queue-then-Failed path.
    /// </summary>
    public async Task<bool> SubmitAsync(FeedbackPayload payload)
    {
        if (string.IsNullOrWhiteSpace(_settings.RxVerifyReportKey)) return false;

        try
        {
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            using var request = new HttpRequestMessage(HttpMethod.Post, EndpointUrl)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.RxVerifyReportKey);

            using var response = await Http.SendAsync(request).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            // Endpoint missing/unreachable/DNS/timeout/etc — fail soft,
            // see class doc. Feedback is simply not sent this time.
            return false;
        }
    }
}
