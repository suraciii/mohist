using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions;
using Mohist.Server.Project.Grains;
using Mohist.Server.Workflow.Projection;

namespace Mohist.Server.Api;

public static class AgentRoutes
{
    private const string ProjectKey = "projects";

    public static WebApplication MapAgentRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/agent");

        group.MapGet("/status", async (IGrainFactory grains, WorkflowProjectionService projection) =>
        {
            var registry = grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Key);
            var runnerIds = await registry.ListRunnerIdsAsync();
            var projectId = await ResolveProjectIdAsync(grains);
            var activeAgents = await projection.ListActiveAgentsAsync(projectId);

            return ApiResults.Ok(new
            {
                running = runnerIds.Count > 0 || activeAgents.Count > 0,
                issueId = activeAgents.FirstOrDefault()?.IssueId,
                issueNumber = activeAgents.FirstOrDefault()?.IssueNumber,
                activeAgents,
                maxConcurrentAgents = runnerIds.Count,
                queueDepth = 0,
                waitingQuestions = Array.Empty<object>(),
                recoverableIssues = Array.Empty<object>(),
                runners = runnerIds.Select(id => new { id }).ToArray(),
            });
        });

        group.MapGet("/sessions", async (string? projectId, string? status, int? limit, IGrainFactory grains, AgentSessionService sessions) =>
        {
            var pid = projectId ?? await ResolveProjectIdAsync(grains);
            if (pid is null) return ApiResults.BadRequest("No active project");
            return ApiResults.Ok(await sessions.ListCurrentAsync(pid, status, limit ?? 50));
        });

        group.MapGet("/activity", async (string? projectId, int? limit, IGrainFactory grains, AgentActivityService activity, CancellationToken ct) =>
        {
            var pid = projectId ?? await ResolveProjectIdAsync(grains);
            if (pid is null) return ApiResults.BadRequest("No active project");
            return ApiResults.Ok(await activity.GetAsync(pid, limit, ct));
        });

        group.MapGet("/session-status", () => ApiResults.Ok(new
        {
            sessionId = (string?)null,
            acpSessionId = (string?)null,
            status = (string?)null,
            currentSessionState = "No active session",
            lastDataAt = (string?)null,
            probeSentAt = (string?)null,
            probeDeadlineAt = (string?)null,
            failureReason = (string?)null,
        }));

        return app;
    }

    private static async Task<string?> ResolveProjectIdAsync(IGrainFactory grains)
    {
        var projectsGrain = grains.GetGrain<IProjectGrain>(ProjectKey);
        var projects = await projectsGrain.GetAllAsync();
        return projects.Count == 1 ? projects[0].Id : null;
    }
}
