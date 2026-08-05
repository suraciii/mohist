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
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
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
                return SlackApiResponse.Rejected(error ?? "unknown_error");
            }

            return SlackApiResponse.Ok(document);
        }
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
}

public sealed record SlackApiResponse(
    SlackApiCallOutcome Outcome,
    JsonDocument? Body = null,
    string? Error = null)
{
    public static SlackApiResponse Ok(JsonDocument body) => new(SlackApiCallOutcome.Ok, body);

    public static SlackApiResponse Rejected(string error) => new(SlackApiCallOutcome.Rejected, Error: error);

    public static SlackApiResponse Unparseable { get; } = new(SlackApiCallOutcome.Unparseable);

    public static SlackApiResponse TransportError { get; } = new(SlackApiCallOutcome.TransportError);
}
