using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Runner.Transport;

namespace Mohist.Runner.Transport;

public class HttpServerConnection : IServerConnection
{
    private readonly HttpClient _http;
    private readonly string _runnerId;
    private readonly ILogger<HttpServerConnection> _log;
    private bool _registered;

    public HttpServerConnection(HttpClient http, string runnerId, ILogger<HttpServerConnection> log)
    {
        _http = http;
        _runnerId = runnerId;
        _log = log;
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        _log.LogInformation("Registering runner {Id} at {Server}", _runnerId, _http.BaseAddress);

        var resp = await _http.PostAsJsonAsync($"/api/runner/{_runnerId}/register",
            new { Capabilities = Array.Empty<string>(), Hostname = Environment.MachineName }, ct);

        resp.EnsureSuccessStatusCode();
        _registered = true;
        _log.LogInformation("Runner {Id} registered", _runnerId);
    }

    public async Task HeartbeatAsync(CancellationToken ct)
    {
        if (!_registered) return;

        var resp = await _http.PostAsync($"/api/runner/{_runnerId}/heartbeat", null, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        if (!_registered) return;

        var resp = await _http.PostAsync($"/api/runner/{_runnerId}/unregister", null, ct);
        resp.EnsureSuccessStatusCode();
        _registered = false;
    }

    public async Task<WorkItem?> PollAsync(CancellationToken ct)
    {
        var resp = await _http.PostAsync($"/api/runner/{_runnerId}/poll", null, ct);

        if (resp.StatusCode == System.Net.HttpStatusCode.NoContent)
            return null;

        resp.EnsureSuccessStatusCode();

        var dispatch = await resp.Content.ReadFromJsonAsync<WorkDispatchResponse>(ct);
        if (dispatch is null) return null;

        return new WorkItem(
            dispatch.WorkflowRunId,
            dispatch.WorkId,
            dispatch.WorkType,
            dispatch.Stage,
            dispatch.Title,
            dispatch.Uses,
            ParseWith(dispatch.With));
    }

    public async Task ReportAsync(WorkItem workItem, WorkItemResult result, CancellationToken ct)
    {
        var resp = await _http.PostAsJsonAsync($"/api/runner/{_runnerId}/report",
            new { workItem.WorkId, result.Status, result.Message, result.Output, result.ExitCode }, ct);

        resp.EnsureSuccessStatusCode();
    }

    private static Dictionary<string, JsonElement?>? ParseWith(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>(value);
    }
}

public record WorkDispatchResponse(
    string WorkflowRunId,
    string WorkId,
    string? Uses,
    string? With,
    string WorkType,
    string? Stage,
    string? Title);
