using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions;
using Mohist.Server.Config.Domain;
using Mohist.Server.Project.Grains;
using Mohist.Server.Workflow.Projection;

namespace Mohist.Server.Api;

public static class AgentRoutes
{
    private const string ProjectKey = "projects";

    public static WebApplication MapAgentRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/agent");

        group.MapGet("/status", async (IGrainFactory grains, WorkflowProjectionService projection, ConfigService config, IConfiguration configuration) =>
        {
            var registry = grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Key);
            var runnerIds = await registry.ListRunnerIdsAsync();
            var projectId = await ResolveProjectIdAsync(grains);
            var activeAgents = await projection.ListActiveAgentsAsync(projectId);
            var maxConcurrentAgents = await MaxConcurrentAgentsAsync(config);
            var runnerAvailable = runnerIds.Count > 0;

            return ApiResults.Ok(new
            {
                running = activeAgents.Count > 0,
                issueId = activeAgents.FirstOrDefault()?.IssueId,
                issueNumber = activeAgents.FirstOrDefault()?.IssueNumber,
                activeAgents,
                capacity = new { active = activeAgents.Count, max = maxConcurrentAgents },
                runnerAvailable,
                embeddedRunnerEnabled = false,
                runnerMessage = runnerAvailable
                    ? null
                    : "No runner is connected. Start the Mohist runner process.",
                runners = runnerIds.Select(id => new { id, kind = "external" }).ToArray(),
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

        return app;
    }

    private static async Task<int> MaxConcurrentAgentsAsync(ConfigService config)
    {
        var cfg = await config.GetConfigAsync();
        return cfg.TryGetValue("maxConcurrentAgents", out var value) && value is int n ? n : 3;
    }

    private static async Task<string?> ResolveProjectIdAsync(IGrainFactory grains)
    {
        var projectsGrain = grains.GetGrain<IProjectGrain>(ProjectKey);
        var projects = await projectsGrain.GetAllAsync();
        return projects.Count == 1 ? projects[0].Id : null;
    }
}
