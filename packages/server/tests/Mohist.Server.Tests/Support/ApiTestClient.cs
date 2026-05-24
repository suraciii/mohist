using System.Net.Http.Json;
using System.Text.Json;

namespace Mohist.Server.Tests.Support;

public static class ApiTestClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<T> GetDataAsync<T>(this HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await ReadDataAsync<T>(response);
    }

    public static async Task<T> PostDataAsync<T>(this HttpClient client, string path, object? body = null)
    {
        using var response = body is null
            ? await client.PostAsync(path, null)
            : await client.PostAsJsonAsync(path, body, JsonOptions);
        response.EnsureSuccessStatusCode();
        return await ReadDataAsync<T>(response);
    }

    public static async Task PostOkAsync(this HttpClient client, string path, object? body = null)
    {
        using var response = body is null
            ? await client.PostAsync(path, null)
            : await client.PostAsJsonAsync(path, body, JsonOptions);
        response.EnsureSuccessStatusCode();
    }

    public static async Task PutAsJsonOkAsync(this HttpClient client, string path, object body)
    {
        using var response = await client.PutAsJsonAsync(path, body, JsonOptions);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<T> ReadDataAsync<T>(HttpResponseMessage response)
    {
        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<T>>(JsonOptions);
        if (envelope is null)
            throw new InvalidOperationException("Empty API response");
        if (!envelope.Success)
            throw new InvalidOperationException(envelope.Error ?? "API request failed");
        return envelope.Data!;
    }

    private sealed record ApiEnvelope<T>(bool Success, T? Data, string? Error = null, string? Code = null);
}
