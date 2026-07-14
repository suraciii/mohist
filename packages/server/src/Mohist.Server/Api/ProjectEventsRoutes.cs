using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Mohist.Server.AgentOps.Services;

namespace Mohist.Server.Api;

/// <summary>
/// Project-scoped read endpoint for the Activity evidence feed
/// (issue-402 T-000). The route surfaces recorded issue, workflow, and
/// agent-session CloudEvents with persisted session lifecycle transcript facts,
/// without changing how events are recorded, emitted, or subscribed. The
/// endpoint is read-only and does not introduce event-subscription or
/// event-stream behaviour.
/// </summary>
public static class ProjectEventsRoutes
{
    private const int DefaultLimit = 200;
    private const int MaxLimit = 1000;

    public static WebApplication MapProjectEventsRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectRef}/events")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        group.MapGet("", async (HttpContext context, int? limit, string? types, bool? attentionOnly, ProjectEventFeedAssembler events, CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            var effectiveLimit = ClampLimit(limit);
            if (!ProjectEventFilter.TryCreate(types, attentionOnly == true, out var filter))
                return ApiResults.BadRequest("types contains an unsupported event category", "invalid_event_types");
            var eventsResult = await events.ListAsync(project.Id, effectiveLimit, filter, ct);
            var response = eventsResult.Select(ProjectEventDto.From).ToList();
            return ApiResults.Ok(response);
        });

        return app;
    }

    private static int ClampLimit(int? requested)
    {
        if (requested is null || requested <= 0) return DefaultLimit;
        return Math.Min(requested.Value, MaxLimit);
    }
}

/// <summary>
/// Wire-level DTO for the project-scoped event endpoint
/// (<c>GET /api/projects/&#123;projectRef&#125;/events</c>). Carries the
/// CloudEvent envelope identity plus the projection fields the Web layer
/// needs to classify entries (<see cref="Origin"/>,
/// <see cref="SourceAggregateKind"/>, <see cref="SourceAggregateId"/>,
/// <see cref="RunnerId"/>) without re-reading the raw envelope.
/// </summary>
/// <remarks>
/// Field names are the Web-facing contract: <c>origin</c>,
/// <c>sourceAggregateKind</c>, <c>sourceAggregateId</c>, <c>runnerId</c>.
/// Domain vocabulary is preserved as-is (no raw implementation field names
/// such as internal column identifiers leak into the response).
/// </remarks>
public sealed record ProjectEventDto(
    long Id,
    string Origin,
    string SourceAggregateKind,
    string SourceAggregateId,
    string Source,
    string Type,
    string Time,
    string EnvelopeId,
    string SpecVersion,
    string? Subject,
    string? DataContentType,
    JsonElement Data,
    string? RunnerId,
    int? IssueNumber,
    string? SessionSourceKind,
    string? WorkflowRunId,
    string? AgentId,
    string? AgentName)
{
    public static ProjectEventDto From(ProjectEventEnvelope envelope) =>
        new(
            envelope.Id,
            envelope.Origin.ToString().ToLowerInvariant(),
            envelope.SourceAggregateKind,
            envelope.SourceAggregateId,
            envelope.Source,
            envelope.Type,
            envelope.Time.ToString("o"),
            envelope.EnvelopeId,
            envelope.SpecVersion,
            envelope.Subject,
            envelope.DataContentType,
            ActivityData(envelope.Data),
            envelope.RunnerId,
            envelope.IssueNumber,
            envelope.SessionSourceKind,
            envelope.WorkflowRunId,
            envelope.AgentId,
            envelope.AgentName);

    private static readonly HashSet<string> ActivityDataFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "agentId", "agentName", "checkName", "coderSessionId", "failureCategory",
        "failureReason", "issueNo", "issueNumber", "issue_number", "message", "reason",
        "runnerId", "sessionId", "stage", "stageName", "status", "taskId", "title",
    };

    private static JsonElement ActivityData(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
            return JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>());

        var result = new Dictionary<string, JsonElement>();
        foreach (var property in data.EnumerateObject())
        {
            if (ActivityDataFields.Contains(property.Name)) result[property.Name] = property.Value.Clone();
        }
        return JsonSerializer.SerializeToElement(result);
    }
}
