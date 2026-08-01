using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Mohist.Server.Webhooks;

public interface IWebhookHttpClient
{
    Task SendAsync(string targetUrl, ReadOnlyMemory<byte> body, byte[]? secret, CancellationToken ct);
}

public sealed class WebhookHttpClient(HttpClient http) : IWebhookHttpClient
{
    public const string SignatureHeader = "X-Hub-Signature-256";

    public async Task SendAsync(string targetUrl, ReadOnlyMemory<byte> body, byte[]? secret, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, targetUrl)
        {
            Content = new ByteArrayContent(body.ToArray()),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        if (secret is { Length: > 0 })
            request.Headers.TryAddWithoutValidation(SignatureHeader, Sign(body.Span, secret));

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private static string Sign(ReadOnlySpan<byte> body, ReadOnlySpan<byte> secret)
    {
        var hash = HMACSHA256.HashData(secret, body);
        return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
