using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Services;
using Mohist.Server.Runner.Services;

namespace Mohist.Server.Api;

public static class AgentDefinitionRoutes
{
    public static WebApplication MapAgentDefinitionRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectRef}/agents")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        group.MapPost("/", async (HttpContext context, AgentCreateRequest req, IGrainFactory grains) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name)) return ApiResults.BadRequest("name is required");
            if (string.IsNullOrWhiteSpace(req.Instructions)) return ApiResults.BadRequest("instructions is required");

            var agentConfigError = AgentConfigSchema.Validate(req.AgentConfig);
            if (agentConfigError is not null)
                return ApiResults.BadRequest(agentConfigError, "invalid_agent_config");

            var projectId = context.GetResolvedProject().Id;
            var agentId = $"agent_{Guid.NewGuid():N}";
            var grain = grains.GetGrain<IAgentGrain>(GrainKey.Agent(projectId, agentId));

            try
            {
                var created = await grain.CreateAsync(new AgentCreateData(
                    projectId,
                    req.Name,
                    req.Description,
                    req.Instructions,
                    req.AgentConfig?.Clone(),
                    req.Skills,
                    req.MaxConcurrentRuns,
                    req.AllowedSubagentAgentIds));
                return Results.Json(new ApiResponse<AgentInfo>(true, created), statusCode: 201);
            }
            catch (Exception ex) when (IsNameConflict(ex))
            {
                return ApiResults.Conflict($"Agent name '{req.Name}' is already used", "AGENT_NAME_CONFLICT", new { name = req.Name });
            }
        });

        group.MapGet("/", async (HttpContext context, bool? all, string? status, AgentQuerier query) =>
        {
            var projectId = context.GetResolvedProject().Id;
            return ApiResults.Ok(await query.ListAsync(projectId, status, all == true));
        });

        group.MapGet("/availability", async (
            HttpContext context,
            AgentQuerier query,
            AgentAvailabilityService availability,
            CancellationToken ct) =>
        {
            var projectId = context.GetResolvedProject().Id;
            var agents = await query.ListActiveDefinitionsAsync(projectId, ct);

            var entries = await availability.GetListSummaryAsync(projectId, agents, ct);
            var summaries = agents
                .Select(agent => entries[agent.Id])
                .Select(ToSummary)
                .ToList();

            return ApiResults.Ok(summaries);
        });

        group.MapGet("/{id}", async (HttpContext context, string id, AgentQuerier query) =>
        {
            var projectId = context.GetResolvedProject().Id;
            var agent = await query.GetByIdAsync(projectId, id);
            return agent is null ? ApiResults.NotFound($"Agent {id} not found") : ApiResults.Ok(agent);
        });

        group.MapPatch("/{id}", async (HttpContext context, string id, AgentUpdateRequest req, IGrainFactory grains, AgentQuerier query) =>
        {
            if (TouchesImmutableField(req.Raw))
                return ApiResults.BadRequest("id, projectId, and createdAt are immutable", "IMMUTABLE_AGENT_FIELD");

            if (req.Fields.Contains(nameof(AgentUpdateRequest.AgentConfig)))
            {
            var agentConfigError = AgentConfigSchema.Validate(req.AgentConfig);
                if (agentConfigError is not null)
                    return ApiResults.BadRequest(agentConfigError, "invalid_agent_config");
            }

            var projectId = context.GetResolvedProject().Id;
            var existing = await query.GetByIdAsync(projectId, id);
            if (existing is null) return ApiResults.NotFound($"Agent {id} not found");

            var grain = grains.GetGrain<IAgentGrain>(GrainKey.Agent(projectId, id));
            try
            {
                var updated = await grain.UpdateAsync(new AgentUpdateData(
                    req.Name,
                    req.Description,
                    req.Instructions,
                    req.AgentConfig?.Clone(),
                    req.Skills,
                    req.MaxConcurrentRuns,
                    req.Fields,
                    req.AllowedSubagentAgentIds));
                return updated is null ? ApiResults.NotFound($"Agent {id} not found") : ApiResults.Ok(updated);
            }
            catch (Exception ex) when (IsNameConflict(ex))
            {
                return ApiResults.Conflict($"Agent name '{req.Name}' is already used", "AGENT_NAME_CONFLICT", new { name = req.Name });
            }
        });

        group.MapDelete("/{id}", async (HttpContext context, string id, IGrainFactory grains, AgentQuerier query) =>
        {
            var projectId = context.GetResolvedProject().Id;
            var existing = await query.GetByIdAsync(projectId, id);
            if (existing is null) return ApiResults.NotFound($"Agent {id} not found");

            var grain = grains.GetGrain<IAgentGrain>(GrainKey.Agent(projectId, id));
            var archived = await grain.ArchiveAsync();
            return archived is null ? ApiResults.NotFound($"Agent {id} not found") : ApiResults.Ok(archived);
        });

        group.MapPost("/{id}/unarchive", async (HttpContext context, string id, IGrainFactory grains, AgentQuerier query) =>
        {
            var projectId = context.GetResolvedProject().Id;
            var existing = await query.GetByIdAsync(projectId, id);
            if (existing is null) return ApiResults.NotFound($"Agent {id} not found");

            var grain = grains.GetGrain<IAgentGrain>(GrainKey.Agent(projectId, id));
            var unarchived = await grain.UnarchiveAsync();
            return unarchived is null ? ApiResults.NotFound($"Agent {id} not found") : ApiResults.Ok(unarchived);
        });

        return app;
    }

    private static AgentAvailabilitySummaryEntry ToSummary(AgentAvailabilityListEntry entry) => new(
        entry.AgentId,
        entry.CanStartNow,
        entry.WaitingReason,
        entry.ActiveRuns,
        entry.MaxConcurrentRuns,
        new AgentAvailabilitySummaryCapacity(entry.Capacity.UsedSlots, entry.Capacity.TotalSlots),
        entry.QueuedCount);

    private static bool TouchesImmutableField(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object) return false;
        return raw.TryGetProperty("id", out _)
            || raw.TryGetProperty("projectId", out _)
            || raw.TryGetProperty("createdAt", out _);
    }

    private static bool IsNameConflict(Exception ex) =>
        ex is AgentNameConflictException
        || ex is DbUpdateException { InnerException: SqliteException sqlite }
            && sqlite.SqliteErrorCode == 19
            && sqlite.Message.Contains("Agents", StringComparison.OrdinalIgnoreCase);
}

public sealed record AgentCreateRequest(
    string Name,
    string Instructions,
    string? Description = null,
    JsonElement? AgentConfig = null,
    IReadOnlyList<string>? Skills = null,
    int? MaxConcurrentRuns = null,
    IReadOnlyList<string>? AllowedSubagentAgentIds = null);

public sealed record AgentUpdateRequest(
    string? Name,
    string? Description,
    string? Instructions,
    JsonElement? AgentConfig,
    IReadOnlyList<string>? Skills,
    int? MaxConcurrentRuns,
    IReadOnlyList<string>? AllowedSubagentAgentIds,
    IReadOnlySet<string> Fields,
    JsonElement Raw)
{
    public static async ValueTask<AgentUpdateRequest?> BindAsync(HttpContext context)
    {
        var raw = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body, JSON.Options);
        return new AgentUpdateRequest(
            GetString(raw, "name"),
            GetString(raw, "description"),
            GetString(raw, "instructions"),
            GetElement(raw, "agentConfig"),
            GetStringList(raw, "skills"),
            GetInt(raw, "maxConcurrentRuns"),
            GetStringList(raw, "allowedSubagentAgentIds"),
            GetFields(raw),
            raw);
    }

    private static IReadOnlySet<string> GetFields(JsonElement raw)
    {
        var fields = new HashSet<string>(StringComparer.Ordinal);
        if (raw.ValueKind != JsonValueKind.Object) return fields;
        if (raw.TryGetProperty("name", out _)) fields.Add(nameof(Name));
        if (raw.TryGetProperty("description", out _)) fields.Add(nameof(Description));
        if (raw.TryGetProperty("instructions", out _)) fields.Add(nameof(Instructions));
        if (raw.TryGetProperty("agentConfig", out _)) fields.Add(nameof(AgentConfig));
        if (raw.TryGetProperty("skills", out _)) fields.Add(nameof(Skills));
        if (raw.TryGetProperty("maxConcurrentRuns", out _)) fields.Add(nameof(MaxConcurrentRuns));
        if (raw.TryGetProperty("allowedSubagentAgentIds", out _)) fields.Add(nameof(AllowedSubagentAgentIds));
        return fields;
    }

    private static string? GetString(JsonElement raw, string property) =>
        raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString()
            : null;

    private static JsonElement? GetElement(JsonElement raw, string property) =>
        raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.Clone()
            : null;

    private static IReadOnlyList<string>? GetStringList(JsonElement raw, string property) =>
        raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray()
            : null;

    private static int? GetInt(JsonElement raw, string property) =>
        raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;
}

public sealed record AgentAvailabilitySummaryEntry(
    string AgentId,
    bool CanStartNow,
    string? WaitingReason,
    int ActiveRuns,
    int? MaxConcurrentRuns,
    AgentAvailabilitySummaryCapacity Capacity,
    int QueuedCount);

public sealed record AgentAvailabilitySummaryCapacity(int UsedSlots, int TotalSlots);
