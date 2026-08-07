using System.Reflection;
using System.Text.Json;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure;

namespace Mohist.Server.Api;

public static class AgentSessionSpawnRoutes
{
    private static readonly IReadOnlySet<string> AllowedFields =
        new HashSet<string>(StringComparer.Ordinal) { "targetAgentRef", "prompt", "workspace" };

    public static WebApplication MapAgentSessionSpawnRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectRef}/agent-sessions")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        group.MapPost("/{parentSessionId}/spawns", async (
            HttpContext context,
            string parentSessionId,
            AgentSessionSpawnBody body,
            IAgentLauncher launcher,
            CancellationToken ct) =>
        {
            if (body is null || body.ParseError)
                return ApiResults.BadRequest("body must be a JSON object", "invalid_body");
            if (body.UndeclaredFields.Count > 0)
                return ApiResults.BadRequest(
                    $"unsupported top-level field(s): {string.Join(", ", body.UndeclaredFields)}",
                    "unsupported_field",
                    new { fields = body.UndeclaredFields.ToArray() });
            if (string.IsNullOrWhiteSpace(body.TargetAgentRef))
                return ApiResults.BadRequest("targetAgentRef is required", "target_agent_ref_required");
            if (string.IsNullOrWhiteSpace(body.Prompt))
                return ApiResults.BadRequest("prompt is required", "prompt_required");
            if (!context.Request.Headers.TryGetValue("Idempotency-Key", out var values)
                || string.IsNullOrWhiteSpace(values.FirstOrDefault()))
                return ApiResults.BadRequest("Idempotency-Key is required", "idempotency_key_required");

            var project = context.GetResolvedProject();
            try
            {
                var result = await launcher.LaunchSubagentAsync(
                    project.Id,
                    parentSessionId,
                    body.TargetAgentRef.Trim(),
                    body.Prompt,
                    values.First()!.Trim(),
                    body.Workspace,
                    ct);
                return Results.Json(new ApiResponse<AgentSessionSpawnResponse>(
                    true,
                    new AgentSessionSpawnResponse(
                        result.JobKey,
                        result.SessionId,
                        result.InputId,
                        result.TurnId,
                        result.AgentId,
                        result.AgentName,
                        "queued",
                        null,
                        null,
                        $"/api/projects/{Uri.EscapeDataString(project.Id)}/agent-sessions/{Uri.EscapeDataString(result.SessionId)}/transcript",
                        $"/api/projects/{Uri.EscapeDataString(project.Id)}/agent-jobs/{Uri.EscapeDataString(result.JobKey)}",
                        $"/api/projects/{Uri.EscapeDataString(project.Id)}/agent-jobs/{Uri.EscapeDataString(result.JobKey)}/launch-observation",
                        parentSessionId,
                        result.ParentLinkEdgeId)),
                    statusCode: StatusCodes.Status201Created);
            }
            catch (LaunchIdempotencyConflictException ex)
            {
                return ApiResults.Conflict(ex.Message, "spawn_idempotency_conflict");
            }
            catch (AgentSpawnPreplanRejectedException ex)
            {
                return ApiResults.Conflict(ex.Message, "spawn_rejected", new { reason = ex.Reason });
            }
            catch (AgentSpawnPostPlanRejectedException ex)
            {
                return ApiResults.Conflict(ex.Message, "spawn_rejected", new { reason = ex.Reason });
            }
            catch (AgentSpawnValidationPendingException ex)
            {
                return Results.Json(
                    new ApiResponse<object>(false, Error: ex.Message, Code: "validation_pending", Details: new { reason = ex.Reason }),
                    statusCode: StatusCodes.Status202Accepted);
            }
            catch (LaunchSetupPendingException ex)
            {
                return Results.Json(
                    new ApiResponse<object>(false, Error: ex.Message, Code: "validation_pending"),
                    statusCode: StatusCodes.Status202Accepted);
            }
        });

        return app;
    }

    public sealed record AgentSessionSpawnResponse(
        string JobId,
        string SessionId,
        string InputId,
        string TurnId,
        string AgentId,
        string AgentName,
        string Status,
        IReadOnlyList<AgentSessionLaunchAttachment>? Attachments,
        IReadOnlyList<AgentSessionLaunchAttachmentRejection>? RejectedAttachments,
        string TranscriptUrl,
        string JobUrl,
        string ObservationUrl,
        string ParentSessionId,
        string? EdgeId);

    public sealed record AgentSessionSpawnBody(
        string? TargetAgentRef,
        string? Prompt,
        string? Workspace,
        IReadOnlyList<string> UndeclaredFields,
        bool ParseError)
    {
        public static async ValueTask<AgentSessionSpawnBody?> BindAsync(HttpContext context, ParameterInfo _)
        {
            try
            {
                var raw = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body, JSON.Options);
                if (raw.ValueKind != JsonValueKind.Object)
                    return new(null, null, null, [], true);
                var undeclared = raw.EnumerateObject()
                    .Where(property => !AllowedFields.Contains(property.Name))
                    .Select(property => property.Name)
                    .ToArray();
                var target = raw.TryGetProperty("targetAgentRef", out var targetElement)
                    && targetElement.ValueKind == JsonValueKind.String
                    ? targetElement.GetString()
                    : null;
                var prompt = raw.TryGetProperty("prompt", out var promptElement)
                    && promptElement.ValueKind == JsonValueKind.String
                    ? promptElement.GetString()
                    : null;
                string? workspace = null;
                if (raw.TryGetProperty("workspace", out var workspaceElement))
                {
                    if (workspaceElement.ValueKind == JsonValueKind.String)
                        workspace = workspaceElement.GetString();
                    else if (workspaceElement.ValueKind != JsonValueKind.Null)
                        return new(null, null, null, [], true);
                }
                return new(target, prompt, workspace, undeclared, false);
            }
            catch (JsonException)
            {
                return new(null, null, null, [], true);
            }
        }
    }
}
