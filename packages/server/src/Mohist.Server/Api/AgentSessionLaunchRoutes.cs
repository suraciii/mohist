using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Epic.Services;
using Mohist.Server.Issue.Services;
using Mohist.Server.Infrastructure;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Api;

/// <summary>
/// Product launch endpoint for a generic AgentSession from a project-scoped
/// Agent profile (composed through the shared
/// <see cref="IAgentLauncher"/>). Distinct from the
/// validation-only <c>POST /api/agent-jobs/validate</c> route, which
/// remains a developer smoke-test surface and is not the product API.
/// </summary>
/// <remarks>
/// <para>
/// The route delegates the canonical mint-session → open-generic-session
/// → build-AgentJobInput → submit-to-grain pipeline to
/// <see cref="IAgentLauncher"/>. The route keeps its domain-level
/// gates (whitespace prompt → 400; unresolved agent → 404; archived agent
/// → 409) and composes the 201 response from
/// <see cref="AgentLaunchResult"/> (carrying both the AgentJob key and the
/// AgentSession id) plus the project-scoped transcript URL and job-result
/// URL (product surfaces owned by the API layer, not the launcher).
/// </para>
/// <para>
/// The launch body is bound through <see cref="AgentSessionLaunchBody"/>'s
/// raw-JSON presence binder, which rejects every undeclared top-level
/// field before the Agent lookup so caller-supplied execution-backend
/// overrides cannot alter a named Agent's runtime. The binder accepts
/// only <c>prompt</c> and <c>context</c>; any extra property surfaces an
/// actionable 400 before any session or job is created.
/// </para>
/// </remarks>
public static class AgentSessionLaunchRoutes
{
    internal static readonly IReadOnlySet<string> AllowedTopLevelFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "prompt",
        "context",
    };

    public static WebApplication MapAgentSessionLaunchRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectRef}/agents/{agentRef}")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        group.MapPost("/sessions", async (
            HttpContext context,
            string projectRef,
            string agentRef,
            AgentSessionLaunchBody body,
            AgentQuerier agentQuerier,
            IssueQuerier issueQuerier,
            EpicQuerier epicQuerier,
            IAgentLauncher launcher,
            CancellationToken ct) =>
        {
            if (body is null)
            {
                return ApiResults.BadRequest(
                    "request body is required",
                    "body_required");
            }

            if (body.UndeclaredFields.Count > 0)
            {
                return ApiResults.BadRequest(
                    $"unsupported top-level field(s): {string.Join(", ", body.UndeclaredFields)}; " +
                    "the launch body accepts only prompt and context.",
                    "unsupported_field",
                    new { fields = body.UndeclaredFields.ToArray() });
            }

            var prompt = body.Prompt;
            if (string.IsNullOrWhiteSpace(prompt))
            {
                return ApiResults.BadRequest(
                    "prompt is required",
                    "prompt_required",
                    new { fields = new[] { "prompt" } });
            }

            var idempotencyKey = ReadIdempotencyKey(context.Request);
            if (string.IsNullOrWhiteSpace(idempotencyKey))
            {
                return ApiResults.BadRequest(
                    "Idempotency-Key is required for manual agent launches",
                    "idempotency_key_required",
                    new { fields = new[] { "Idempotency-Key" } });
            }

            var project = context.GetResolvedProject();
            var launchRequest = new AgentLaunchCoordinatorRequest(
                Prompt: prompt?.Trim() ?? string.Empty,
                AgentRef: agentRef,
                Runtime: null,
                WorkspacePath: body.Context?.WorkspacePath,
                IssueNumber: body.Context?.IssueNumber,
                EpicNumber: body.Context?.EpicNumber,
                Repository: body.Context?.Repository,
                Title: null);

            try
            {
                var resumed = await launcher.ResumeIdempotentAsync(
                    project.Id,
                    idempotencyKey,
                    launchRequest,
                    ct);
                if (resumed is not null)
                    return AcceptedLaunch(project.Id, resumed);
            }
            catch (LaunchIdempotencyConflictException ex)
            {
                return ApiResults.Conflict(
                    ex.Message,
                    "launch_idempotency_conflict",
                    new { idempotencyKey = ex.IdempotencyKey });
            }
            catch (LaunchSetupPendingException ex)
            {
                return LaunchSetupPending(ex);
            }

            if (string.IsNullOrWhiteSpace(prompt))
            {
                return ApiResults.BadRequest(
                    "prompt is required",
                    "prompt_required",
                    new { fields = new[] { "prompt" } });
            }

            var agent = await AgentRefResolver.ResolveAsync(agentQuerier, project.Id, agentRef);
            if (agent is null)
            {
                return ApiResults.NotFound($"Agent '{agentRef}' not found");
            }
            if (string.Equals(agent.Status, AgentStatus.Archived, StringComparison.Ordinal))
            {
                return ApiResults.Conflict("Archived agents cannot start new sessions", "agent_archived");
            }

            var contextError = await ValidateContextAsync(body.Context, project.Id, issueQuerier, epicQuerier);
            if (contextError is not null)
                return contextError;

            var launchContext = new AgentLaunchContext(
                ProjectId: project.Id,
                IssueNumber: body.Context?.IssueNumber,
                EpicNumber: body.Context?.EpicNumber,
                Repository: body.Context?.Repository,
                WorkspacePath: body.Context?.WorkspacePath,
                Title: null);

            AgentLaunchResult result;
            try
            {
                result = await launcher.LaunchIdempotentAsync(
                    agent,
                    prompt!,
                    launchContext,
                    idempotencyKey,
                    launchRequest,
                    ct: ct);
            }
            catch (ArgumentException ex)
            {
                return ApiResults.BadRequest(ex.Message, "validation_failed");
            }
            catch (LaunchIdempotencyConflictException ex)
            {
                return ApiResults.Conflict(
                    ex.Message,
                    "launch_idempotency_conflict",
                    new { idempotencyKey = ex.IdempotencyKey });
            }
            catch (LaunchSetupPendingException ex)
            {
                return LaunchSetupPending(ex);
            }

            return AcceptedLaunch(project.Id, result);
        });

        return app;
    }

    private static IResult AcceptedLaunch(string projectId, AgentLaunchResult result)
    {
        return Results.Json(
                new ApiResponse<AgentSessionLaunchResponse>(
                    true,
                    new AgentSessionLaunchResponse(
                        JobId: result.JobKey,
                        SessionId: result.SessionId,
                        InputId: result.InputId,
                        TurnId: result.TurnId,
                        AgentId: result.AgentId,
                        AgentName: result.AgentName,
                        Status: "queued",
                        TranscriptUrl: $"/api/projects/{Uri.EscapeDataString(projectId)}/agent-sessions/{Uri.EscapeDataString(result.SessionId)}/transcript",
                        JobUrl: $"/api/projects/{Uri.EscapeDataString(projectId)}/agent-jobs/{Uri.EscapeDataString(result.JobKey)}",
                        ObservationUrl: $"/api/projects/{Uri.EscapeDataString(projectId)}/agent-jobs/{Uri.EscapeDataString(result.JobKey)}/launch-observation")),
                statusCode: 201);
    }

    private static IResult LaunchSetupPending(LaunchSetupPendingException exception) =>
        ApiResults.Fail(
            exception.Message,
            StatusCodes.Status503ServiceUnavailable,
            "launch_setup_pending",
            new { idempotencyKey = exception.IdempotencyKey });

    private static async Task<IResult?> ValidateContextAsync(
        AgentSessionLaunchContextRef? context,
        string projectId,
        IssueQuerier issueQuerier,
        EpicQuerier epicQuerier)
    {
        if (context?.IssueNumber is <= 0)
            return ApiResults.BadRequest("issueNumber must be positive", "validation_failed");
        if (context?.EpicNumber is <= 0)
            return ApiResults.BadRequest("epicNumber must be positive", "validation_failed");

        if (context?.IssueNumber is int issueNumber
            && await issueQuerier.GetAsync(projectId, issueNumber) is null)
        {
            return ApiResults.NotFound($"Issue #{issueNumber} not found");
        }

        if (context?.EpicNumber is int epicNumber
            && await epicQuerier.GetAsync(projectId, epicNumber) is null)
        {
            return ApiResults.NotFound($"Epic #{epicNumber} not found");
        }

        return null;
    }
    private static string? ReadIdempotencyKey(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Idempotency-Key", out var values))
            return null;
        if (values.Count == 0)
            return null;
        var value = values[0];
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

/// <summary>
/// Raw-JSON presence-bound body for
/// <c>POST /api/projects/{projectRef}/agents/{agentRef}/sessions</c>.
/// Records every top-level JSON property name so the route can reject
/// callers that try to override the Agent's execution definition
/// (e.g. a <c>runtime</c> field). Only <c>prompt</c> and <c>context</c>
/// are accepted; any other property surfaces an actionable 400 before
/// Agent lookup, AgentSession creation, or AgentJob submission.
/// </summary>
public sealed record AgentSessionLaunchBody(
    string? Prompt,
    AgentSessionLaunchContextRef? Context,
    IReadOnlyList<string> UndeclaredFields,
    JsonElement Raw)
{
    public static async ValueTask<AgentSessionLaunchBody?> BindAsync(HttpContext context)
    {
        try
        {
            return await BindCoreAsync(context);
        }
        catch (JsonException)
        {
            return new AgentSessionLaunchBody(null, null, [], default);
        }
    }

    private static async ValueTask<AgentSessionLaunchBody> BindCoreAsync(HttpContext context)
    {
        var raw = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body, JSON.Options);
        if (raw.ValueKind != JsonValueKind.Object)
        {
            return new AgentSessionLaunchBody(
                Prompt: null,
                Context: null,
                UndeclaredFields: [],
                Raw: raw);
        }

        var undeclared = new List<string>();
        foreach (var property in raw.EnumerateObject())
        {
            if (!AgentSessionLaunchRoutes.AllowedTopLevelFields.Contains(property.Name))
                undeclared.Add(property.Name);
        }

        var prompt = raw.TryGetProperty("prompt", out var promptElement)
                     && promptElement.ValueKind != JsonValueKind.Null
            ? promptElement.ValueKind == JsonValueKind.String
                ? promptElement.GetString()
                : throw new JsonException("prompt must be a string")
            : null;

        AgentSessionLaunchContextRef? ctx = null;
        if (raw.TryGetProperty("context", out var ctxElement)
            && ctxElement.ValueKind == JsonValueKind.Object)
        {
            ctx = new AgentSessionLaunchContextRef(
                IssueNumber: TryReadPositiveInt(ctxElement, "issueNumber"),
                EpicNumber: TryReadPositiveInt(ctxElement, "epicNumber"),
                Repository: TryReadString(ctxElement, "repository"),
                WorkspacePath: TryReadString(ctxElement, "workspacePath"));
        }

        return new AgentSessionLaunchBody(
            Prompt: prompt,
            Context: ctx,
            UndeclaredFields: undeclared,
            Raw: raw);
    }

    private static string? TryReadString(JsonElement parent, string name) =>
        !parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null
            ? null
            : value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : throw new JsonException($"context.{name} must be a string");

    private static int? TryReadPositiveInt(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
            throw new JsonException($"context.{name} must be an integer");
        return number;
    }
}

public sealed record AgentSessionLaunchContextRef(
    int? IssueNumber = null,
    int? EpicNumber = null,
    string? Repository = null,
    string? WorkspacePath = null);

/// <summary>
/// Response body for a successful generic AgentSession launch. A launch
/// creates two entities atomically — an <c>AgentJob</c> (the work owner)
/// and an <c>AgentSession</c> (the conversation owner) — so the response
/// surfaces both identities: <see cref="JobId"/> is the AgentJob grain
/// key the launcher minted (the same id the AgentJob read surface
/// accepts — there is no translation gap between launch and read), and
/// <see cref="SessionId"/> is the conversation owner. The agent id and
/// name echo the resolved profile; the status reflects the initial state
/// immediately after dispatch; <see cref="TranscriptUrl"/> points at the
/// product read path for the generic session transcript, and
/// <see cref="JobUrl"/> points at the AgentJob read surface.
/// </summary>
public sealed record AgentSessionLaunchResponse(
    string JobId,
    string SessionId,
    string InputId,
    string TurnId,
    string AgentId,
    string AgentName,
    string Status,
    string TranscriptUrl,
    string JobUrl,
    string ObservationUrl);
