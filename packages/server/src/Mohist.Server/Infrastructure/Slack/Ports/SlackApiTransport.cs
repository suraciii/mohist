using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Mohist.Server.Infrastructure.Slack.Ports;

/// <summary>
/// Minimal Slack Web API transport for the control-plane outbound port
/// adapters. Endpoints are relative to the configured base URL; responses
/// are classified so adapters can map Slack semantics onto their port
/// outcomes without touching HTTP shapes. Caller cancellation propagates;
/// timeouts and transport failures surface as <see cref="SlackApiCallOutcome.TransportError"/>.
/// A rate-limited response is classified as
/// <see cref="SlackApiCallOutcome.RateLimited"/> and carries the provider's
/// requested delay when Slack supplied one.
/// </summary>
public sealed class SlackApiTransport(HttpClient http)
{
    public async Task<SlackApiResponse> PostFormAsync(
        string endpoint,
        IReadOnlyDictionary<string, string>? form,
        string? bearerToken,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (form is { Count: > 0 })
            request.Content = new FormUrlEncodedContent(form);
        if (!string.IsNullOrWhiteSpace(bearerToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return SlackApiResponse.TransportError;
        }
        catch (HttpRequestException)
        {
            return SlackApiResponse.TransportError;
        }

        using (response)
        {
            var grantedScopesHeader = ReadGrantedScopesHeader(response);
            var retryAfter = ReadRetryAfterHeader(response);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return SlackApiResponse.RateLimited(ExtractError(body) ?? "ratelimited", retryAfter);
            if (response.StatusCode != HttpStatusCode.OK)
                return SlackApiResponse.Rejected(ExtractError(body) ?? $"http_{(int)response.StatusCode}");

            JsonDocument? document = null;
            try
            {
                document = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                document?.Dispose();
                return SlackApiResponse.Unparseable;
            }

            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                return SlackApiResponse.Unparseable;
            }

            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
            {
                var error = root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.String
                    ? errorElement.GetString()
                    : null;
                document.Dispose();
                // Slack reports some limits inside an HTTP 200 envelope.
                if (string.Equals(error, "ratelimited", StringComparison.Ordinal))
                    return SlackApiResponse.RateLimited("ratelimited", retryAfter);
                return SlackApiResponse.Rejected(error ?? "unknown_error");
            }

            return SlackApiResponse.Ok(document, grantedScopesHeader);
        }
    }

    private static string? ReadGrantedScopesHeader(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("x-oauth-scopes", out var values))
            return null;
        var joined = string.Join(",", values.Where(value => !string.IsNullOrWhiteSpace(value)));
        return string.IsNullOrWhiteSpace(joined) ? null : joined;
    }

    /// <summary>
    /// Slack sends the whole-second retry delay in <c>Retry-After</c>; an
    /// absent or unparseable value leaves the delay unknown without hiding
    /// the rate limit itself.
    /// </summary>
    private static TimeSpan? ReadRetryAfterHeader(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Retry-After", out var values))
            return null;
        var raw = values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (raw is null)
            return null;
        return int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            && seconds >= 0
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }

    private static string? ExtractError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String)
                return error.GetString();
        }
        catch (JsonException)
        {
        }

        return null;
    }
}

public enum SlackApiCallOutcome
{
    Ok,
    Rejected,
    Unparseable,
    TransportError,
    /// <summary>Slack refused the call for rate limiting; see the response's retry delay.</summary>
    RateLimited,
}

public sealed record SlackApiResponse(
    SlackApiCallOutcome Outcome,
    JsonDocument? Body = null,
    string? Error = null,
    string? GrantedScopesHeader = null,
    TimeSpan? RetryAfter = null)
{
    public static SlackApiResponse Ok(JsonDocument body, string? grantedScopesHeader = null) =>
        new(SlackApiCallOutcome.Ok, body, GrantedScopesHeader: grantedScopesHeader);

    public static SlackApiResponse Rejected(string error) => new(SlackApiCallOutcome.Rejected, Error: error);

    public static SlackApiResponse RateLimited(string error, TimeSpan? retryAfter) =>
        new(SlackApiCallOutcome.RateLimited, Error: error, RetryAfter: retryAfter);

    public static SlackApiResponse Unparseable { get; } = new(SlackApiCallOutcome.Unparseable);

    public static SlackApiResponse TransportError { get; } = new(SlackApiCallOutcome.TransportError);
}
