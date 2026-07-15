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
/// Poll-based workflow and Agent-job reconciler. Workflow runs remain their
/// dispatch ledger; Agent jobs retain a stable snapshot in the Runner grain.
/// </summary>
public sealed class DispatchService : IScopedService
{
    private readonly IGrainFactory _grains;
    private readonly WorkflowRunQuerier _workflowRuns;
    private readonly WorkflowItemTranslator _translator;
    private readonly ILogger<DispatchService> _log;
    private readonly IDispatchPollObserver _pollObserver;

    public DispatchService(
        IGrainFactory grains,
        WorkflowRunQuerier workflowRuns,
        WorkflowItemTranslator translator,
        ILogger<DispatchService> log,
        IDispatchPollObserver? pollObserver = null)
    {
        _grains = grains;
        _workflowRuns = workflowRuns;
        _translator = translator;
        _log = log;
        _pollObserver = pollObserver ?? NoopDispatchPollObserver.Instance;
    }

    public async Task<RunnerPollResponse> PollAsync(
        string runnerId,
        RunnerPollRequest req,
        CancellationToken ct = default)
    {
        var runner = _grains.GetGrain<IRunnerGrain>(runnerId);
        var admission = await runner.TryBeginPollAsync();
        if (!admission.Admitted)
            return new RunnerPollResponse([]);

        try
        {
            return await PollCoreAsync(runner, runnerId, req, admission.Slots, ct);
        }
        finally
        {
            await runner.EndPollAsync();
        }
    }

    private async Task<RunnerPollResponse> PollCoreAsync(
        IRunnerGrain runner,
        string runnerId,
        RunnerPollRequest req,
        int slots,
        CancellationToken ct)
    {
        var workerId = runnerId;
        await runner.TouchPresenceAsync();
        var info = await runner.GetInfoAsync();

        if (info is null)
            return new RunnerPollResponse([]);

        await _pollObserver.AfterRunnerInfoAsync(runnerId);

        var dispatches = new List<WorkDispatch>();
        var reportedWorkKeys = ReportedWorkKeys(req);
        var agentJobs = await runner.ReconcileAgentJobsAsync(reportedWorkKeys.ToList());
        if (agentJobs.Dispatch is not null)
            dispatches.Add(agentJobs.Dispatch);

        var activeWorkKeys = await AddMissingRedeliveriesAsync(runnerId, workerId, reportedWorkKeys, dispatches, ct);
        var spare = slots - activeWorkKeys.Count - agentJobs.ActiveCount;
        if (spare <= 0)
            return new RunnerPollResponse(dispatches);

        spare = await AddAssignedReadyDispatchesAsync(runner, info.ProjectId, runnerId, workerId, spare, dispatches, ct);
        if (spare <= 0)
            return new RunnerPollResponse(dispatches);

        await ActivateBoundGatedStartsAsync(info.ProjectId, ct);
        await AddAssignablePendingDispatchesAsync(runner, info.ProjectId, runnerId, workerId, spare, dispatches, ct);

        return new RunnerPollResponse(dispatches);
    }

    private async Task ActivateBoundGatedStartsAsync(string? projectId, CancellationToken ct)
    {
        foreach (var workflowRunId in await _workflowRuns.FindGatedStartsAsync(projectId, ct: ct))
        {
            try
            {
                var workflow = _grains.GetGrain<IWorkflowGrain>(workflowRunId);
                await workflow.ActivateAsync();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "DispatchService failed to activate bound workflow start {WorkflowRunId}", workflowRunId);
            }
        }
    }

    private static HashSet<string> ReportedWorkKeys(RunnerPollRequest req) =>
        new((req.InFlight ?? []).Concat(req.AwaitingAck ?? []), StringComparer.Ordinal);

    private async Task<HashSet<string>> AddMissingRedeliveriesAsync(
        string runnerId,
        string workerId,
        IReadOnlySet<string> reportedWorkKeys,
        List<WorkDispatch> dispatches,
        CancellationToken ct)
    {
        var activeWorkKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var workflowRunId in await _workflowRuns.FindRunningAssignedToAsync(workerId, ct))
        {
            var (workKey, dispatch) = await RenderActiveWorkAsync(workflowRunId, runnerId, workerId, ct);
            if (workKey is null) continue;
            activeWorkKeys.Add(workKey);
            if (!reportedWorkKeys.Contains(workKey) && dispatch is not null)
                dispatches.Add(dispatch);
        }

        return activeWorkKeys;
    }

    private async Task<int> AddAssignedReadyDispatchesAsync(
        IRunnerGrain runner,
        string? projectId,
        string runnerId,
        string workerId,
        int availableSlots,
        List<WorkDispatch> dispatches,
        CancellationToken ct)
    {
        var remainingSlots = availableSlots;

        foreach (var workflowRunId in await _workflowRuns.FindAssignedToAsync(workerId, ct))
        {
            if (remainingSlots <= 0) break;
            var dispatch = await ClaimAndRenderAsync(
                runner, workflowRunId, projectId, runnerId, workerId, assignWorker: false, ct);
            if (dispatch is null) continue;
            dispatches.Add(dispatch);
            remainingSlots--;
        }

        return remainingSlots;
    }

    private async Task<int> AddAssignablePendingDispatchesAsync(
        IRunnerGrain runner,
        string? projectId,
        string runnerId,
        string workerId,
        int availableSlots,
        List<WorkDispatch> dispatches,
        CancellationToken ct)
    {
        var remainingSlots = availableSlots;

        foreach (var workflowRunId in await _workflowRuns.FindAssignableAsync(projectId, ct: ct))
        {
            if (remainingSlots <= 0) break;
            var dispatch = await ClaimAndRenderAsync(
                runner, workflowRunId, projectId, runnerId, workerId, assignWorker: true, ct);
            if (dispatch is null) continue;
            dispatches.Add(dispatch);
            remainingSlots--;
        }

        return remainingSlots;
    }

    private async Task<(string? WorkKey, WorkDispatch? Dispatch)> RenderActiveWorkAsync(
        string workflowRunId, string runnerId, string workerId, CancellationToken ct)
    {
        var run = await _workflowRuns.LoadAsync(workflowRunId, ct);
        if (run is null) return (null, null);

        var activeWork = run.CurrentActiveWorkFor(workerId);
        if (activeWork is null) return (null, null);

        var workId = activeWork.WorkId;
        var workKey = $"{WorkDispatchOwnerKinds.Workflow}:{workflowRunId}:{workId}";
        try
        {
            var dispatch = await _translator.TranslateToDispatchAsync(activeWork.Item, workflowRunId, run, runnerId);
            return (workKey, WithIssueFromRunAnnotations(dispatch, run));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "DispatchService failed to render redelivery dispatch for workflow {WorkflowId} work {WorkId}",
                workflowRunId, workId);
            // Render failed after claim — the work is Running and unreported,
            // so the next poll retries the render (spec §Poll Reconciliation).
            return (workKey, null);
        }
    }

    private async Task<WorkDispatch?> ClaimAndRenderAsync(
        IRunnerGrain runner,
        string workflowRunId,
        string? projectId,
        string runnerId,
        string workerId,
        bool assignWorker,
        CancellationToken ct)
    {
        var item = await runner.TryClaimWorkflowAsync(workflowRunId, projectId, assignWorker);
        if (item is null) return null;

        var run = await _workflowRuns.LoadAsync(workflowRunId, ct);
        if (run is null) return null;

        try
        {
            var dispatch = await _translator.TranslateToDispatchAsync(item, workflowRunId, run, runnerId);
            return WithIssueFromRunAnnotations(dispatch, run);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "DispatchService failed to render dispatch for workflow {WorkflowId} after claim",
                workflowRunId);
            // Claimed but render failed — work is Running and unreported, so
            // the next poll redelivers it via poll reconciliation.
            return null;
        }
    }

    private static WorkDispatch WithIssueFromRunAnnotations(WorkDispatch dispatch, WorkflowRun run)
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
