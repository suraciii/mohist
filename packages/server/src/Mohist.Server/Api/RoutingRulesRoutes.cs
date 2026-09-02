using System.Text.Json;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;

namespace Mohist.Server.Api;

public static class RoutingRulesRoutes
{
    public static WebApplication MapRoutingRulesRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectRef}/routing/rules")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        group.MapGet("", async (HttpContext context, RoutingRuleStore store, CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            var rules = await store.ListAsync(project.Id, true, ct);
            return ApiResults.Ok(rules.Select(ToDto).ToArray());
        });

        group.MapPost("", async (HttpContext context, RoutingRuleCreateRequest request, RoutingRuleStore store, CancellationToken ct) =>
        {
            if (request is null) return ApiResults.BadRequest("request body required");
            var project = context.GetResolvedProject();
            var rule = new RoutingRule
            {
                Id = $"rule_{Guid.NewGuid():N}", ProjectId = project.Id, Name = request.Name ?? string.Empty,
                Match = request.Match ?? string.Empty, AgentId = request.AgentId ?? string.Empty,
                ResponsePrompt = request.ResponsePrompt ?? string.Empty, Continue = request.Continue,
            };
            try
            {
                var created = await store.CreateAsync(rule, request.Before, request.After, ct);
                return Results.Json(new ApiResponse<RoutingRuleDto>(true, ToDto(created)), statusCode: StatusCodes.Status201Created);
            }
            catch (Exception ex)
            {
                return MapError(ex);
            }
        });

        group.MapGet("/{ruleId}", async (HttpContext context, string ruleId, RoutingRuleStore store, CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            var rule = await store.GetAsync(project.Id, ruleId, ct);
            return rule is null || rule.Status == RoutingRuleStatus.Deleted
                ? ApiResults.NotFound($"Routing rule '{ruleId}' not found")
                : ApiResults.Ok(ToDto(rule));
        });

        group.MapPatch("/{ruleId}", async (HttpContext context, string ruleId, RoutingRuleUpdateRequest request, RoutingRuleStore store, CancellationToken ct) =>
        {
            if (request is null) return ApiResults.BadRequest("request body required");
            var project = context.GetResolvedProject();
            try
            {
                var updated = await store.UpdateAsync(project.Id, ruleId, request.Name, request.Match, request.AgentId, request.ResponsePrompt, request.Continue, request.Fields, ct);
                return updated is null ? ApiResults.NotFound($"Routing rule '{ruleId}' not found") : ApiResults.Ok(ToDto(updated));
            }
            catch (Exception ex)
            {
                return MapError(ex);
            }
        });

        group.MapPost("/{ruleId}/archive", async (HttpContext context, string ruleId, RoutingRuleStore store, CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            var archived = await store.ArchiveAsync(project.Id, ruleId, ct);
            return archived is null ? ApiResults.NotFound($"Routing rule '{ruleId}' not found") : ApiResults.Ok(ToDto(archived));
        });

        group.MapPost("/{ruleId}/move", async (HttpContext context, string ruleId, RoutingRuleMoveRequest request, RoutingRuleStore store, CancellationToken ct) =>
        {
            if (request is null) return ApiResults.BadRequest("request body required");
            var project = context.GetResolvedProject();
            try
            {
                var moved = await store.MoveAsync(project.Id, ruleId, request.Before, request.After, ct);
                return moved is null ? ApiResults.NotFound($"Routing rule '{ruleId}' not found") : ApiResults.Ok(ToDto(moved));
            }
            catch (Exception ex)
            {
                return MapError(ex);
            }
        });

        return app;
    }

    private static IResult MapError(Exception exception) => exception switch
    {
        RoutingRuleMatchException match => ApiResults.BadRequest(match.Message, "invalid_match_expression", new
        {
            offset = match.Diagnostic.Offset, line = match.Diagnostic.Line, column = match.Diagnostic.Column,
        }),
        RoutingRuleValidationException validation => ApiResults.Conflict(validation.Message, validation.Code),
        RoutingRuleNameConflictException conflict => ApiResults.Conflict(conflict.Message, "routing_rule_name_conflict", new { conflict.ProjectId, conflict.Name }),
        RoutingRuleMoveTargetNotFoundException missing => ApiResults.NotFound(missing.Message),
        RoutingRuleMoveTargetException invalid => ApiResults.BadRequest(invalid.Message, "invalid_move_target"),
        _ => throw exception,
    };

    private static RoutingRuleDto ToDto(RoutingRule rule) => new(
        rule.Id, rule.ProjectId, rule.Name, rule.Position, rule.Match, rule.AgentId,
        rule.ResponsePrompt, rule.Continue, rule.Status, rule.CreatedAt, rule.UpdatedAt);
}

public sealed record RoutingRuleDto(
    string Id, string ProjectId, string Name, int Position, string Match, string AgentId,
    string ResponsePrompt, bool Continue, string Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record RoutingRuleCreateRequest(
    string? Name, string? Match, string? AgentId, string? ResponsePrompt, bool Continue = false, string? Before = null, string? After = null);

public sealed record RoutingRuleMoveRequest(string? Before, string? After);

public sealed record RoutingRuleUpdateRequest(
    string? Name, string? Match, string? AgentId, string? ResponsePrompt, bool? Continue, IReadOnlySet<string> Fields, JsonElement Raw)
{
    public static ValueTask<RoutingRuleUpdateRequest?> BindAsync(HttpContext context) =>
        RoutingRuleUpdateRequestBinder.BindAsync(context);
}
