using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Epic.Services;
using Mohist.Server.Issue.Services;
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
/// The route now delegates the canonical mint-session → open-generic-session
/// → build-AgentJobInput → submit-to-grain pipeline to
/// <see cref="IAgentLauncher"/>. The route keeps its three domain-level
/// gates (whitespace prompt → 400; unresolved agent → 404; archived agent
/// → 409) and composes the 201 response from
/// <see cref="AgentLaunchResult"/> + the project-scoped transcript URL
/// (a product surface owned by the API layer, not the launcher).
/// </para>
/// <para>
/// Empty/whitespace prompt is rejected with 400 before any session or job
/// is created; unknown agentRef is rejected with 404 before any session is
/// created; archived agents are rejected with 409 before any session is
/// created.
/// </para>
/// </remarks>
public static class AgentSessionLaunchRoutes
{
    public static WebApplication MapAgentSessionLaunchRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectRef}/agents/{agentRef}")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        group.MapPost("/sessions", async (
            HttpContext context,
            string projectRef,
            string agentRef,
            AgentSessionLaunchRequest req,
            AgentQuerier agentQuerier,
            IssueQuerier issueQuerier,
            IssueWorkflowProfileManager issueWorkflowProfileManager,
            EpicQuerier epicQuerier,
            IAgentLauncher launcher,
            CancellationToken ct) =>
        {
            var prompt = req?.Prompt;
            if (string.IsNullOrWhiteSpace(prompt))
            {
                return ApiResults.BadRequest(
                    "prompt is required",
                    "prompt_required",
                    new { fields = new[] { "prompt" } });
            }

            var project = context.GetResolvedProject();
            var agent = await AgentRefResolver.ResolveAsync(agentQuerier, project.Id, agentRef);
            if (agent is null)
            {
                return ApiResults.NotFound($"Agent '{agentRef}' not found");
            }
            if (string.Equals(agent.Status, AgentStatus.Archived, StringComparison.Ordinal))
            {
                return ApiResults.Conflict("Archived agents cannot start new sessions", "agent_archived");
            }

            var contextError = await ValidateContextAsync(req!.Context, project.Id, issueQuerier, epicQuerier);
            if (contextError is not null)
                return contextError;

            var launchContext = new AgentLaunchContext(
                ProjectId: project.Id,
                IssueNumber: req.Context?.IssueNumber,
                EpicNumber: req.Context?.EpicNumber,
                Repository: req.Context?.Repository,
                WorkspacePath: req.Context?.WorkspacePath,
                Title: null);

            var runtimeOverride = req.Runtime;
            if (runtimeOverride is null && req.Context?.IssueNumber is int issueNumber)
            {
                runtimeOverride = await issueWorkflowProfileManager
                    .GetAgentRuntimeOverrideAsync(project.Id, issueNumber);
            }

            var runtimeError = ValidateRuntimeOverride(runtimeOverride);
            if (runtimeError is not null)
            {
                return ApiResults.BadRequest(runtimeError, "runtime_invalid");
            }

            AgentLaunchResult result;
            try
            {
                result = await launcher.LaunchAsync(
                    agent,
                    prompt,
                    launchContext,
                    triggerLabels: null,
                    runtimeOverride: runtimeOverride,
                    ct: ct);
            }
            catch (ArgumentException ex)
            {
                return ApiResults.BadRequest(ex.Message, "validation_failed");
            }

            return Results.Json(
                new ApiResponse<AgentSessionLaunchResponse>(
                    true,
                    new AgentSessionLaunchResponse(
                        SessionId: result.SessionId,
                        AgentId: result.AgentId,
                        AgentName: result.AgentName,
                        Status: "inactive",
                        TranscriptUrl: $"/api/projects/{Uri.EscapeDataString(project.Id)}/agent-sessions/{Uri.EscapeDataString(result.SessionId)}/transcript")),
                statusCode: 201);
        });

        return app;
    }

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

    private static string? ValidateRuntimeOverride(string? runtime)
    {
        if (runtime is null) return null;
        if (!Mohist.Server.Infrastructure.AgentConfigSchema.AllowedRuntimes.Contains(runtime))
        {
            return $"runtime '{runtime}' is not supported; the agent runtime accepts only " +
                string.Join(", ", Mohist.Server.Infrastructure.AgentConfigSchema.AllowedRuntimes) + ".";
        }
        return null;
    }

}

/// <summary>
/// Body for <c>POST /api/projects/{projectRef}/agents/{agentRef}/sessions</c>.
/// The prompt is required; <see cref="Context"/> is optional and records
/// context references (issue, epic, repository, workspace path) as session
/// metadata without creating scope/mount/supervisor lifecycle.
/// </summary>
public sealed record AgentSessionLaunchRequest(
    string? Prompt = null,
    AgentSessionLaunchContextRef? Context = null,
    /// <summary>
    /// Optional launch-time override of the execution backend
    ///. When set, wins over the Agent's configured
    /// <c>runtime</c> in <c>agentConfig</c>; when absent, the Agent's
    /// configured backend applies (defaulting to <c>opencode</c>).
    /// Accepted values: <c>opencode</c>, <c>pi</c>.
    /// </summary>
    string? Runtime = null);

public sealed record AgentSessionLaunchContextRef(
    int? IssueNumber = null,
    int? EpicNumber = null,
    string? Repository = null,
    string? WorkspacePath = null);

/// <summary>
/// Response body for a successful generic AgentSession launch. The session
/// id is the caller-observable identity (matches the runtime-event
/// identity on the runner side); the agent id and name echo the resolved
/// profile; the status reflects the initial state immediately after
/// dispatch; transcriptUrl points at the product read path for the generic
/// session transcript.
/// </summary>
public sealed record AgentSessionLaunchResponse(
    string SessionId,
    string AgentId,
    string AgentName,
    string Status,
    string TranscriptUrl);
