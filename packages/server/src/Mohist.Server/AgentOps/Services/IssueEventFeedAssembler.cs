using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.AgentOps.Services;

public sealed class IssueEventFeedAssembler : IScopedService
{
    private readonly IEventStore _events;
    private readonly WorkflowEventQuerier _workflowEvents;
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public IssueEventFeedAssembler(
        IEventStore events,
        WorkflowEventQuerier workflowEvents,
        IDbContextFactory<MohistDbContext> dbFactory)
    {
        _events = events;
        _workflowEvents = workflowEvents;
        _dbFactory = dbFactory;
    }

    public async Task<IReadOnlyList<StoredCloudEvent>> ListAsync(
        string projectId,
        int issueNumber,
        string? workflowRunId,
        int limit,
        CancellationToken ct = default)
    {
        var effectiveLimit = limit > 0 ? limit : 200;
        var issueEvents = await _events.ListIssueEventsAsync(projectId, issueNumber, int.MaxValue, ct);
        var workflowEvents = workflowRunId is null
            ? []
            : await _workflowEvents.ListValidWorkflowEventsAsync(workflowRunId, ct);
        var sessionEvents = await ListRoutedFailuresAsync(projectId, issueNumber, ct);

        return SelectNewest(
            issueEvents,
            workflowEvents,
            sessionEvents,
            effectiveLimit);
    }

    internal static IReadOnlyList<StoredCloudEvent> SelectNewest(
        IReadOnlyList<StoredCloudEvent> issueEvents,
        IReadOnlyList<StoredCloudEvent> workflowEvents,
        IReadOnlyList<StoredCloudEvent> sessionEvents,
        int limit)
    {
        var candidates = issueEvents.Select(e => new Candidate(0, e))
            .Concat(workflowEvents.Select(e => new Candidate(1, e)))
            .Concat(sessionEvents.Select(e => new Candidate(2, e)))
            .OrderByDescending(e => e.Event.Envelope.Time)
            .ThenByDescending(e => e.OriginRank)
            .ThenByDescending(e => e.Event.Envelope.Source.ToString(), StringComparer.Ordinal)
            .ThenByDescending(e => e.Event.Id)
            .ThenByDescending(e => e.Event.Envelope.Id, StringComparer.Ordinal)
            .Take(limit)
            .OrderBy(e => e.Event.Envelope.Time)
            .ThenBy(e => e.OriginRank)
            .ThenBy(e => e.Event.Envelope.Source.ToString(), StringComparer.Ordinal)
            .ThenBy(e => e.Event.Id)
            .ThenBy(e => e.Event.Envelope.Id, StringComparer.Ordinal)
            .Select(e => e.Event)
            .ToList();

        return candidates;
    }

    private async Task<IReadOnlyList<StoredCloudEvent>> ListRoutedFailuresAsync(
        string projectId,
        int issueNumber,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await (
            from part in db.AgentSessionTranscriptParts.AsNoTracking()
            join turn in db.AgentSessionTranscriptTurns.AsNoTracking() on part.TurnId equals turn.Id
            join session in db.AgentSessions.AsNoTracking() on turn.SessionId equals session.Id
            where session.LabelProjectId == projectId
                && session.LabelSourceKind == "agent-launch"
                && session.LabelAgentLaunchIssueNumber == issueNumber.ToString()
                && session.LabelTriggerEventId != null
                && session.LabelTriggerEventId != ""
                && session.LabelTriggerRuleId != null
                && session.LabelTriggerRuleId != ""
                 && part.Type == TranscriptPartTypes.SessionActivity
                && part.PayloadStatus == "failed"
                && part.CorrelationKey.StartsWith("agent-job:")
                && part.CorrelationKey.EndsWith(":terminal")
                && part.CorrelationId == part.CorrelationKey
            select new { Part = part, Session = session })
            .ToListAsync(ct);

        return rows
            .Select(row => ProjectRoutedFailure(row.Session, row.Part))
            .Where(static candidate => candidate is not null)
            .Select(static candidate => candidate!)
            .GroupBy(static candidate => candidate.Envelope.Subject!, StringComparer.Ordinal)
            .Select(static group => group
                .OrderByDescending(candidate => candidate.Id)
                .ThenByDescending(candidate => candidate.Envelope.Id, StringComparer.Ordinal)
                .First())
            .ToList();
    }

    internal static StoredCloudEvent? ProjectRoutedFailure(
        AgentSessionRow session,
        AgentSessionTranscriptPartRow part)
    {
        JsonElement payload;
        try
        {
            payload = JSON.DeserializeElement(part.PayloadJson);
        }
        catch
        {
            return null;
        }

        if (payload.ValueKind != JsonValueKind.Object
            || !TryGetString(payload, "deliveryId", out var deliveryId)
            || !string.Equals(deliveryId, part.CorrelationKey, StringComparison.Ordinal)
            || !string.Equals(deliveryId, part.CorrelationId, StringComparison.Ordinal)
            || !IsAgentJobTerminalDeliveryId(deliveryId)
            || !TryGetString(payload, "status", out var status)
            || !string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
            return null;

        var sessionId = session.Id;
        var data = JsonSerializer.SerializeToElement(new
        {
            deliveryId,
            status,
            exitCode = GetNullableInt(payload, "exitCode"),
            failureReason = GetNullableString(payload, "failureReason"),
            failureCategory = GetNullableString(payload, "failureCategory"),
            recordedAt = GetNullableString(payload, "recordedAt"),
            agentJobId = GetNullableString(payload, "agentJobId"),
            sessionId,
            agentId = session.LabelAgentId,
            agentName = session.LabelAgentName,
            triggerEventId = session.LabelTriggerEventId,
            triggerRuleId = session.LabelTriggerRuleId,
        }, JSON.Options);
        var extensions = new Dictionary<string, string>
        {
            [EventCatalog.Lineage.ProjectId] = session.LabelProjectId!,
            [EventCatalog.Lineage.Issue] = session.LabelAgentLaunchIssueNumber!,
        };
        var envelope = new CloudEvent(
            id: $"{sessionId}:closed:{deliveryId}",
            source: new Uri(AgentSessionEventPersistence.AgentSessionSource(sessionId), UriKind.Relative),
            type: "session.closed",
            time: new DateTimeOffset(DateTime.SpecifyKind(part.LastSeenAt, DateTimeKind.Utc)),
            data: data,
            dataContentType: "application/json",
            subject: sessionId,
            specVersion: "1.0",
            extensions: extensions);

        return new StoredCloudEvent(part.Id, envelope);
    }

    private static bool TryGetString(JsonElement payload, string name, out string value)
    {
        value = GetNullableString(payload, name) ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool IsAgentJobTerminalDeliveryId(string value) =>
        value.StartsWith("agent-job:", StringComparison.Ordinal)
        && value.EndsWith(":terminal", StringComparison.Ordinal)
        && value.Length > "agent-job::terminal".Length;

    private static string? GetNullableString(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetNullableInt(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
                ? number
                : null;

    private sealed record Candidate(int OriginRank, StoredCloudEvent Event);
}
