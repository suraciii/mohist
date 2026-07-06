using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure;

namespace Mohist.Server.Api;

/// <summary>
/// Subscription CRUD endpoints for an Agent (issue-391 T-002). A
/// subscription is a first-class object owned by exactly one Agent within
/// a project; the Agent profile and any sibling subscriptions are not
/// mutated by per-subscription operations. Lifecycle transitions
/// (archive/restore) toggle status only — they do not delete the row.
/// </summary>
/// <remarks>
/// <para>
/// The route group inherits the project resolver from
/// <see cref="ProjectResolutionEndpointFilter"/>, then resolves
/// <c>agentRef</c> via <see cref="AgentRefResolver"/> (id-else-name, same
/// rule as the launch endpoint). The store uses the EF-backed
/// <see cref="AgentSubscriptionStore"/> with multi-row single-table
/// persistence per design D1; no subscription payload is ever embedded in
/// the Agent definition.
/// </para>
/// <para>
/// Archived Agent names gate the create path with a 409 carrying the same
/// <c>agent_archived</c> code that the launch endpoint already returns —
/// matches design D8.
/// </para>
/// </remarks>
public static class AgentSubscriptionRoutes
{
    public static WebApplication MapAgentSubscriptionRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectRef}/agents/{agentRef}/subscriptions")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        group.MapGet("/", async (
            HttpContext context,
            string agentRef,
            AgentQuerier agentQuerier,
            AgentSubscriptionStore store,
            CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            var agent = await AgentRefResolver.ResolveAsync(agentQuerier, project.Id, agentRef);
            if (agent is null)
                return ApiResults.NotFound($"Agent '{agentRef}' not found");

            var rows = await store.ListByAgentAsync(project.Id, agent.Id, ct);
            return ApiResults.Ok(rows.Select(AgentSubscriptionQuerier.ToDto).ToArray());
        });

        group.MapPost("/", async (
            HttpContext context,
            string agentRef,
            AgentSubscriptionCreateRequest req,
            AgentQuerier agentQuerier,
            AgentSubscriptionStore store,
            CancellationToken ct) =>
        {
            if (req is null)
                return ApiResults.BadRequest("request body required");
            if (string.IsNullOrWhiteSpace(req.Name))
                return ApiResults.BadRequest("name is required", "missing_field", new { field = "name" });
            if (string.IsNullOrWhiteSpace(req.Filter?.Type))
                return ApiResults.BadRequest("filter.type is required", "missing_field", new { field = "filter.type" });
            if (string.IsNullOrWhiteSpace(req.ResponsePrompt))
                return ApiResults.BadRequest("responsePrompt is required", "missing_field", new { field = "responsePrompt" });

            var project = context.GetResolvedProject();
            var agent = await AgentRefResolver.ResolveAsync(agentQuerier, project.Id, agentRef);
            if (agent is null)
                return ApiResults.NotFound($"Agent '{agentRef}' not found");

            if (string.Equals(agent.Status, AgentStatus.Archived, StringComparison.Ordinal))
                return ApiResults.Conflict(
                    "Archived agents cannot receive new subscriptions",
                    "agent_archived");

            var subscription = new AgentSubscription
            {
                Id = NewSubscriptionId(),
                ProjectId = project.Id,
                AgentId = agent.Id,
                Name = req.Name.Trim(),
                Filter = new SubscriptionFilter
                {
                    Type = req.Filter.Type,
                    Source = string.IsNullOrWhiteSpace(req.Filter.Source) ? null : req.Filter.Source,
                    Subject = string.IsNullOrWhiteSpace(req.Filter.Subject) ? null : req.Filter.Subject,
                },
                ResponsePrompt = req.ResponsePrompt,
                Priority = req.Priority,
                Status = SubscriptionStatus.Active,
            };

            try
            {
                await store.CreateAsync(subscription, ct);
            }
            catch (AgentSubscriptionNameConflictException ex)
            {
                return ApiResults.Conflict(
                    ex.Message,
                    "subscription_name_conflict",
                    new { agentId = ex.AgentId, name = ex.Name });
            }

            return Results.Json(
                new ApiResponse<AgentSubscriptionDto>(true, AgentSubscriptionQuerier.ToDto(subscription)),
                statusCode: 201);
        });

        group.MapGet("/{subscriptionId}", async (
            HttpContext context,
            string agentRef,
            string subscriptionId,
            AgentQuerier agentQuerier,
            AgentSubscriptionStore store,
            CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            var agent = await AgentRefResolver.ResolveAsync(agentQuerier, project.Id, agentRef);
            if (agent is null)
                return ApiResults.NotFound($"Agent '{agentRef}' not found");

            var subscription = await store.GetAsync(subscriptionId, ct);
            if (subscription is null
                || subscription.ProjectId != project.Id
                || subscription.AgentId != agent.Id)
                return ApiResults.NotFound($"Subscription '{subscriptionId}' not found");

            return ApiResults.Ok(AgentSubscriptionQuerier.ToDto(subscription));
        });

        group.MapPatch("/{subscriptionId}", async (
            HttpContext context,
            string agentRef,
            string subscriptionId,
            AgentSubscriptionUpdateRequest req,
            AgentQuerier agentQuerier,
            AgentSubscriptionStore store,
            CancellationToken ct) =>
        {
            if (req is null)
                return ApiResults.BadRequest("request body required");

            var project = context.GetResolvedProject();
            var agent = await AgentRefResolver.ResolveAsync(agentQuerier, project.Id, agentRef);
            if (agent is null)
                return ApiResults.NotFound($"Agent '{agentRef}' not found");

            var existing = await store.GetAsync(subscriptionId, ct);
            if (existing is null
                || existing.ProjectId != project.Id
                || existing.AgentId != agent.Id)
                return ApiResults.NotFound($"Subscription '{subscriptionId}' not found");

            string? newName = req.Fields.Contains(nameof(req.Name)) ? req.Name : null;
            if (newName is not null && string.IsNullOrWhiteSpace(newName))
                return ApiResults.BadRequest("name cannot be blank", "invalid_field", new { field = "name" });

            string? newResponsePrompt = req.Fields.Contains(nameof(req.ResponsePrompt)) ? req.ResponsePrompt : null;
            if (newResponsePrompt is not null && string.IsNullOrWhiteSpace(newResponsePrompt))
                return ApiResults.BadRequest("responsePrompt cannot be blank", "invalid_field", new { field = "responsePrompt" });

            SubscriptionFilter? newFilter = null;
            if (req.Fields.Contains(nameof(req.Filter)))
            {
                if (req.Filter is null)
                    return ApiResults.BadRequest("filter cannot be null", "invalid_field", new { field = "filter" });
                if (string.IsNullOrWhiteSpace(req.Filter.Type))
                    return ApiResults.BadRequest("filter.type is required", "missing_field", new { field = "filter.type" });
                newFilter = new SubscriptionFilter
                {
                    Type = req.Filter.Type,
                    Source = string.IsNullOrWhiteSpace(req.Filter.Source) ? null : req.Filter.Source,
                    Subject = string.IsNullOrWhiteSpace(req.Filter.Subject) ? null : req.Filter.Subject,
                };
            }

            int? newPriority = req.Priority;
            bool priorityTouched = req.Fields.Contains(nameof(req.Priority));

            AgentSubscription? updated;
            try
            {
                updated = await store.UpdateAsync(
                    subscriptionId,
                    newName,
                    newFilter,
                    newResponsePrompt,
                    newPriority,
                    priorityTouched,
                    ct);
            }
            catch (AgentSubscriptionNameConflictException ex)
            {
                return ApiResults.Conflict(
                    ex.Message,
                    "subscription_name_conflict",
                    new { agentId = ex.AgentId, name = ex.Name });
            }

            return updated is null
                ? ApiResults.NotFound($"Subscription '{subscriptionId}' not found")
                : ApiResults.Ok(AgentSubscriptionQuerier.ToDto(updated));
        });

        group.MapPost("/{subscriptionId}/archive", async (
            HttpContext context,
            string agentRef,
            string subscriptionId,
            AgentQuerier agentQuerier,
            AgentSubscriptionStore store,
            CancellationToken ct) =>
        {
            return await TransitionStatusAsync(context, agentRef, subscriptionId, agentQuerier, store, store.ArchiveAsync, ct);
        });

        group.MapPost("/{subscriptionId}/restore", async (
            HttpContext context,
            string agentRef,
            string subscriptionId,
            AgentQuerier agentQuerier,
            AgentSubscriptionStore store,
            CancellationToken ct) =>
        {
            return await TransitionStatusAsync(context, agentRef, subscriptionId, agentQuerier, store, store.RestoreAsync, ct);
        });

        group.MapDelete("/{subscriptionId}", async (
            HttpContext context,
            string agentRef,
            string subscriptionId,
            AgentQuerier agentQuerier,
            AgentSubscriptionStore store,
            CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            var agent = await AgentRefResolver.ResolveAsync(agentQuerier, project.Id, agentRef);
            if (agent is null)
                return ApiResults.NotFound($"Agent '{agentRef}' not found");

            var existing = await store.GetAsync(subscriptionId, ct);
            if (existing is null
                || existing.ProjectId != project.Id
                || existing.AgentId != agent.Id)
                return ApiResults.NotFound($"Subscription '{subscriptionId}' not found");

            await store.DeleteAsync(subscriptionId, ct);
            return ApiResults.Ok();
        });

        return app;
    }

    private static async Task<IResult> TransitionStatusAsync(
        HttpContext context,
        string agentRef,
        string subscriptionId,
        AgentQuerier agentQuerier,
        AgentSubscriptionStore store,
        Func<string, CancellationToken, Task<AgentSubscription?>> transition,
        CancellationToken ct)
    {
        var project = context.GetResolvedProject();
        var agent = await AgentRefResolver.ResolveAsync(agentQuerier, project.Id, agentRef);
        if (agent is null)
            return ApiResults.NotFound($"Agent '{agentRef}' not found");

        var existing = await store.GetAsync(subscriptionId, ct);
        if (existing is null
            || existing.ProjectId != project.Id
            || existing.AgentId != agent.Id)
            return ApiResults.NotFound($"Subscription '{subscriptionId}' not found");

        var result = await transition(subscriptionId, ct);
        return result is null
            ? ApiResults.NotFound($"Subscription '{subscriptionId}' not found")
            : ApiResults.Ok(AgentSubscriptionQuerier.ToDto(result));
    }

    private static string NewSubscriptionId() =>
        $"subs_{Guid.NewGuid():N}";
}

/// <summary>
/// Body for <c>POST /api/projects/{projectRef}/agents/{agentRef}/subscriptions</c>.
/// All fields are required at creation time except <c>priority</c>, which is
/// nullable and defaults to <c>0</c> at dispatch time (per design D3).
/// </summary>
public sealed record AgentSubscriptionCreateRequest(
    string? Name,
    AgentSubscriptionFilterPayload? Filter,
    string? ResponsePrompt,
    int? Priority = null);

/// <summary>
/// Body for <c>PATCH /api/projects/{projectRef}/agents/{agentRef}/subscriptions/{subscriptionId}</c>.
/// Only fields listed in <see cref="Fields"/> are touched; omitted fields keep
/// their existing value. <c>priority</c> accepts explicit <c>null</c> to clear the
/// override (dispatch will then default to <c>0</c>). The presence of
/// <c>priority</c> in <see cref="Fields"/> signals the user wants to clear or
/// set it; absence keeps the current value untouched.
/// </summary>
public sealed record AgentSubscriptionUpdateRequest(
    string? Name,
    AgentSubscriptionFilterPayload? Filter,
    string? ResponsePrompt,
    int? Priority,
    IReadOnlySet<string> Fields,
    JsonElement Raw)
{
    public static async ValueTask<AgentSubscriptionUpdateRequest?> BindAsync(HttpContext context)
    {
        var raw = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body, JSON.Options);
        return new AgentSubscriptionUpdateRequest(
            GetString(raw, "name"),
            GetFilter(raw),
            GetString(raw, "responsePrompt"),
            GetInt(raw, "priority"),
            GetFields(raw),
            raw);
    }

    private static IReadOnlySet<string> GetFields(JsonElement raw)
    {
        var fields = new HashSet<string>(StringComparer.Ordinal);
        if (raw.ValueKind != JsonValueKind.Object) return fields;
        if (raw.TryGetProperty("name", out _)) fields.Add("Name");
        if (raw.TryGetProperty("filter", out _)) fields.Add("Filter");
        if (raw.TryGetProperty("responsePrompt", out _)) fields.Add("ResponsePrompt");
        if (raw.TryGetProperty("priority", out _)) fields.Add("Priority");
        return fields;
    }

    private static string? GetString(JsonElement raw, string property) =>
        raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement raw, string property)
    {
        if (raw.ValueKind != JsonValueKind.Object || !raw.TryGetProperty(property, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Null) return null;
        return value.TryGetInt32(out var intValue) ? intValue : null;
    }

    private static AgentSubscriptionFilterPayload? GetFilter(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object || !raw.TryGetProperty("filter", out var element))
            return null;
        if (element.ValueKind == JsonValueKind.Null) return null;
        return new AgentSubscriptionFilterPayload(
            GetString(element, "type"),
            GetString(element, "source"),
            GetString(element, "subject"));
    }
}

public sealed record AgentSubscriptionFilterPayload(
    string? Type,
    string? Source,
    string? Subject);
