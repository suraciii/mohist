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

    public async Task<WorkItem?> PollAsync(CancellationToken ct)
    {
        var resp = await _http.PostAsync($"/api/runner/{_runnerId}/poll", null, ct);

        if (resp.StatusCode == System.Net.HttpStatusCode.NoContent)
            return null;

        resp.EnsureSuccessStatusCode();

        var dispatch = await resp.Content.ReadFromJsonAsync<WorkDispatchResponse>(ct);
        if (dispatch is null) return null;

        return new WorkItem(
            dispatch.RunId,
            dispatch.Stage,
            dispatch.WorkId,
            dispatch.WorkType,
            dispatch.Uses,
            dispatch.With);
    }

    public async Task ReportAsync(string workId, WorkItemResult result, CancellationToken ct)
    {
        var resp = await _http.PostAsJsonAsync($"/api/runner/{_runnerId}/report",
            new { workId, result.Status, result.Message, result.Output, result.ExitCode }, ct);

        resp.EnsureSuccessStatusCode();
    }
}

public record WorkDispatchResponse(
    string RunId,
    string Stage,
    string WorkId,
    string WorkType,
    string? Uses,
    Dictionary<string, JsonElement?>? With);
