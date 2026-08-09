using System.Text.Json;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure;

namespace Mohist.Server.Api;

/// <summary>
/// Agent-scoped configuration facade over the project's ordered routing rules.
/// The facade owns the subscription contract; routing remains the execution
/// owner so the event matcher and dispatch arbitration have one source of truth.
/// </summary>
public static class AgentSubscriptionRoutes
{
    public static WebApplication MapAgentSubscriptionRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectRef}/agents/{agentRef}/subscriptions")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        group.MapGet("", async (
            HttpContext context,
            string agentRef,
            AgentQuerier agents,
            RoutingRuleStore rules,
            AgentConnectionStore connections,
            CancellationToken ct) =>
        {
            var projectId = context.GetResolvedProject().Id;
            var agent = await AgentRefResolver.ResolveAsync(agents, projectId, agentRef);
            if (agent is null)
                return ApiResults.NotFound($"Agent '{agentRef}' not found");

            var allRules = await rules.ListAsync(projectId, includeArchived: true, ct);
            var subscriptions = allRules
                .Where(rule => rule.AgentId == agent.Id)
                .Select(ToDto)
                .ToArray();
            var connectionRows = (await connections.ListAsync(projectId, ct: ct))
                .Where(connection => connection.AgentId == agent.Id)
                .ToArray();

            return ApiResults.Ok(new AgentSubscriptionListDto(
                subscriptions,
                DeriveState(agent, subscriptions, connectionRows),
                agent.Status,
                agent.Readiness?.Conclusion ?? AgentReadinessConclusions.Unknown,
                DeriveConnectionState(connectionRows)));
        });

        group.MapPost("", async (
            HttpContext context,
            string agentRef,
            AgentSubscriptionCreateRequest request,
            AgentQuerier agents,
            RoutingRuleStore rules,
            CancellationToken ct) =>
        {
            if (request is null) return ApiResults.BadRequest("request body required");
            var projectId = context.GetResolvedProject().Id;
            var agent = await AgentRefResolver.ResolveAsync(agents, projectId, agentRef);
            if (agent is null)
                return ApiResults.NotFound($"Agent '{agentRef}' not found");

            string? idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString().Trim();
            if (idempotencyKey.Length == 0) idempotencyKey = null;
            try
            {
                var existing = idempotencyKey is null
                    ? null
                    : await rules.GetByIdempotencyKeyAsync(projectId, idempotencyKey, ct);
                if (existing is not null)
                    return SameRequestValues(existing, agent.Id, request)
                        ? ApiResults.Ok(ToDto(existing))
                        : ApiResults.Conflict(
                            "The Idempotency-Key was already used for a different subscription.",
                            "idempotency_key_conflict");

                var newId = $"rule_{Guid.NewGuid():N}";
                var created = await rules.CreateAsync(new RoutingRule
                {
                    Id = newId,
                    ProjectId = projectId,
                    Name = request.Name ?? string.Empty,
                    Match = request.Match ?? string.Empty,
                    AgentId = agent.Id,
                    ResponsePrompt = request.ResponsePrompt ?? string.Empty,
                    Continue = request.Continue,
                }, ct: ct, idempotencyKey: idempotencyKey);

                var replay = created.Id != newId;
                if (replay && !SameRequestValues(created, agent.Id, request))
                    return ApiResults.Conflict(
                        "The Idempotency-Key was already used for a different subscription.",
                        "idempotency_key_conflict");
                return replay
                    ? ApiResults.Ok(ToDto(created))
                    : Results.Json(new ApiResponse<AgentSubscriptionDto>(true, ToDto(created)), statusCode: StatusCodes.Status201Created);
            }
            catch (Exception ex)
            {
                return MapError(ex);
            }
        });

        group.MapPatch("/{subscriptionId}", async (
            HttpContext context,
            string agentRef,
            string subscriptionId,
            AgentSubscriptionUpdateRequest request,
            AgentQuerier agents,
            RoutingRuleStore rules,
            CancellationToken ct) =>
        {
            if (request is null) return ApiResults.BadRequest("request body required");
            if (request.Fields.Count == 0) return ApiResults.BadRequest("At least one editable field is required.");
            var projectId = context.GetResolvedProject().Id;
            var agent = await AgentRefResolver.ResolveAsync(agents, projectId, agentRef);
            if (agent is null)
                return ApiResults.NotFound($"Agent '{agentRef}' not found");
            var existing = await rules.GetAsync(projectId, subscriptionId, ct);
            if (existing is null || existing.AgentId != agent.Id)
                return ApiResults.NotFound($"Subscription '{subscriptionId}' not found");

            try
            {
                var updated = await rules.UpdateAsync(
                    projectId,
                    subscriptionId,
                    request.Name,
                    request.Match,
                    agent.Id,
                    request.ResponsePrompt,
                    request.Continue,
                    request.Fields,
                    ct);
                return updated is null
                    ? ApiResults.NotFound($"Subscription '{subscriptionId}' not found")
                    : ApiResults.Ok(ToDto(updated));
            }
            catch (Exception ex)
            {
                return MapError(ex);
            }
        });

        group.MapDelete("/{subscriptionId}", async (
            HttpContext context,
            string agentRef,
            string subscriptionId,
            AgentQuerier agents,
            RoutingRuleStore rules,
            CancellationToken ct) =>
        {
            var projectId = context.GetResolvedProject().Id;
            var agent = await AgentRefResolver.ResolveAsync(agents, projectId, agentRef);
            if (agent is null)
                return ApiResults.NotFound($"Agent '{agentRef}' not found");
            var existing = await rules.GetAsync(projectId, subscriptionId, ct);
            if (existing is not null && existing.AgentId != agent.Id)
                return ApiResults.NotFound($"Subscription '{subscriptionId}' not found");

            // A repeated DELETE has the same observable result as the first one.
            await rules.DeleteAsync(projectId, subscriptionId, ct);
            return ApiResults.Ok(new { id = subscriptionId, status = "deleted" });
        });

        return app;
    }

    private static bool SameRequestValues(RoutingRule existing, string agentId, AgentSubscriptionCreateRequest request) =>
        existing.AgentId == agentId
        && string.Equals(existing.Name, request.Name?.Trim(), StringComparison.Ordinal)
        && string.Equals(existing.Match, request.Match, StringComparison.Ordinal)
        && string.Equals(existing.ResponsePrompt, request.ResponsePrompt, StringComparison.Ordinal)
        && existing.Continue == request.Continue;

    private static string DeriveState(AgentInfo agent, IReadOnlyList<AgentSubscriptionDto> subscriptions, IReadOnlyList<AgentConnection> connections)
    {
        if (agent.Readiness?.Conclusion == AgentReadinessConclusions.NeedsSetup)
            return "unconfigured";
        if (connections.Count == 0)
            return "no_connection";
        if (DeriveConnectionState(connections) != "connected")
            return "unavailable";
        return subscriptions.Count == 0 ? "empty" : "configured";
    }

    private static string DeriveConnectionState(IReadOnlyList<AgentConnection> connections) =>
        connections.Count == 0
            ? "no_connection"
            : connections.Any(connection => connection.SetupProgress == SetupProgressKind.Complete
                && connection.DesiredState == DesiredStateKind.Enabled
                && connection.ConnectionHealth == ConnectionHealthKind.Healthy)
                ? "connected"
                : "unavailable";

    private static IResult MapError(Exception exception) => exception switch
    {
        RoutingRuleMatchException match => ApiResults.BadRequest(match.Message, "invalid_match_expression", new
        {
            offset = match.Diagnostic.Offset, line = match.Diagnostic.Line, column = match.Diagnostic.Column,
        }),
        RoutingRuleValidationException validation => ApiResults.Conflict(validation.Message, validation.Code),
        RoutingRuleNameConflictException conflict => ApiResults.Conflict(conflict.Message, "routing_rule_name_conflict", new { conflict.ProjectId, conflict.Name }),
        _ => throw exception,
    };

    private static AgentSubscriptionDto ToDto(RoutingRule rule) => new(
        rule.Id, rule.ProjectId, rule.AgentId, rule.Name, rule.Match, rule.ResponsePrompt,
        rule.Continue, rule.Position, rule.Status, rule.CreatedAt, rule.UpdatedAt);
}

public sealed record AgentSubscriptionDto(
    string Id,
    string ProjectId,
    string AgentId,
    string Name,
    string Match,
    string ResponsePrompt,
    bool Continue,
    int Position,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record AgentSubscriptionListDto(
    IReadOnlyList<AgentSubscriptionDto> Subscriptions,
    string State,
    string AgentStatus,
    string Readiness,
    string Connection);

public sealed record AgentSubscriptionCreateRequest(
    string? Name,
    string? Match,
    string? ResponsePrompt,
    bool Continue = false);

public sealed record AgentSubscriptionUpdateRequest(
    string? Name,
    string? Match,
    string? ResponsePrompt,
    bool? Continue,
    IReadOnlySet<string> Fields,
    JsonElement Raw)
{
    public static async ValueTask<AgentSubscriptionUpdateRequest?> BindAsync(HttpContext context)
    {
        var raw = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body, JSON.Options);
        var fields = new HashSet<string>(StringComparer.Ordinal);
        if (raw.ValueKind == JsonValueKind.Object)
        {
            if (raw.TryGetProperty("name", out _)) fields.Add("name");
            if (raw.TryGetProperty("match", out _)) fields.Add("match");
            if (raw.TryGetProperty("responsePrompt", out _)) fields.Add("responsePrompt");
            if (raw.TryGetProperty("continue", out _)) fields.Add("continue");
        }
        return new AgentSubscriptionUpdateRequest(
            GetString(raw, "name"), GetString(raw, "match"), GetString(raw, "responsePrompt"),
            GetBool(raw, "continue"), fields, raw);
    }

    private static string? GetString(JsonElement raw, string name) =>
        raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString()
            : null;

    private static bool? GetBool(JsonElement raw, string name) =>
        raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;
}
