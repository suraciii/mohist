using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Queries;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Workflow.Projection;

namespace Mohist.Server.Api;

public static class AgentRoutes
{
    public static WebApplication MapAgentRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/agent");

        group.MapGet("/status", async (string? projectId, IGrainFactory grains, WorkflowProjectionService projection, ConfigService config, IConfiguration configuration) =>
        {
            if (string.IsNullOrWhiteSpace(projectId))
            {
                return ApiResults.BadRequest("No active project");
            }

            var runnerIds = await ListAvailableRunnerIdsAsync(grains, projectId);
            var activeAgents = await projection.ListActiveAgentsAsync(projectId);
            var maxConcurrentAgents = await MaxConcurrentAgentsAsync(config);

            return ApiResults.Ok(AgentStatusResponse.Create(activeAgents, runnerIds, maxConcurrentAgents));
        });

        group.MapGet("/sessions", async (string projectId, string? status, int? limit, WorkflowAgentSessionQueryService sessions) =>
        {
            return ApiResults.Ok(await sessions.ListCurrentAsync(projectId, status, limit ?? 50));
        });

        group.MapGet("/activity", async (string projectId, int? limit, WorkflowAgentSessionQueryService sessions, IGrainFactory grains, WorkflowProjectionService projection, CancellationToken ct) =>
        {
            var runnerIds = await ListAvailableRunnerIdsAsync(grains, projectId);
            return ApiResults.Ok(await sessions.GetActivityAsync(projectId, limit, runnerIds: runnerIds, ct: ct));
        });

        return app;
    }

    private static async Task<int> MaxConcurrentAgentsAsync(ConfigService config)
    {
        var cfg = await config.GetConfigAsync();
        return cfg.TryGetValue("maxConcurrentAgents", out var value) && value is int n ? n : 3;
    }

    private static async Task<IReadOnlyList<string>> ListAvailableRunnerIdsAsync(IGrainFactory grains, string projectId)
    {
        var projectRunnerIds = await grains.GetGrain<IRunnerRegistryGrain>(GrainKey.RunnerRegistry(projectId)).ListRunnerIdsAsync();
        var globalRunnerIds = await grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global).ListRunnerIdsAsync();
        return projectRunnerIds.Concat(globalRunnerIds).Distinct(StringComparer.Ordinal).ToArray();
    }
}

public sealed record AgentStatusResponse(
    bool Running,
    string? IssueId,
    int? IssueNumber,
    IReadOnlyList<ActiveAgentDto> ActiveAgents,
    AgentCapacityResponse Capacity,
    bool RunnerAvailable,
    bool EmbeddedRunnerEnabled,
    string? RunnerMessage,
    IReadOnlyList<RunnerStatusResponse> Runners)
{
    public static AgentStatusResponse Create(IReadOnlyList<ActiveAgentDto> activeAgents, IReadOnlyList<string> runnerIds, int maxConcurrentAgents)
    {
        var runnerAvailable = runnerIds.Count > 0;
        return new AgentStatusResponse(
            Running: activeAgents.Count > 0,
            IssueId: activeAgents.FirstOrDefault()?.IssueId,
            IssueNumber: activeAgents.FirstOrDefault()?.IssueNumber,
            ActiveAgents: activeAgents,
            Capacity: new AgentCapacityResponse(activeAgents.Count, maxConcurrentAgents),
            RunnerAvailable: runnerAvailable,
            EmbeddedRunnerEnabled: false,
            RunnerMessage: runnerAvailable ? null : "No runner is connected. Start the Mohist runner process.",
            Runners: runnerIds.Select(id => new RunnerStatusResponse(id, "external")).ToArray());
    }
}

public sealed record AgentCapacityResponse(int Active, int Max);
public sealed record RunnerStatusResponse(string Id, string Kind);
