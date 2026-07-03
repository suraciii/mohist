using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Api;

/// <summary>
/// Product launch endpoint for a generic AgentSession from a project-scoped
/// Agent profile (issue-129 T-003). Distinct from the validation-only
/// <c>POST /api/agent-jobs/validate</c> route, which remains a developer
/// smoke-test surface and is not the product API.
/// </summary>
/// <remarks>
/// <para>
/// The handler resolves the Agent in the project via
/// <see cref="AgentQuerier"/>, composes the Agent's
/// <c>Instructions + AgentConfig</c> with the caller's prompt (and any
/// optional context references) into an <see cref="AgentJobInput"/>
/// snapshot, mints the sessionId up front via
/// <see cref="IAgentSessionGrain.OpenAsync"/> with <c>source-kind =
/// agent-launch</c> labels + agent id/name + context-ref annotations, then
/// submits the <see cref="AgentJobGrain"/> carrying the minted sessionId,
/// and returns <c>201 { sessionId, agentId, agentName, status, transcriptUrl }</c>.
/// </para>
/// <para>
/// Empty/whitespace prompt is rejected with 400 before any session or job
/// is created; unknown agentRef is rejected with 404 before any session is
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
            AgentSessionResolver sessions,
            IGrainFactory grains,
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

            var sessionId = sessions.NewSessionId();
            var context_ = BuildContext(project.Id, agent, prompt, req!);
            var sessionGrain = sessions.GetGrain(sessionId);
            await sessionGrain.OpenAsync(new OpenAgentSessionCommand(
                RunnerId: string.Empty,
                AgentRuntime: "opencode",
                WorkDir: req!.Context?.WorkspacePath,
                Metadata: GenericAgentSessionMetadata.Metadata(context_)));

            var jobKey = $"agent-job-launch-{Guid.NewGuid():N}";
            var jobGrain = grains.GetGrain<IAgentJobGrain>(jobKey);
            var input = new AgentJobInput(
                Prompt: prompt.Trim(),
                Model: null,
                WorkspacePath: req.Context?.WorkspacePath,
                ProjectId: project.Id,
                Uses: "mohist/acp-agent",
                AgentId: agent.Id,
                AgentInstructions: string.IsNullOrWhiteSpace(agent.Instructions) ? null : agent.Instructions,
                AgentConfig: agent.AgentConfig?.Clone(),
                AgentSessionId: sessionId);

            try
            {
                await jobGrain.SubmitAsync(input);
            }
            catch (ArgumentException ex)
            {
                return ApiResults.BadRequest(ex.Message, "validation_failed");
            }

            return Results.Json(
                new ApiResponse<AgentSessionLaunchResponse>(
                    true,
                    new AgentSessionLaunchResponse(
                        SessionId: sessionId,
                        AgentId: agent.Id,
                        AgentName: agent.Name,
                        Status: "inactive",
                        TranscriptUrl: $"/api/projects/{Uri.EscapeDataString(project.Id)}/agent-sessions/{Uri.EscapeDataString(sessionId)}/transcript")),
                statusCode: 201);
        });

        return app;
    }

    private static GenericAgentSessionContext BuildContext(
        string projectId,
        AgentInfo agent,
        string prompt,
        AgentSessionLaunchRequest req)
    {
        var contextRefs = req.Context;
        return new GenericAgentSessionContext(
            ProjectId: projectId,
            AgentId: agent.Id,
            AgentName: agent.Name,
            IssueNumber: contextRefs?.IssueNumber,
            EpicNumber: contextRefs?.EpicNumber,
            Repository: contextRefs?.Repository,
            WorkspacePath: contextRefs?.WorkspacePath,
            Title: null);
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
    AgentSessionLaunchContextRef? Context = null);

public sealed record AgentSessionLaunchContextRef(
    int? IssueNumber = null,
    string? EpicNumber = null,
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
