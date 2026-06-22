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

    public static async Task<string> GetRawAsync(this HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    public static async Task<T> PostDataAsync<T>(this HttpClient client, string path, object? body = null)
    {
        using var response = body is null
            ? await client.PostAsync(path, null)
            : await client.PostAsJsonAsync(path, body, JsonOptions);
        await EnsureSuccessWithBodyAsync(response);
        return await ReadDataAsync<T>(response);
    }

    public static async Task<T> PostMultipartDataAsync<T>(this HttpClient client, string path, HttpContent body)
    {
        using var response = await client.PostAsync(path, body);
        await EnsureSuccessWithBodyAsync(response);
        return await ReadDataAsync<T>(response);
    }

    /// <summary>
    /// Creates a project and immediately attaches a single default repository.
    /// Tests that need a project to be issue-startable use this helper instead
    /// of POSTing a name-only project (which is the new contract) and then
    /// issuing against an empty repository set.
    /// </summary>
    public static async Task<T> PostProjectWithRepositoryAsync<T>(
        this HttpClient client,
        string projectPath,
        object projectBody,
        object repositoryBody)
    {
        var project = await PostDataAsync<T>(client, projectPath, projectBody);
        var idProperty = typeof(T).GetProperty("Id");
        var projectId = idProperty?.GetValue(project) as string
            ?? throw new InvalidOperationException("Project response must have an Id property");
        await client.PostAsJsonAsync(
            $"{projectPath}/{projectId}/repositories",
            repositoryBody,
            JsonOptions);
        return project;
    }

    public static async Task PostOkAsync(this HttpClient client, string path, object? body = null)
    {
        using var response = body is null
            ? await client.PostAsync(path, null)
            : await client.PostAsJsonAsync(path, body, JsonOptions);
        await EnsureSuccessWithBodyAsync(response);
    }

    public static async Task PutAsJsonOkAsync(this HttpClient client, string path, object body)
    {
        using var response = await client.PutAsJsonAsync(path, body, JsonOptions);
        await EnsureSuccessWithBodyAsync(response);
    }

    public static async Task<T> PatchDataAsync<T>(this HttpClient client, string path, object body)
    {
        using var response = await client.PatchAsJsonAsync(path, body, JsonOptions);
        await EnsureSuccessWithBodyAsync(response);
        return await ReadDataAsync<T>(response);
    }

    public static async Task PatchOkAsync(this HttpClient client, string path, object body)
    {
        using var response = await client.PatchAsJsonAsync(path, body, JsonOptions);
        await EnsureSuccessWithBodyAsync(response);
    }

    public static async Task<T> ReadDataAsync<T>(this HttpResponseMessage response)
    {
        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<T>>(JsonOptions);
        if (envelope is null)
            throw new InvalidOperationException("Empty API response");
        if (!envelope.Success)
            throw new InvalidOperationException(envelope.Error ?? "API request failed");
        return envelope.Data!;
    }

    private static async Task EnsureSuccessWithBodyAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync();
        throw new HttpRequestException(
            $"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}). Body: {body}",
            inner: null,
            response.StatusCode);
    }

    private sealed record ApiEnvelope<T>(bool Success, T? Data, string? Error = null, string? Code = null, object? Details = null);
}
