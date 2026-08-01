using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Mohist.Server.Webhooks.Domain;

namespace Mohist.Server.Webhooks;

public interface IWebhookHttpClient
{
    Task<WebhookDeliveryResult> SendAsync(
        string targetUrl,
        ReadOnlyMemory<byte> body,
        WebhookAuthMaterial? auth,
        byte[]? signingSecret,
        CancellationToken ct);
}

/// <summary>Outcome of a single webhook HTTP attempt.</summary>
public sealed record WebhookDeliveryResult(
    bool Success,
    int? StatusCode,
    string? ResponseSnippet,
    string? Error,
    long DurationMs);

public sealed class WebhookHttpClient(HttpClient http) : IWebhookHttpClient
{
    public const string SignatureHeader = "X-Hub-Signature-256";

    /// <summary>CloudEvents 1.0 structured JSON media type.</summary>
    public const string CloudEventsJsonMediaType = "application/cloudevents+json";

    /// <summary>Header names callers may not set via custom auth, because they are transport-owned or reserved.</summary>
    private static readonly HashSet<string> ReservedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Transfer-Encoding", "Content-Type",
        SignatureHeader, "X-Mohist-Delivery", "X-Mohist-Event",
    };

    public async Task<WebhookDeliveryResult> SendAsync(
        string targetUrl,
        ReadOnlyMemory<byte> body,
        WebhookAuthMaterial? auth,
        byte[]? signingSecret,
        CancellationToken ct)
    {
        var started = Environment.TickCount64;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, targetUrl)
            {
                Content = new ByteArrayContent(body.ToArray()),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(CloudEventsJsonMediaType);

            if (auth is { Headers.Count: > 0 })
            {
                foreach (var (name, value) in auth.Headers)
                {
                    if (ReservedHeaders.Contains(name))
                        continue;
                    request.Headers.TryAddWithoutValidation(name, value);
                }
            }

            // Legacy compatibility: subscriptions created before v1 may still carry a signing secret.
            // v1 does not expose this in the create flow; it is preserved, not extended.
            if (signingSecret is { Length: > 0 })
                request.Headers.TryAddWithoutValidation(SignatureHeader, Sign(body.Span, signingSecret));

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            var success = status is >= 200 and <= 299;
            var snippet = success ? null : await ReadSnippetAsync(response.Content, ct).ConfigureAwait(false);
            return new WebhookDeliveryResult(success, status, snippet, success ? null : $"endpoint responded {status}", ElapsedMs(started));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new WebhookDeliveryResult(false, null, null, "request timed out", ElapsedMs(started));
        }
        catch (HttpRequestException ex)
        {
            return new WebhookDeliveryResult(false, null, null, ex.InnerException?.Message ?? ex.Message, ElapsedMs(started));
        }
        catch (Exception ex)
        {
            return new WebhookDeliveryResult(false, null, null, ex.Message, ElapsedMs(started));
        }
    }

    private static async Task<string?> ReadSnippetAsync(HttpContent? content, CancellationToken ct)
    {
        if (content is null) return null;
        const int max = 4096;
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[max];
        var read = await stream.ReadAsync(buffer.AsMemory(0, max), ct).ConfigureAwait(false);
        return read <= 0 ? null : Encoding.UTF8.GetString(buffer, 0, read);
    }

    private static long ElapsedMs(long started) => Math.Max(0, Environment.TickCount64 - started);

    private static string Sign(ReadOnlySpan<byte> body, ReadOnlySpan<byte> secret)
    {
        var hash = HMACSHA256.HashData(secret, body);
        return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
