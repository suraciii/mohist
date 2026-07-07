using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Mohist.Server.SpecTests.Support;

/// <summary>
/// Test-side HTTP poll client for the runner <c>POST /api/runner/{id}/poll</c>
/// endpoint. After the scheduling refactor (epic #44) the poll response is no
/// longer a single dispatch object: a successful (HTTP 200) response carries
/// <c>{ "dispatches": [ WorkDispatchResponse, ... ] }</c> (a
/// <c>RunnerPollResponseDto</c>), and an empty round is returned as HTTP 204 No
/// Content. These helpers unwrap the new envelope so spec call sites read like
/// the old single-dispatch contract.
/// </summary>
public static class RunnerPollClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Reads a 200 poll body, unwraps <c>dispatches</c>, and returns the first
    /// dispatch typed as <typeparamref name="T"/>. Returns null for 204 No
    /// Content (and for a 200 carrying an empty list), mirroring the old
    /// "no work this round" return shape.
    /// </summary>
    public static async Task<T?> ReadFirstDispatchAsync<T>(this HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.NoContent) return default;

        response.EnsureSuccessStatusCode();
        var envelope = await response.Content.ReadFromJsonAsync<RunnerPollEnvelope<T>>(JsonOptions);
        if (envelope is null) return default;
        return envelope.Dispatches is { Count: > 0 } list ? list[0] : default;
    }

    /// <summary>
    /// Reads a 200 poll body, unwraps <c>dispatches</c>, and returns the first
    /// dispatch as a raw <see cref="JsonElement"/> (or null for 204 / empty).
    /// For specs that inspect arbitrary dispatch fields without a fixed DTO.
    /// </summary>
    public static async Task<JsonElement?> ReadFirstDispatchElementAsync(this HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.NoContent) return null;

        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("dispatches", out var dispatches) || dispatches.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var item in dispatches.EnumerateArray())
            return item.Clone();
        return null;
    }

    /// <summary>
    /// Reads a 200 poll body and returns every dispatch in <c>dispatches</c>
    /// as a raw <see cref="JsonElement"/> (empty for 204 / empty list). For
    /// specs that need to drain or inspect the full reconciliation round.
    /// </summary>
    public static async Task<List<JsonElement>> ReadDispatchElementsAsync(this HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.NoContent) return [];

        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var result = new List<JsonElement>();
        if (doc.RootElement.TryGetProperty("dispatches", out var dispatches) && dispatches.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in dispatches.EnumerateArray())
                result.Add(item.Clone());
        }
        return result;
    }

    private sealed record RunnerPollEnvelope<T>(List<T>? Dispatches);
}
