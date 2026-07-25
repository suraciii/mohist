using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Mohist.Server.AgentOps.Services;
using Mohist.Server.Issue.Services;
using Mohist.Server.Otel;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Services;
using Mohist.Server.Runner.Services;
using Mohist.Server.Sessions;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Api;

public static class AgentStatusHandlers
{
    public static async Task<IResult> GetStatusAsync(
        ProjectInfo project,
        RunnerStatusService runnerStatus,
        WorkflowActivityQuerier projection,
        CancellationToken ct)
    {
        var scope = RequestWorkScope.Current;
        scope?.SetAgentPath("agent.status");

        var runners = await runnerStatus.GetOnlineRunnersAsync(project.Id);
        var activeAgents = await projection.ListActiveAgentsResultAsync(project.Id, ct);
        scope?.AddCandidates(activeAgents.Candidates);
        scope?.AddProcessed(activeAgents.ActiveAgents.Count);

        var amplification = CurrentAmplification();
        return ApiResults.Ok(AgentStatusResponse.Create(
            activeAgents.ActiveAgents,
            runners,
            SumCapacity(runners),
            amplification));
    }

    public static async Task<IResult> GetActivityAsync(
        ProjectInfo project,
        int? limit,
        AgentActivityFeedAssembler activityFeed,
        IssueQuerier issues,
        RunnerStatusService runnerStatus,
        CancellationToken ct)
    {
        RequestWorkScope.Current?.SetAgentPath("agent.activity");

        var capacity = await runnerStatus.GetCapacityAsync(project.Id);
        var waiting = await BuildWaitingCardsAsync(issues, project.Id);
        var activity = await activityFeed.GetActivityAsync(
            project.Id,
            limit,
            waiting: waiting,
            capacity: capacity,
            ct: ct);
        var scope = RequestWorkScope.Current;
        scope?.AddCandidates(activity.Amplification.Candidates);
        scope?.AddProcessed(activity.Amplification.Processed);
        scope?.AddTranscriptRecords(activity.Amplification.TranscriptRecords);

        return ApiResults.Ok(activity with { Amplification = CurrentAmplification() });
    }

    public static async Task<(IResult? Error, ProjectInfo? Project)> ResolveAliasedProjectAsync(
        HttpContext context,
        ProjectRefResolver resolver)
    {
        var selected = SelectProjectRef(
            context.Request.Query["projectId"],
            context.Request.Headers["X-Mohist-Project"]);
        if (selected is null)
            return (ApiResults.BadRequest("No active project"), null);

        var project = await resolver.ResolveAsync(selected);
        return project is null
            ? (ApiResults.NotFound("Project not found"), null)
            : (null, project);
    }

    public static string? SelectProjectRef(StringValues queryValues, StringValues headerValues) =>
        FirstNonblank(queryValues) ?? FirstNonblank(headerValues);

    private static string? FirstNonblank(StringValues values)
    {
        foreach (var value in values)
        {
            var trimmed = value?.Trim();
            if (!string.IsNullOrEmpty(trimmed)) return trimmed;
        }

        return null;
    }

    private static AgentAmplificationDto CurrentAmplification()
    {
        var snapshot = RequestWorkScope.Current?.Snapshot() ?? default;
        return new AgentAmplificationDto(
            snapshot.Candidates,
            snapshot.Processed,
            snapshot.TranscriptRecords,
            snapshot.DatabaseCalls,
            snapshot.DownstreamCalls);
    }

    private static async Task<IReadOnlyList<ActivityWaitingCardDto>> BuildWaitingCardsAsync(
        IssueQuerier issues,
        string projectId)
    {
        var waiting = await issues.ListInProgressWithApprovalGateAsync(projectId);
        return waiting
            .Select(issue => new ActivityWaitingCardDto(
                issue.Number,
                string.IsNullOrWhiteSpace(issue.Title) ? $"Issue #{issue.Number}" : issue.Title,
                issue.WorkflowStage,
                "Needs Approval",
                issue.StageApproval?.RequestedAt.ToString("o"),
                null))
            .ToList();
    }

    private static RunnerCapacityView SumCapacity(IReadOnlyList<RunnerStatusView> runners)
    {
        var used = 0;
        var total = 0;
        foreach (var runner in runners)
        {
            if (runner.Capacity is not { } capacity) continue;
            used += capacity.UsedSlots;
            total += capacity.TotalSlots;
        }

        return new RunnerCapacityView(used, total);
    }
}
