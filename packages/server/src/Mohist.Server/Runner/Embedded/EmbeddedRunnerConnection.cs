using System.Text.Json;
using Mohist.Runner.Transport;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions;

namespace Mohist.Server.Runner.Embedded;

public sealed class EmbeddedRunnerConnection : IServerConnection
{
    private readonly IGrainFactory _grains;
    private readonly AgentSessionService _sessions;
    private readonly ILogger<EmbeddedRunnerConnection> _log;
    private readonly string _runnerId;
    private bool _registered;

    public EmbeddedRunnerConnection(IGrainFactory grains, AgentSessionService sessions, ILogger<EmbeddedRunnerConnection> log, string runnerId)
    {
        _grains = grains;
        _sessions = sessions;
        _log = log;
        _runnerId = runnerId;
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        var runner = _grains.GetGrain<IRunnerGrain>(_runnerId);
        await runner.RegisterAsync(new RunnerInfo(_runnerId, [], Environment.MachineName));
        _registered = true;
        _log.LogInformation("Embedded runner {RunnerId} registered", _runnerId);
    }

    public async Task HeartbeatAsync(CancellationToken ct)
    {
        if (!_registered) return;
        await _grains.GetGrain<IRunnerGrain>(_runnerId).HeartbeatAsync();
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        if (!_registered) return;
        await _grains.GetGrain<IRunnerGrain>(_runnerId).UnregisterAsync();
        _registered = false;
        _log.LogInformation("Embedded runner {RunnerId} unregistered", _runnerId);
    }

    public async Task<WorkItem?> PollAsync(CancellationToken ct)
    {
        var dispatch = await _grains.GetGrain<IRunnerGrain>(_runnerId).PollAsync();
        if (dispatch is null) return null;

        var session = await _sessions.CreateForDispatchAsync(_runnerId, dispatch, ct);

        return new WorkItem(
            dispatch.WorkflowRunId,
            dispatch.WorkId,
            dispatch.WorkType,
            dispatch.Stage,
            dispatch.Title,
            dispatch.Uses,
            ParseJson(dispatch.With),
            ParseJson(dispatch.Variables),
            session is null
                ? null
                : new AgentSessionContext(
                    session.Id,
                    session.ProjectId,
                    session.IssueNumber,
                    session.WorkflowRunId,
                    session.WorkId,
                    session.Stage,
                    session.Title,
                    session.ExternalSessionId));
    }

    public async Task ReportAsync(WorkItem workItem, WorkItemResult result, CancellationToken ct)
    {
        await _grains.GetGrain<IRunnerGrain>(_runnerId).ReportAsync(
            workItem.WorkId,
            new WorkDispatchResult(result.Status, result.Message, result.Output, result.ExitCode));
    }

    private static Dictionary<string, JsonElement?>? ParseJson(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>(value);
    }
}
