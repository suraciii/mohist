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

            var maxConcurrentRunsError = ValidateMaxConcurrentRuns(req.Raw);
            if (maxConcurrentRunsError is not null)
                return ApiResults.BadRequest(maxConcurrentRunsError, "invalid_max_concurrent_runs");

            var agentConfigError = AgentConfigSchema.Validate(req.AgentConfig);
            if (agentConfigError is not null)
                return ApiResults.BadRequest(agentConfigError, "invalid_agent_config");
            var permissionsError = AgentPermissionVocabulary.Validate(req.Raw);
            if (permissionsError is not null)
                return ApiResults.BadRequest(permissionsError, "invalid_agent_permissions");

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
                    req.AllowedSubagentAgentIds,
                    req.Avatar,
                    Purpose: req.Purpose,
                    Permissions: req.Permissions));
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
            // Availability must reflect each Agent's resolved Runtime/model, so
            // the list is hydrated rather than read as raw definitions.
            var agents = await query.ListAsync(projectId, ct: ct);

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

        // Built-in Workflow Agent names contain a path separator
        // (`mohist/builder`), so their detail read needs a catch-all name
        // route. It applies the same case-insensitive name resolution as
        // launch: a stored Project Agent shadows the built-in under this name.
        group.MapGet("/by-name/{**name}", async (HttpContext context, string name, AgentQuerier query) =>
        {
            var projectId = context.GetResolvedProject().Id;
            var agent = await query.GetEffectiveByNameAsync(projectId, name);
            return agent is null ? ApiResults.NotFound($"Agent '{name}' not found") : ApiResults.Ok(agent);
        });

        // Materialize a built-in Workflow Agent as a same-name Project Agent
        // carrying the caller's changes. The creation path and its invariants
        // are the ones POST /agents already owns; this route only composes
        // the definition before delegating to it.
        group.MapPost("/overrides", async (
            HttpContext context,
            AgentOverrideRequest? req,
            IGrainFactory grains,
            AgentQuerier query) =>
        {
            if (req is null) return ApiResults.BadRequest("request body is required", "body_required");
            if (req.UndeclaredFields.Count > 0)
            {
                return ApiResults.BadRequest(
                    $"unsupported top-level field(s): {string.Join(", ", req.UndeclaredFields)}; the override accepts only name, description, agentConfig, and skills.",
                    "unsupported_field",
                    new { fields = req.UndeclaredFields.ToArray() });
            }

            var name = req.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                return ApiResults.BadRequest("name is required", "validation_failed", new { fields = new[] { "name" } });

            var definition = BuiltInAgentCatalog.FindWorkflow(name);
            if (definition is null)
                return ApiResults.NotFound($"Built-in Agent '{name}' not found");

            var agentConfigError = AgentConfigSchema.Validate(req.AgentConfig);
            if (agentConfigError is not null)
                return ApiResults.BadRequest(agentConfigError, "invalid_agent_config");

            var projectId = context.GetResolvedProject().Id;
            var existing = await query.GetByNameAsync(projectId, definition.Name);
            if (existing is not null)
            {
                return string.Equals(existing.Status, AgentStatus.Archived, StringComparison.Ordinal)
                    ? ApiResults.Conflict(
                        $"An archived Project Agent named '{definition.Name}' shadows this built-in Agent; restore or rename it instead of creating an override.",
                        "agent_override_archived",
                        new { name = definition.Name, agentId = existing.Id })
                    : ApiResults.Conflict(
                        $"An active Project Agent named '{definition.Name}' already overrides this built-in Agent; edit that Agent instead.",
                        "agent_override_conflict",
                        new { name = definition.Name, agentId = existing.Id });
            }

            var agentId = $"agent_{Guid.NewGuid():N}";
            var grain = grains.GetGrain<IAgentGrain>(GrainKey.Agent(projectId, agentId));
            try
            {
                var created = await grain.CreateAsync(new AgentCreateData(
                    projectId,
                    definition.Name,
                    req.Description ?? definition.Description,
                    definition.Instructions,
                    OverlayBuiltInConfig(definition, req.AgentConfig),
                    req.Skills ?? definition.Skills,
                    MaxConcurrentRuns: null));
                return Results.Json(new ApiResponse<AgentInfo>(true, created), statusCode: 201);
            }
            catch (Exception ex) when (IsNameConflict(ex))
            {
                return ApiResults.Conflict(
                    $"An active Project Agent named '{definition.Name}' already overrides this built-in Agent; edit that Agent instead.",
                    "agent_override_conflict",
                    new { name = definition.Name });
            }
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
            var permissionsError = AgentPermissionVocabulary.Validate(req.Raw);
            if (permissionsError is not null)
                return ApiResults.BadRequest(permissionsError, "invalid_agent_permissions");

            var maxConcurrentRunsError = ValidateMaxConcurrentRuns(req.Raw);
            if (maxConcurrentRunsError is not null)
                return ApiResults.BadRequest(maxConcurrentRunsError, "invalid_max_concurrent_runs");

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
                    req.AllowedSubagentAgentIds,
                    req.Avatar,
                    Purpose: req.Purpose,
                    Permissions: req.Permissions));
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

    private static JsonElement? OverlayBuiltInConfig(
        BuiltInAgentDefinition definition,
        JsonElement? changes)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["runtime"] = JsonSerializer.SerializeToElement(definition.Runtime),
        };
        if (!string.IsNullOrWhiteSpace(definition.Model))
            values["model"] = JsonSerializer.SerializeToElement(definition.Model);
        if (!string.IsNullOrWhiteSpace(definition.Variant))
            values["variant"] = JsonSerializer.SerializeToElement(definition.Variant);

        if (changes is { ValueKind: JsonValueKind.Object } overlay)
        {
            foreach (var property in overlay.EnumerateObject())
                values[property.Name] = property.Value.Clone();
        }

        return JsonSerializer.SerializeToElement(values);
    }

    private static AgentAvailabilitySummaryEntry ToSummary(AgentAvailabilityListEntry entry) => new(
        entry.AgentId,
        entry.CanStartNow,
        entry.WaitingReason,
        entry.ActiveRuns,
        entry.MaxConcurrentRuns,
        new AgentAvailabilitySummaryCapacity(entry.Capacity.UsedSlots, entry.Capacity.TotalSlots, entry.CapacityIncomplete),
        entry.QueuedCount);

    private static bool TouchesImmutableField(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object) return false;
        return raw.TryGetProperty("id", out _)
            || raw.TryGetProperty("projectId", out _)
            || raw.TryGetProperty("createdAt", out _);
    }

    internal static string? ValidateMaxConcurrentRuns(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object || !raw.TryGetProperty("maxConcurrentRuns", out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var maxConcurrentRuns) || maxConcurrentRuns <= 0)
            return "maxConcurrentRuns must be a positive integer or null.";
        return null;
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
    string? Purpose = null,
    JsonElement? AgentConfig = null,
    IReadOnlyList<string>? Skills = null,
    IReadOnlyList<string>? Permissions = null,
    int? MaxConcurrentRuns = null,
    IReadOnlyList<string>? AllowedSubagentAgentIds = null,
    string? Avatar = null,
    JsonElement Raw = default)
{
    public static async ValueTask<AgentCreateRequest?> BindAsync(HttpContext context)
    {
        var raw = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body, JSON.Options);
        return new AgentCreateRequest(
            AgentDefinitionRequestBinding.GetString(raw, "name") ?? string.Empty,
            AgentDefinitionRequestBinding.GetString(raw, "instructions") ?? string.Empty,
            AgentDefinitionRequestBinding.GetString(raw, "description"),
            AgentDefinitionRequestBinding.GetString(raw, "purpose"),
            AgentDefinitionRequestBinding.GetElement(raw, "agentConfig"),
            AgentDefinitionRequestBinding.GetStringList(raw, "skills"),
            AgentDefinitionRequestBinding.GetPermissionList(raw),
            AgentDefinitionRequestBinding.GetInt(raw, "maxConcurrentRuns"),
            AgentDefinitionRequestBinding.GetStringList(raw, "allowedSubagentAgentIds"),
            AgentDefinitionRequestBinding.GetString(raw, "avatar"),
            raw);
    }
}

public sealed record AgentOverrideRequest(
    string? Name,
    string? Description,
    JsonElement? AgentConfig,
    IReadOnlyList<string>? Skills,
    IReadOnlyList<string> UndeclaredFields)
{
    public static async ValueTask<AgentOverrideRequest?> BindAsync(HttpContext context)
    {
        try
        {
            var raw = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body, JSON.Options);
            if (raw.ValueKind != JsonValueKind.Object)
                throw new JsonException("the override request must be a JSON object");

            var undeclared = raw.EnumerateObject()
                .Where(property => !AllowedFields.Contains(property.Name))
                .Select(property => property.Name)
                .ToArray();
            return new AgentOverrideRequest(
                AgentDefinitionRequestBinding.GetString(raw, "name"),
                AgentDefinitionRequestBinding.GetString(raw, "description"),
                AgentDefinitionRequestBinding.GetElement(raw, "agentConfig"),
                AgentDefinitionRequestBinding.GetStringList(raw, "skills"),
                undeclared);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly IReadOnlySet<string> AllowedFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "name",
        "description",
        "agentConfig",
        "skills",
    };
}

public sealed record AgentUpdateRequest(
    string? Name,
    string? Description,
    string? Purpose,
    string? Instructions,
    JsonElement? AgentConfig,
    IReadOnlyList<string>? Skills,
    IReadOnlyList<string>? Permissions,
    int? MaxConcurrentRuns,
    IReadOnlyList<string>? AllowedSubagentAgentIds,
    IReadOnlySet<string> Fields,
    JsonElement Raw,
    string? Avatar = null)
{
    public static async ValueTask<AgentUpdateRequest?> BindAsync(HttpContext context)
    {
        var raw = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body, JSON.Options);
        return new AgentUpdateRequest(
            AgentDefinitionRequestBinding.GetString(raw, "name"),
            AgentDefinitionRequestBinding.GetString(raw, "description"),
            AgentDefinitionRequestBinding.GetString(raw, "purpose"),
            AgentDefinitionRequestBinding.GetString(raw, "instructions"),
            AgentDefinitionRequestBinding.GetElement(raw, "agentConfig"),
            AgentDefinitionRequestBinding.GetStringList(raw, "skills"),
            AgentDefinitionRequestBinding.GetPermissionList(raw),
            AgentDefinitionRequestBinding.GetInt(raw, "maxConcurrentRuns"),
            AgentDefinitionRequestBinding.GetStringList(raw, "allowedSubagentAgentIds"),
            GetFields(raw),
            raw,
            AgentDefinitionRequestBinding.GetString(raw, "avatar"));
    }

    private static IReadOnlySet<string> GetFields(JsonElement raw)
    {
        var fields = new HashSet<string>(StringComparer.Ordinal);
        if (raw.ValueKind != JsonValueKind.Object) return fields;
        if (raw.TryGetProperty("name", out _)) fields.Add(nameof(Name));
        if (raw.TryGetProperty("avatar", out _)) fields.Add(nameof(Avatar));
        if (raw.TryGetProperty("description", out _)) fields.Add(nameof(Description));
        if (raw.TryGetProperty("purpose", out _)) fields.Add(nameof(Purpose));
        if (raw.TryGetProperty("instructions", out _)) fields.Add(nameof(Instructions));
        if (raw.TryGetProperty("agentConfig", out _)) fields.Add(nameof(AgentConfig));
        if (raw.TryGetProperty("skills", out _)) fields.Add(nameof(Skills));
        if (raw.TryGetProperty("permissions", out _)) fields.Add(nameof(Permissions));
        if (raw.TryGetProperty("maxConcurrentRuns", out _)) fields.Add(nameof(MaxConcurrentRuns));
        if (raw.TryGetProperty("allowedSubagentAgentIds", out _)) fields.Add(nameof(AllowedSubagentAgentIds));
        return fields;
    }

}

internal static class AgentDefinitionRequestBinding
{
    public static string? GetString(JsonElement raw, string property) =>
        raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString()
            : null;

    public static JsonElement? GetElement(JsonElement raw, string property) =>
        raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.Clone()
            : null;

    public static IReadOnlyList<string>? GetStringList(JsonElement raw, string property) =>
        raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray()
            : null;

    // Permission validation happens in the route handler, so binding must preserve
    // malformed elements long enough for that boundary to return its API error.
    public static IReadOnlyList<string>? GetPermissionList(JsonElement raw) =>
        raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("permissions", out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : string.Empty).ToArray()
            : null;

    public static int? GetInt(JsonElement raw, string property) =>
        raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result
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

public sealed record AgentAvailabilitySummaryCapacity(int UsedSlots, int TotalSlots, bool Incomplete = false);
