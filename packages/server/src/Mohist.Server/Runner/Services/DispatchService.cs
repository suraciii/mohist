using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Orleans;

namespace Mohist.Server.Runner.Services;

/// <summary>
/// Stateless scheduler that computes a runner's dispatches per poll from
/// persisted state — no cursor, no cache, no ledger of its own. Every decision
/// (repair, claim, fairness) is a fresh query over the store, repaired by the
/// next poll. See <c>design/workflow/scheduling.md</c> §Poll Reconciliation.
/// </summary>
public sealed class DispatchService : IScopedService
{
    private readonly IGrainFactory _grains;
    private readonly WorkflowRunQuerier _workflowRuns;
    private readonly WorkflowItemTranslator _translator;
    private readonly ILogger<DispatchService> _log;

    public DispatchService(
        IGrainFactory grains,
        WorkflowRunQuerier workflowRuns,
        WorkflowItemTranslator translator,
        ILogger<DispatchService> log)
    {
        _grains = grains;
        _workflowRuns = workflowRuns;
        _translator = translator;
        _log = log;
    }

    /// <summary>
    /// One poll = one reconciliation round. Steps (spec §114-145):
    /// <list type="number">
    ///   <item><description>touch presence (poll is heartbeat).</description></item>
    ///   <item><description>desired ← Running runs assigned to me.</description></item>
    ///   <item><description>repair = desired − reported → re-render each from the run.</description></item>
    ///   <item><description>spare = slots − |desired|; claim Ready runs (ReadySince ASC), then claimable Pending.</description></item>
    /// </list>
    /// Ordering: repair first (debts already owed), then serve assigned Ready
    /// runs, then claim new workflows — held work always precedes expansion.
    /// </summary>
    public async Task<RunnerPollResponse> PollAsync(
        string runnerId,
        RunnerPollRequest req,
        int slots,
        CancellationToken ct = default)
    {
        var runner = _grains.GetGrain<IRunnerGrain>(runnerId);
        // ① poll IS heartbeat — refresh presence on every poll.
        await runner.TouchPresenceAsync();
        // The runner's project scopes claimable discovery so a poll does not
        // claim Pending runs from unrelated projects. May be null for a
        // project-less runner (claims across all projects).
        var info = await runner.GetInfoAsync();

        // An offline runner (info cleared on unregister) must not claim or be
        // re-dispatched new work — its presence is gone. Return an empty round
        // so a stale/offline poll is a harmless no-op.
        if (info is null)
            return new RunnerPollResponse([]);

        var dispatches = new List<WorkDispatch>();

        // Agent-jobs are push-based (the job grain owns its single work item;
        // no run to re-render from). Serve them before workflow repair/claim.
        // A pending agent-job work occupies a slot the runner already accepted.
        var agentJob = await runner.DequeueAssignedAgentJobAsync();
        if (agentJob is not null)
            dispatches.Add(agentJob);

        var reported = new HashSet<string>(
            (req.InFlight ?? []).Concat(req.AwaitingAck ?? []),
            StringComparer.Ordinal);

        // ② desired: Running runs assigned to me (work in flight I should be
        // holding). Load each to resolve its workId.
        var desiredRunIds = await _workflowRuns.FindRunningAssignedToAsync(runnerId, ct);
        var desiredKeys = new HashSet<string>(StringComparer.Ordinal);

        // ③ repair = desired − reported. A Running work the process does not
        // report was never delivered or was lost — re-dispatch it. Rendering
        // from the persisted run is a pure function; the work is already
        // Running so no claim is needed.
        foreach (var workflowRunId in desiredRunIds)
        {
            var (workKey, dispatch) = await RenderRunningWorkAsync(workflowRunId, runnerId, ct);
            if (workKey is null) continue;
            desiredKeys.Add(workKey);
            if (!reported.Contains(workKey) && dispatch is not null)
            {
                dispatches.Add(dispatch);
            }
        }

        // ④ spare = slots − |desired|. The capacity gate counts currently-
        // executing workflow works (|desired|), not held assignments.
        var spare = slots - desiredKeys.Count;
        if (spare <= 0)
            return new RunnerPollResponse(dispatches);

        // Serve already-assigned Ready runs first (ReadySince ASC for
        // round-robin fairness), then claim new Pending runs.
        foreach (var workflowRunId in await _workflowRuns.FindAssignedToAsync(runnerId, ct))
        {
            if (spare <= 0) break;
            var d = await ClaimAndRenderAsync(workflowRunId, runnerId, ct);
            if (d is null) continue;
            dispatches.Add(d);
            spare--;
        }

        if (spare <= 0)
            return new RunnerPollResponse(dispatches);

        // Claimable Pending runs: assign to me then claim. Optimistic —
        // concurrent runners may race a candidate; the arbiter admits one.
        // Scoped to the runner's project so a poll does not claim unrelated
        // Pending runs.
        foreach (var workflowRunId in await _workflowRuns.FindAssignableAsync(info?.ProjectId, ct: ct))
        {
            if (spare <= 0) break;
            var workflow = _grains.GetGrain<IWorkflowGrain>(workflowRunId);
            var assigned = await workflow.AssignRunnerAsync(runnerId);
            if (assigned.Status != WorkflowAssignmentStatus.Assigned) continue;

            var d = await ClaimAndRenderAsync(workflowRunId, runnerId, ct);
            if (d is null) continue;
            dispatches.Add(d);
            spare--;
        }

        return new RunnerPollResponse(dispatches);
    }

    /// <summary>
    /// Renders a repair dispatch for a Running work from the persisted run.
    /// The work is already claimed (Running), so no grain write is needed —
    /// the dispatch is a pure projection of the run's current in-flight work.
    /// Returns null workKey when the run has no resolvable in-flight work item
    /// (advanced between the store query and the load).
    /// </summary>
    private async Task<(string? WorkKey, WorkDispatch? Dispatch)> RenderRunningWorkAsync(
        string workflowRunId, string runnerId, CancellationToken ct)
    {
        var run = await _workflowRuns.LoadAsync(workflowRunId, ct);
        if (run is null) return (null, null);

        var workId = run.CurrentStage().RunningTask?.WorkId ?? run.CurrentStage().RunningTask?.Id;
        WorkItem? item = null;
        if (workId is not null)
        {
            var task = run.CurrentStage().Tasks.FirstOrDefault(t =>
                t.Status == TaskRunStatus.Running
                && string.Equals(t.WorkId ?? t.Id, workId, StringComparison.Ordinal));
            if (task is not null)
            {
                item = WorkItem.Task(
                    run.CurrentStage().Id, task.WorkId ?? task.Id, task.Title, task.Uses,
                    task.WithInput, task.Artifacts, task.SetVars, task.Recovery);
            }
        }
        else
        {
            var checksWorkId = run.CurrentStage().ChecksWorkId;
            if (!string.IsNullOrWhiteSpace(checksWorkId))
            {
                workId = checksWorkId;
                var pendingChecks = run.CurrentStage().Checks
                    .Where(c => c.Status is StageCheckStatus.Pending or StageCheckStatus.Running)
                    .Select(c => new CheckItem(c.Name, c.Title, c.Uses, c.WithInput))
                    .ToList();
                item = WorkItem.Checks(run.CurrentStage().Id, checksWorkId, pendingChecks);
            }
        }

        if (item is null || workId is null) return (null, null);

        var workKey = $"{WorkDispatchOwnerKinds.Workflow}:{workflowRunId}:{workId}";
        try
        {
            var dispatch = await _translator.TranslateToDispatchAsync(item, workflowRunId, run, runnerId);
            return (workKey, BackfillIssue(dispatch, run));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "DispatchService failed to render repair dispatch for workflow {WorkflowId} work {WorkId}",
                workflowRunId, workId);
            // Render failed after claim — the work is Running and unreported,
            // so the next poll retries the render (spec §Recovery).
            return (workKey, null);
        }
    }

    /// <summary>
    /// Claims a Ready run's next work (single atomic grain write) and renders
    /// the dispatch. Returns null when the claim fails (stage lock contended,
    /// run advanced, no dispatchable work) — the run is retried on later polls.
    /// </summary>
    private async Task<WorkDispatch?> ClaimAndRenderAsync(
        string workflowRunId, string runnerId, CancellationToken ct)
    {
        var workflow = _grains.GetGrain<IWorkflowGrain>(workflowRunId);
        var item = await workflow.ClaimNextAsync(runnerId);
        if (item is null) return null;

        var run = await _workflowRuns.LoadAsync(workflowRunId, ct);
        if (run is null) return null;

        try
        {
            var dispatch = await _translator.TranslateToDispatchAsync(item, workflowRunId, run, runnerId);
            return BackfillIssue(dispatch, run);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "DispatchService failed to render dispatch for workflow {WorkflowId} after claim",
                workflowRunId);
            // Claimed but render failed — work is Running and unreported, so
            // the next poll re-dispatches it via the repair path.
            return null;
        }
    }

    /// <summary>
    /// Backfills the issue ref from run annotations when the translator did
    /// not populate it (issue metadata lives on the run, not the work item).
    /// </summary>
    private static WorkDispatch BackfillIssue(WorkDispatch dispatch, WorkflowRun run)
    {
        if (dispatch.Issue is not null) return dispatch;
        if (run.Metadata?.Annotations is not { } annotations) return dispatch;
        if (!annotations.TryGetValue("projectId", out var projectId)
            || !annotations.TryGetValue("issueId", out var issueId)
            || !annotations.TryGetValue("issueNumber", out var numberStr)
            || !int.TryParse(numberStr, out var number))
            return dispatch;
        return dispatch with { Issue = new WorkIssueRef(projectId, issueId, number) };
    }
}
