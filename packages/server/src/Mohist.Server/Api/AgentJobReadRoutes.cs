using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure;
using Mohist.Server.Runner.Services;
using Mohist.Server.Project.Services;

namespace Mohist.Server.Api;

/// <summary>
/// Product read surface for <see cref="IAgentJobGrain"/>.
/// <c>list</c> reads the relational <c>AgentJobs</c> mirror (the queryable
/// index, written through by the grain); <c>view</c> reads the grain directly
/// so the detail is always authoritative — including for jobs in-flight or
/// terminal at cutover, which load their real state from
/// <c>[PersistentState]</c>. <c>view</c> enforces project isolation from the
/// grain's <c>State.Input.ProjectId</c> (manual-launch keys are global GUIDs).
/// </summary>
public static class AgentJobReadRoutes
{
    public static WebApplication MapAgentJobReadRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectRef}")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        group.MapGet("/agents/{agentRef}/jobs", (
            HttpContext context,
            string projectRef,
            string agentRef,
            string? status,
            int? limit,
            AgentQuerier agentQuerier,
            AgentJobQuerier jobs,
            CancellationToken ct) =>
            HandleListAsync(context.GetResolvedProject(), agentRef, status, limit, agentQuerier, jobs, ct));

        group.MapGet("/agents/{agentRef}/status", async (
            HttpContext context,
            string agentRef,
            AgentQuerier agentQuerier,
            AgentAvailabilityService availability,
            CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            var agent = await AgentRefResolver.ResolveAsync(agentQuerier, project.Id, agentRef);
            if (agent is null)
                return ApiResults.NotFound($"Agent '{agentRef}' not found");

            var conclusion = await availability.GetAsync(project.Id, agent, ct);
            if (conclusion is null)
                return ApiResults.NotFound($"Agent '{agentRef}' not found");

            var waiting = await availability.GetWaitingWorkAsync(project.Id, agent, conclusion, ct);
            return ApiResults.Ok(new AgentStatusDetailResponse(
                agent.Id,
                agent.Name,
                new AgentAvailabilityResponse(
                    conclusion.CanStartNow,
                    conclusion.WaitingReason,
                    conclusion.ActiveRuns,
                    conclusion.MaxConcurrentRuns,
                    conclusion.Capacity,
                    conclusion.ObservedAt.ToString("o")),
                waiting));
        });

        group.MapGet("/agent-jobs/{jobId}", (
            HttpContext context,
            string projectRef,
            string jobId,
            IGrainFactory grains,
            CancellationToken ct) =>
            HandleViewAsync(context.GetResolvedProject(), jobId, grains));

        return app;
    }

    internal static async Task<IResult> HandleListAsync(
        ProjectInfo project,
        string agentRef,
        string? status,
        int? limit,
        AgentQuerier agentQuerier,
        AgentJobQuerier jobs,
        CancellationToken ct)
    {
        var agent = await AgentRefResolver.ResolveAsync(agentQuerier, project.Id, agentRef);
        if (agent is null)
            return ApiResults.NotFound($"Agent '{agentRef}' not found");

        HashSet<AgentJobStatus>? statusSet = null;
        HashSet<string>? requestedStatuses = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            requestedStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            statusSet = [];
            foreach (var token in status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (string.Equals(token, "recovering", StringComparison.OrdinalIgnoreCase))
                {
                    requestedStatuses.Add("recovering");
                    statusSet.Add(AgentJobStatus.Unknown);
                    continue;
                }

                if (!Enum.TryParse<AgentJobStatus>(token, ignoreCase: true, out var parsed))
                    return ApiResults.BadRequest(
                        $"Unknown status '{token}'. Valid values: pending, running, completed, failed, unknown, recovering.");
                requestedStatuses.Add(parsed.ToString());
                statusSet.Add(parsed);
            }
        }

        var items = await jobs.ListByAgentAsync(project.Id, agent.Id, statusSet, limit ?? 50, ct);
        if (requestedStatuses is not null)
            items = items
                .Where(item => requestedStatuses.Contains(item.Status ?? string.Empty))
                .ToList();
        var dtos = items
            .Select(item => new AgentJobListItemDto(
                JobId: item.JobKey,
                AgentId: item.AgentId,
                AgentName: agent.Name,
                Status: item.Status,
                SubmittedAt: item.SubmittedAt,
                TerminalAt: item.TerminalAt,
                FailureReason: item.FailureReason,
                RecoveryDeadlineAt: item.RecoveryDeadlineAt))
            .ToList();
        return ApiResults.Ok(dtos);
    }

    internal static async Task<IResult> HandleViewAsync(
        ProjectInfo project,
        string jobId,
        IGrainFactory grains)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return ApiResults.NotFound("Job not found");

        var grain = grains.GetGrain<IAgentJobGrain>(jobId);
        var snapshot = await grain.GetRuntimeSnapshotAsync();

        if (!string.Equals(snapshot.ProjectId, project.Id, StringComparison.Ordinal))
            return ApiResults.NotFound("Job not found");

        var status = await grain.GetStatusAsync();
        var isRecovering = status == AgentJobStatus.Unknown && snapshot.IsRecovering;
        var isTerminal = status is AgentJobStatus.Completed or AgentJobStatus.Failed or AgentJobStatus.Cancelled;
        // Unknown is nonterminal; surface it without
        // the terminal-result fields. Callers consume it as a
        // nonterminal, non-dispatchable state — neither successful
        // nor failed — and act on it via the launch-observation read.

        string? message = null;
        string? output = null;
        IReadOnlyList<string>? artifactUploadIds = null;
        string? failureReason = null;
        int? exitCode = null;
        if (isTerminal)
        {
            var terminal = await grain.GetTerminalResultAsync();
            message = terminal.Message;
            output = terminal.Output;
            artifactUploadIds = terminal.ArtifactUploadIds;
            failureReason = terminal.FailureReason;
            exitCode = terminal.ExitCode;
        }
        else if (status == AgentJobStatus.Unknown)
        {
            failureReason = snapshot.FailureReason;
        }

        return ApiResults.Ok(new AgentJobViewDto(
            JobId: jobId,
            Status: ToStatusString(status, isRecovering),
            Message: message,
            Output: output,
            ArtifactUploadIds: artifactUploadIds,
            FailureReason: failureReason,
            ExitCode: exitCode,
            ExecutionDefinition: snapshot.ExecutionDefinition,
            RecoveryDeadlineAt: isRecovering ? snapshot.RecoveryDeadlineAt : null));
    }

    private static string ToStatusString(AgentJobStatus status, bool isRecovering = false) =>
        isRecovering && status == AgentJobStatus.Unknown
            ? "recovering"
            : status switch
    {
        AgentJobStatus.Pending => "pending",
        AgentJobStatus.Running => "running",
        AgentJobStatus.Completed => "completed",
        AgentJobStatus.Failed => "failed",
        AgentJobStatus.Cancelled => "cancelled",
        AgentJobStatus.Unknown => "unknown",
        AgentJobStatus.RecoverablyInterrupted => "recoverably-interrupted",
        _ => "unknown",
    };
}

/// <summary>
/// Read shape for a single AgentJob in the agent-scoped list
/// (<c>GET /api/projects/{projectRef}/agents/{agentRef}/jobs</c>). Sourced from
/// the relational <c>AgentJobs</c> mirror row; <see cref="AgentName"/> is the
/// resolved agent profile name (constant across the agent-scoped list).
/// </summary>
public sealed record AgentJobListItemDto(
    string JobId,
    string? AgentId,
    string? AgentName,
    string? Status,
    string? SubmittedAt,
    string? TerminalAt,
    string? FailureReason = null,
    DateTimeOffset? RecoveryDeadlineAt = null);

public sealed record AgentStatusDetailResponse(
    string AgentId,
    string AgentName,
    AgentAvailabilityResponse Availability,
    IReadOnlyList<AgentWaitingWork> WaitingWork);

public sealed record AgentAvailabilityResponse(
    bool CanStartNow,
    string? WaitingReason,
    int ActiveRuns,
    int? MaxConcurrentRuns,
    RunnerCapacityView Capacity,
    string ObservedAt);

/// <summary>
/// Authoritative read shape for a single AgentJob
/// (<c>GET /api/projects/{projectRef}/agent-jobs/{jobId}</c>), sourced from the
/// grain. The terminal-result fields (<see cref="Message"/>, <see cref="Output"/>,
/// <see cref="ArtifactUploadIds"/>, <see cref="FailureReason"/>, <see cref="ExitCode"/>)
/// are populated only once the job is terminal; for a pending/running job they are
/// <c>null</c> (absent rather than fabricated).
/// </summary>
public sealed record AgentJobViewDto(
    string JobId,
    string Status,
    string? Message,
    string? Output,
    IReadOnlyList<string>? ArtifactUploadIds,
    string? FailureReason,
    int? ExitCode,
    AgentExecutionDefinition? ExecutionDefinition,
    DateTimeOffset? RecoveryDeadlineAt = null);
