using System.Net.Http.Json;
using System.Text.Json;

namespace Mohist.Server.SpecTests.Support;

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

    /// <summary>
    /// Issue-512 T-001: POST a manual AgentSession launch with an
    /// Idempotency-Key header. The route now requires the header
    /// (the coordinator owns the durable launch identity). Tests
    /// that want to omit the header (e.g. to assert the 400
    /// missing-header gate) call <see cref="LaunchAgentSessionWithoutIdempotencyKeyAsync"/>.
    /// </summary>
    public static Task<HttpResponseMessage> LaunchAgentSessionAsync(
        this HttpClient client,
        string projectId,
        string agentId,
        object body,
        string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{projectId}/agents/{agentId}/sessions")
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString("N"));
        return client.SendAsync(request);
    }

    /// <summary>
    /// Issue-512 T-001: POST a manual AgentSession launch WITHOUT
    /// an Idempotency-Key header. Used to assert the 400
    /// missing-header rejection gate.
    /// </summary>
    public static Task<HttpResponseMessage> LaunchAgentSessionWithoutIdempotencyKeyAsync(
        this HttpClient client,
        string projectId,
        string agentId,
        object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{projectId}/agents/{agentId}/sessions")
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        return client.SendAsync(request);
    }

    public static async Task<T> PostMultipartDataAsync<T>(this HttpClient client, string path, HttpContent body)
    {
        using var response = await client.PostAsync(path, body);
        await EnsureSuccessWithBodyAsync(response);
        return await ReadDataAsync<T>(response);
    }

    /// <summary>
    /// Creates a project with its initial repository in one request.
    /// </summary>
    public static async Task<T> PostProjectWithRepositoryAsync<T>(
        this HttpClient client,
        string projectPath,
        object projectBody,
        object repositoryBody)
    {
        var request = JsonSerializer.SerializeToNode(projectBody, JsonOptions)?.AsObject()
            ?? throw new InvalidOperationException("Project request must be a JSON object");
        request["repository"] = JsonSerializer.SerializeToNode(repositoryBody, JsonOptions);
        return await PostDataAsync<T>(client, projectPath, request);
    }

    /// <summary>
    /// Issue-416 / T-001: POST a project with a placeholder default
    /// repository in one request so tests do not need to construct the new
    /// request contract inline. The repository declaration is a fixed
    /// non-functional metadata — tests that exercise repository semantics
    /// construct their own.
    /// </summary>
    public static async Task<T> CreateProjectWithDefaultRepositoryAsync<T>(
        this HttpClient client,
        string projectPath,
        string projectName,
        string? repoName = null,
        string gitUrl = "git@example.com:test-repo.git",
        string baseBranch = "main")
    {
        var body = new
        {
            name = projectName,
            repository = new
            {
                name = repoName ?? "test-repo",
                gitUrl,
                baseBranch,
            },
        };
        return await PostDataAsync<T>(client, projectPath, body);
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

    /// <summary>
    /// Polls <paramref name="path"/> via GET until <paramref name="statusSelector"/>
    /// returns <paramref name="expectedStatus"/>, or throws on timeout.
    /// Used by spec tests that depend on the dispatcher's auto-mark-done
    /// event handler firing after an issue completion — the handler is
    /// eventually-consistent (Orleans reminder + best-effort poke), so
    /// tests must wait for it rather than race it with an explicit call.
    /// </summary>
    public static async Task<T> WaitForStatusAsync<T>(
        this HttpClient client,
        string path,
        Func<T, string?> statusSelector,
        string expectedStatus,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null)
        => await client.WaitForAsync<T>(
            path,
            resource => string.Equals(statusSelector(resource), expectedStatus, StringComparison.Ordinal),
            $"status '{expectedStatus}'",
            timeout,
            pollInterval);

    public static async Task<T> WaitForAsync<T>(
        this HttpClient client,
        string path,
        Func<T, bool> predicate,
        string expected,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null)
    {
        var budget = timeout ?? TimeSpan.FromSeconds(10);
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(50);
        var attempts = Math.Max(1, (int)Math.Ceiling(
            budget.TotalMilliseconds / Math.Max(1, interval.TotalMilliseconds)));
        T? latest = default;

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                latest = await client.GetDataAsync<T>(path);
                if (predicate(latest))
                    return latest;
            }
            catch
            {
            }
        }

        throw new TimeoutException(
            $"Resource at '{path}' did not reach {expected} after {attempts} probes.");
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
