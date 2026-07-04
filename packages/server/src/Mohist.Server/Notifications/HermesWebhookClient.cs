using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Mohist.Server.Infrastructure;

namespace Mohist.Server.Notifications;

public sealed class HermesWebhookClient : IHermesWebhookClient
{
    public const string SignatureHeader = "X-Mohist-Signature";
    public const string EventHeader = "X-Mohist-Event";

    private readonly HttpClient _http;
    private readonly IOptionsMonitor<HermesNotificationOptions> _options;

    public HermesWebhookClient(HttpClient http, IOptionsMonitor<HermesNotificationOptions> options)
    {
        _http = http;
        _options = options;
    }

    public async Task SendAsync(HermesIssueNotificationPayload payload, CancellationToken ct)
    {
        var options = _options.CurrentValue;
        if (string.IsNullOrWhiteSpace(options.WebhookUrl))
            return;

        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JSON.Options);
        using var request = new HttpRequestMessage(HttpMethod.Post, options.WebhookUrl)
        {
            Content = new ByteArrayContent(bytes),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation(EventHeader, payload.NotificationType);

        if (!string.IsNullOrWhiteSpace(options.Secret))
        {
            request.Headers.TryAddWithoutValidation(SignatureHeader, Sign(bytes, options.Secret));
        }

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private static string Sign(byte[] payload, string secret)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var hash = HMACSHA256.HashData(key, payload);
        return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
