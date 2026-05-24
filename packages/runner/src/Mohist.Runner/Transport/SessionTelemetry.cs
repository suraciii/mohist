using System.Net.Http.Json;
using System.Text.Json;

namespace Mohist.Runner.Transport;

public interface ISessionTelemetrySink
{
    Task StartedAsync(AgentSessionContext session, SessionStarted started, CancellationToken ct);
    Task AppendAsync(AgentSessionContext session, IReadOnlyList<SessionEventInput> events, CancellationToken ct);
    Task CompletedAsync(AgentSessionContext session, SessionCompleted completed, CancellationToken ct);
}

public sealed class NullSessionTelemetrySink : ISessionTelemetrySink
{
    public Task StartedAsync(AgentSessionContext session, SessionStarted started, CancellationToken ct) => Task.CompletedTask;
    public Task AppendAsync(AgentSessionContext session, IReadOnlyList<SessionEventInput> events, CancellationToken ct) => Task.CompletedTask;
    public Task CompletedAsync(AgentSessionContext session, SessionCompleted completed, CancellationToken ct) => Task.CompletedTask;
}

public sealed class HttpSessionTelemetrySink : ISessionTelemetrySink
{
    private readonly HttpClient _http;
    private readonly string _runnerId;

    public HttpSessionTelemetrySink(HttpClient http, string runnerId)
    {
        _http = http;
        _runnerId = runnerId;
    }

    public async Task StartedAsync(AgentSessionContext session, SessionStarted started, CancellationToken ct)
    {
        var resp = await _http.PostAsJsonAsync($"/api/runner/{_runnerId}/sessions/{session.Id}/started", started, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task AppendAsync(AgentSessionContext session, IReadOnlyList<SessionEventInput> events, CancellationToken ct)
    {
        if (events.Count == 0) return;
        var resp = await _http.PostAsJsonAsync($"/api/runner/{_runnerId}/sessions/{session.Id}/events", new { Events = events }, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task CompletedAsync(AgentSessionContext session, SessionCompleted completed, CancellationToken ct)
    {
        var resp = await _http.PostAsJsonAsync($"/api/runner/{_runnerId}/sessions/{session.Id}/completed", completed, ct);
        resp.EnsureSuccessStatusCode();
    }
}

public sealed record SessionStarted(string? ExternalSessionId = null, string? Model = null, string? WorkDir = null, string? ChangeDir = null, int? ProcessPid = null);
public sealed record SessionEventInput(string Type, JsonElement Payload);
public sealed record SessionCompleted(string Status, string? FailureReason = null, int? ExitCode = null);

public sealed record AgentSessionContext(
    string Id,
    string ProjectId,
    int IssueNumber,
    string WorkflowRunId,
    string WorkId,
    string? Stage,
    string? Title,
    string? ExternalSessionId = null);
