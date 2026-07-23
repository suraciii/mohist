using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Runner.Services;
using Mohist.Server.Sessions;

namespace Mohist.Server.AgentOps.Services;

public sealed class ActivityEvidenceAssembler : IScopedService
{
    private const int SourceLimit = 200;
    private readonly ProjectEventFeedAssembler _events;
    private readonly AgentActivityFeedAssembler _agentActivity;
    private readonly ActivityWaitingProjection _waiting;
    private readonly RunnerStatusService _runnerStatus;

    public ActivityEvidenceAssembler(
        ProjectEventFeedAssembler events,
        AgentActivityFeedAssembler agentActivity,
        ActivityWaitingProjection waiting,
        RunnerStatusService runnerStatus)
    {
        _events = events;
        _agentActivity = agentActivity;
        _waiting = waiting;
        _runnerStatus = runnerStatus;
    }

    public async Task<IReadOnlyList<ActivityEntryDto>> ListAsync(string projectId, int limit, CancellationToken ct = default)
    {
        var waiting = await _waiting.ListAsync(projectId, ct);
        var runners = await _runnerStatus.GetRunnersAsync(projectId);
        var capacity = SumCapacity(runners);
        var activity = await _agentActivity.GetActivityAsync(projectId, SourceLimit, waiting, capacity, ct);
        var events = await _events.ListAsync(projectId, SourceLimit, ct: ct);

        return events.Select(ActivityEntryDto.FromRecorded)
            .Concat(activity.Sessions.Select(ActivityEntryDto.FromSession))
            .Concat(activity.Waiting.Select(ActivityEntryDto.FromWaiting))
            .Concat(runners.Select(ActivityEntryDto.FromRunner))
            .OrderByDescending(entry => entry.Time)
            .ThenBy(entry => entry.Id, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }

    private static RunnerCapacityView SumCapacity(IReadOnlyList<RunnerStatusView> runners) =>
        new(
            runners.Sum(runner => runner.Capacity?.UsedSlots ?? 0),
            runners.Sum(runner => runner.Capacity?.TotalSlots ?? 0));
}

public sealed record ActivityEntryDto(
    string Id,
    string Provenance,
    string Scope,
    string Kind,
    DateTimeOffset Time,
    string Title,
    string Description,
    string? EventType = null,
    int? IssueNumber = null,
    string? WorkflowRunId = null,
    string? SessionId = null,
    string? RunnerId = null,
    string? Status = null)
{
    public static ActivityEntryDto FromRecorded(ProjectEventEnvelope entry) =>
        new(
            entry.EnvelopeId,
            "recorded",
            "project",
            entry.SourceAggregateKind,
            entry.Time,
            RecordedTitle(entry),
            entry.Type,
            entry.Type,
            entry.IssueNumber,
            entry.WorkflowRunId ?? (entry.SourceAggregateKind == "workflow-run" ? entry.SourceAggregateId : null),
            entry.SourceAggregateKind == "agent-session" ? entry.SourceAggregateId : null,
            entry.RunnerId,
            ReadStatus(entry));

    public static ActivityEntryDto FromSession(ActivityCardDto entry) =>
        new(
            $"snapshot:agent-session:{entry.SessionId}",
            "snapshot",
            "project",
            "agent-session",
            ParseTime(entry.LastActivityAt),
            entry.Title ?? $"Agent session {entry.SessionId}",
            $"{entry.Status} session for Issue #{entry.IssueNumber}",
            IssueNumber: entry.IssueNumber,
            SessionId: entry.SessionId,
            Status: entry.Status);

    public static ActivityEntryDto FromWaiting(ActivityWaitingCardDto entry) =>
        new(
            $"snapshot:waiting:issue:{entry.IssueNumber}",
            "snapshot",
            "project",
            "waiting",
            ParseTime(entry.RequestedAt),
            entry.IssueTitle,
            $"{entry.Label}{(string.IsNullOrWhiteSpace(entry.Stage) ? string.Empty : $" at {entry.Stage}")}",
            IssueNumber: entry.IssueNumber,
            Status: "waiting");

    public static ActivityEntryDto FromRunner(RunnerStatusView entry) =>
        new(
            $"snapshot:runner:{entry.Id}",
            "snapshot",
            "global",
            "runner",
            entry.LastHeartbeatAt ?? entry.RegisteredAt ?? DateTimeOffset.MinValue,
            $"Runner {entry.Id}",
            $"{entry.Kind} runner on {entry.Hostname}",
            RunnerId: entry.Id,
            Status: entry.Status);

    private static string RecordedTitle(ProjectEventEnvelope entry) => entry.SourceAggregateKind switch
    {
        "issue" => entry.IssueNumber is int issueNumber ? $"Issue #{issueNumber}" : $"Issue {entry.SourceAggregateId}",
        "workflow-run" => $"Workflow run {entry.SourceAggregateId}",
        "agent-session" => $"Agent session {entry.SourceAggregateId}",
        _ => entry.SourceAggregateId,
    };

    private static string? ReadStatus(ProjectEventEnvelope entry) =>
        entry.Data.ValueKind == System.Text.Json.JsonValueKind.Object
        && entry.Data.TryGetProperty("status", out var status)
        && status.ValueKind == System.Text.Json.JsonValueKind.String
            ? status.GetString()
            : null;

    private static DateTimeOffset ParseTime(string? value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed : DateTimeOffset.MinValue;
}
