using System.Net;
using System.Text;
using Mohist.Server.Infrastructure.Slack.Ports;

namespace Mohist.Server.SpecTests.Support;

/// <summary>
/// Scripted fake for the production <see cref="SlackApiTransport"/> handler
/// chain in integration specs: every Slack Web API call made by a production
/// port adapter is recorded and answered from a per-test script instead of
/// the network. The default responder rejects any unscripted call loudly, so
/// a spec that forgets to script an endpoint fails deterministically instead
/// of hanging or leaking to the real Slack API. A singleton script is shared
/// by all typed-client handler instances of one test host.
/// </summary>
public sealed class SlackApiTestScript
{
    public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } = Unscripted;

    public List<RecordedSlackApiRequest> Requests { get; } = [];

    /// <summary>
    /// Resets recording and restores the loud-failure default responder.
    /// Every spec that scripts responses calls this first so ordering or
    /// leftover state can never leak between tests.
    /// </summary>
    public void Clear()
    {
        Requests.Clear();
        Responder = Unscripted;
    }

    public static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Unscripted(HttpRequestMessage request) =>
        JsonResponse("""{"ok":false,"error":"unexpected_slack_api_call"}""");
}

public sealed class SlackApiTestHandler(SlackApiTestScript script) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        script.Requests.Add(new RecordedSlackApiRequest(
            request.Method,
            request.RequestUri?.ToString() ?? string.Empty,
            request.Headers.Authorization?.ToString(),
            body));
        return script.Responder(request);
    }
}

public sealed record RecordedSlackApiRequest(
    HttpMethod Method,
    string Uri,
    string? Authorization,
    string Body);
