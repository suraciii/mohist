using System.Text.Json;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Orleans;

namespace Mohist.Server.Runner.Services;

public sealed class DispatchService : IScopedService
{
    private readonly IGrainFactory _grains;
    private readonly WorkflowRunQuerier _workflowRuns;
    private readonly IAgentJobStore _agentJobs;
    private readonly IDispatchSnapshotStore _dispatchSnapshots;
    private readonly WorkflowItemTranslator _translator;
    private readonly ILogger<DispatchService> _log;
    private readonly IDispatchPollObserver _pollObserver;

    public DispatchService(
        IGrainFactory grains,
        WorkflowRunQuerier workflowRuns,
        IAgentJobStore agentJobs,
        IDispatchSnapshotStore dispatchSnapshots,
        WorkflowItemTranslator translator,
        ILogger<DispatchService> log,
        IDispatchPollObserver? pollObserver = null)
    {
        _grains = grains;
        _workflowRuns = workflowRuns;
        _agentJobs = agentJobs;
        _dispatchSnapshots = dispatchSnapshots;
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
        await runner.TouchPresenceAsync();
        var info = await runner.GetInfoAsync();
        if (info is null)
            return new RunnerPollResponse([]);

        await _pollObserver.AfterRunnerInfoAsync(runnerId);

        var readiness = await runner.ObserveRuntimeReadinessAsync(
            req.ConnectionGeneration,
            req.RuntimeReadiness ?? []);

        var dispatches = new List<WorkDispatch>();
        var reportedWorkKeys = ReportedWorkKeys(req);
        var activeWorkKeys = await AddMissingRedeliveriesAsync(
            runnerId,
            reportedWorkKeys,
            dispatches,
            ct);
        // Unresolved Agent work is deliberately absent from desired
        // redelivery, but a connected Runner still reports its execution as
        // occupying a slot until it retires the key itself.
        activeWorkKeys.UnionWith(reportedWorkKeys);
        var spare = slots - activeWorkKeys.Count;
        if (spare <= 0)
            return new RunnerPollResponse(dispatches);

        // A runner with unhealthy durable admission state can still reconcile
        // held work, but it must not receive fresh claims until its local
        // journals and terminal delivery are writable again.
        if (req.AdmissionReady is false)
            return new RunnerPollResponse(dispatches);

        spare = await AddPendingDispatchesAsync(
            runner,
            info.ProjectId,
            runnerId,
            assigned: true,
            spare,
            dispatches,
            readiness,
            ct);
        if (spare > 0)
        {
            await AddPendingDispatchesAsync(
                runner,
                info.ProjectId,
                runnerId,
                assigned: false,
                spare,
                dispatches,
                readiness,
                ct);
        }

        return new RunnerPollResponse(dispatches);
    }

    private static HashSet<string> ReportedWorkKeys(RunnerPollRequest req) =>
        new((req.InFlight ?? []).Concat(req.AwaitingAck ?? []), StringComparer.Ordinal);

    private async Task<HashSet<string>> AddMissingRedeliveriesAsync(
        string runnerId,
        IReadOnlySet<string> reportedWorkKeys,
        List<WorkDispatch> dispatches,
        CancellationToken ct)
    {
        var activeWorkKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var workflowRunId in await _workflowRuns.FindRunningAssignedToAsync(runnerId, ct))
        {
            var (workKey, dispatch) = await RenderActiveWorkflowAsync(
                workflowRunId,
                runnerId,
                reportedWorkKeys,
                ct);
            if (workKey is null)
                continue;

            activeWorkKeys.Add(workKey);
            if (dispatch is not null)
                dispatches.Add(dispatch);
        }

        foreach (var record in await _agentJobs.ListRunningForRunnerAsync(runnerId, ct))
        {
            var workKey = AgentJobWorkKey(record.JobKey, record.WorkId);
            activeWorkKeys.Add(workKey);
            if (reportedWorkKeys.Contains(workKey))
                continue;

            var dispatch = DeserializeAgentDispatch(record);
            if (dispatch is not null)
                dispatches.Add(dispatch);
        }

        return activeWorkKeys;
    }

    private async Task<int> AddPendingDispatchesAsync(
        IRunnerGrain runner,
        string? projectId,
        string runnerId,
        bool assigned,
        int availableSlots,
        List<WorkDispatch> dispatches,
        RunnerRuntimeReadinessSnapshot readiness,
        CancellationToken ct)
    {
        var candidateLimit = Math.Max(availableSlots, 20);
        var workflowCandidates = assigned
            ? (await _workflowRuns.FindAssignedCandidatesAsync(runnerId, candidateLimit, ct))
                .Select(candidate => new PendingCandidate(
                    candidate.ReadySince,
                    WorkDispatchOwnerKinds.Workflow,
                    candidate.WorkflowRunId))
            : (await _workflowRuns.FindAssignableCandidatesAsync(projectId, candidateLimit, ct))
                .Select(candidate => new PendingCandidate(
                    candidate.ReadySince,
                    WorkDispatchOwnerKinds.Workflow,
                    candidate.WorkflowRunId));

        var agentCandidates = assigned
            ? (await _agentJobs.ListAssignedPendingForRunnerAsync(runnerId, candidateLimit, ct))
                .Select(record => new PendingCandidate(
                    record.ReadySince ?? DateTimeOffset.MinValue,
                    WorkDispatchOwnerKinds.AgentJob,
                    record.JobKey))
            : (await _agentJobs.ListEligiblePendingAsync(projectId, candidateLimit, ct))
                .Select(record => new PendingCandidate(
                    record.ReadySince ?? DateTimeOffset.MinValue,
                    WorkDispatchOwnerKinds.AgentJob,
                    record.JobKey));

        var candidates = workflowCandidates
            .Concat(agentCandidates)
            .OrderBy(candidate => candidate.ReadySince)
            .ThenBy(candidate => candidate.OwnerKind, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.OwnerId, StringComparer.Ordinal);

        var remainingSlots = availableSlots;
        foreach (var candidate in candidates)
        {
            if (remainingSlots <= 0)
                break;

            WorkDispatch? dispatch;
            if (candidate.OwnerKind == WorkDispatchOwnerKinds.AgentJob)
            {
                var record = await _agentJobs.LoadLedgerAsync(candidate.OwnerId, ct);
                var requiredRuntimes = record is null
                    ? null
                    : RuntimeRequirementsFromDispatch(DeserializeAgentDispatch(record));
                if (!readiness.Allows(requiredRuntimes))
                    continue;

                var claim = await runner.TryClaimAgentJobAsync(candidate.OwnerId, projectId);
                dispatch = claim?.Dispatch;
            }
            else
            {
                var requiredRuntimes = await ResolveWorkflowRuntimeRequirementsAsync(candidate.OwnerId, ct);
                if (!readiness.Allows(requiredRuntimes))
                    continue;

                dispatch = await ClaimAndRenderWorkflowAsync(
                    runner,
                    candidate.OwnerId,
                    projectId,
                    runnerId,
                    assignWorker: !assigned,
                    ct);
            }

            if (dispatch is null)
                continue;

            dispatches.Add(dispatch);
            remainingSlots--;
        }

        return remainingSlots;
    }

    private async Task<(string? WorkKey, WorkDispatch? Dispatch)> RenderActiveWorkflowAsync(
        string workflowRunId,
        string runnerId,
        IReadOnlySet<string> reportedWorkKeys,
        CancellationToken ct)
    {
        var run = await _workflowRuns.LoadAsync(workflowRunId, ct);
        if (run is null)
            return (WorkflowOwnerKey(workflowRunId), null);
        if (run.HasUnresolvedAgentResult())
            return (null, null);

        var activeWork = run.CurrentActiveWorkFor(runnerId);
        if (activeWork is null)
            return (WorkflowOwnerKey(workflowRunId), null);

        var workId = activeWork.WorkId;
        var workKey = WorkflowWorkKey(workflowRunId, workId);
        if (activeWork.IsTask
            && activeWork.TaskRunId is { } taskRunId
            && run.Stages
                .SelectMany(stage => stage.Tasks)
                .Any(task => string.Equals(task.Id, taskRunId, StringComparison.Ordinal)
                    && task.AgentInvocation is not null))
        {
            // The linkage is the durable ownership handoff. Once present,
            // redelivery must not recreate a Workflow dispatch or count the
            // task beside its AgentJob.
            return (null, null);
        }
        if (reportedWorkKeys.Contains(workKey))
            return (workKey, null);

        try
        {
            if (activeWork.IsTask)
            {
                var storedJson = await _dispatchSnapshots.LoadJsonAsync(workflowRunId, workId, ct);
                if (storedJson is not null)
                    return (workKey, JSON.Deserialize<WorkDispatch>(storedJson)!);
            }

            var translation = await _translator.TranslateToDispatchAsync(activeWork.Item, workflowRunId, run, runnerId);
            if (translation is WorkflowItemTranslationResult.Delegated delegated)
            {
                await BindDelegatedInvocationAsync(workflowRunId, delegated.Invocation);
                // A delegated task has no Workflow-owned runner dispatch. Its
                // AgentJob ledger is the sole active-work owner and will be
                // claimed by the AgentJob poll path.
                return (null, null);
            }

            var concrete = WithIssueFromRun(
                ((WorkflowItemTranslationResult.Dispatch)translation).Value,
                run);
            if (activeWork.IsChecks)
                return (workKey, concrete);

            var saved = await StoreDispatchAsync(workflowRunId, runnerId, workId, concrete);
            return (workKey, saved);
        }
        catch (WorkflowDispatchRejectedException ex)
        {
            await RejectWorkflowDispatchAsync(workflowRunId, runnerId, workId, ex);
            return (null, null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "failed to render redelivery dispatch for run {run} work {work}",
                workflowRunId,
                workId);
            return (workKey, null);
        }
    }

    private async Task<WorkDispatch?> ClaimAndRenderWorkflowAsync(
        IRunnerGrain runner,
        string workflowRunId,
        string? projectId,
        string runnerId,
        bool assignWorker,
        CancellationToken ct)
    {
        WorkItem? item;
        try
        {
            item = await runner.TryClaimWorkflowAsync(workflowRunId, projectId, assignWorker);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex,
                "skipped dispatch claim for run {run}",
                workflowRunId);
            return null;
        }

        if (item is null)
            return null;

        var run = await _workflowRuns.LoadAsync(workflowRunId, ct);
        if (run is null)
            return null;

        try
        {
            var translation = await _translator.TranslateToDispatchAsync(item, workflowRunId, run, runnerId);
            if (translation is WorkflowItemTranslationResult.Delegated delegated)
            {
                await BindDelegatedInvocationAsync(workflowRunId, delegated.Invocation);
                // The handoff has already admitted the AgentJob. Returning
                // null keeps this poll free of a duplicate Workflow dispatch;
                // the next poll claims the durable AgentJob ledger entry.
                return null;
            }

            var concrete = WithIssueFromRun(
                ((WorkflowItemTranslationResult.Dispatch)translation).Value,
                run);
            if (item.IsChecks)
                return concrete;

            return await StoreDispatchAsync(workflowRunId, runnerId, item.Id!, concrete);
        }
        catch (WorkflowDispatchRejectedException ex)
        {
            await RejectWorkflowDispatchAsync(workflowRunId, runnerId, item.Id!, ex);
            return null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "failed to render dispatch for run {run} after claim",
                workflowRunId);
            return null;
        }
    }

    private static WorkDispatch? DeserializeAgentDispatch(AgentJobLedgerRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.DispatchJson))
            return null;

        try
        {
            return JsonSerializer.Deserialize<WorkDispatch>(record.DispatchJson, JSON.Options);
        }
        catch
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<string>?> ResolveWorkflowRuntimeRequirementsAsync(
        string workflowRunId,
        CancellationToken ct)
    {
        var run = await _workflowRuns.LoadAsync(workflowRunId, ct);
        var next = run?.NextWork();
        if (run is null || next is null)
            return null;

        var item = next switch
        {
            WorkflowTaskWork task => WorkItem.Task(
                task.Stage,
                task.Id,
                task.Title,
                task.Uses,
                task.With,
                task.Artifacts,
                task.SetVars,
                task.Recovery,
                task.RecoveryRemaining),
            WorkflowChecksWork checks => WorkItem.Checks(
                checks.Stage,
                WorkflowRunExtensions.ChecksWorkIdFor(checks.Stage),
                checks.Items),
            _ => null,
        };
        return item is null ? null : await _translator.ResolveRequiredRuntimesAsync(item, run);
    }

    private static IReadOnlyList<string>? RuntimeRequirementsFromDispatch(WorkDispatch? dispatch)
    {
        if (dispatch is null)
            return null;
        if (dispatch.AgentDefinition?.Runtime is { } runtime && !string.IsNullOrWhiteSpace(runtime))
            return [runtime.Trim()];

        return dispatch.Uses switch
        {
            "mohist/pi" => ["pi"],
            "mohist/opencode" => ["opencode"],
            _ => [],
        };
    }

    private async Task<WorkDispatch?> StoreDispatchAsync(
        string workflowRunId,
        string runnerId,
        string workId,
        WorkDispatch dispatch) =>
        await _grains.GetGrain<IWorkflowGrain>(workflowRunId)
            .StoreActiveWorkDispatchAsync(runnerId, workId, dispatch);

    private async Task BindDelegatedInvocationAsync(
        string workflowRunId,
        WorkflowAgentInvocation invocation)
    {
        var link = new AgentInvocationLink(
            InvocationId: invocation.InvocationId,
            TaskRunId: invocation.TaskRunId,
            WorkId: invocation.CommandId,
            JobId: invocation.JobKey,
            SessionId: invocation.SessionId,
            InputId: invocation.InputId,
            TurnId: invocation.TurnId);
        var ack = await _grains.GetGrain<IWorkflowGrain>(workflowRunId)
            .BindAgentInvocationAsync(link);
        if (ack != ReportAck.Stale)
            return;

        // The handoff client normally binds before activation. Keep this
        // idempotent fallback for replay or an alternate dispatch client; if
        // the run stopped before either binding write, cancel the activated
        // job so it cannot remain ownerless.
        try
        {
            await _grains.GetGrain<IAgentJobGrain>(invocation.JobKey).CancelAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "could not cancel stale delegated AgentJob {JobId} for workflow {WorkflowRunId}",
                invocation.JobKey,
                workflowRunId);
        }
    }

    private async Task RejectWorkflowDispatchAsync(
        string workflowRunId,
        string runnerId,
        string workId,
        WorkflowDispatchRejectedException exception)
    {
        _log.LogWarning(exception,
            "rejected dispatch for run {run} work {work}: {code} {reason}",
            workflowRunId,
            workId,
            exception.Error.Code,
            exception.Error.Message);
        await _grains.GetGrain<IWorkflowGrain>(workflowRunId)
            .RejectActiveWorkDispatchAsync(runnerId, workId, exception.Error);
    }

    private static string WorkflowWorkKey(string workflowRunId, string workId) =>
        $"{WorkDispatchOwnerKinds.Workflow}:{workflowRunId}:{workId}";

    private static string WorkflowOwnerKey(string workflowRunId) =>
        $"{WorkDispatchOwnerKinds.Workflow}:{workflowRunId}";

    private static string AgentJobWorkKey(string agentJobId, string? workId) =>
        $"{WorkDispatchOwnerKinds.AgentJob}:{agentJobId}:{workId}";

    private static WorkDispatch WithIssueFromRun(WorkDispatch dispatch, WorkflowRun run)
    {
        if (dispatch.Issue is not null)
            return dispatch;
        if (string.IsNullOrWhiteSpace(run.Metadata.ProjectId)
            || run.Metadata.IssueNumber is not > 0)
            return dispatch;
        return dispatch with { Issue = new WorkIssueRef(run.Metadata.ProjectId, run.Metadata.IssueNumber.Value) };
    }

    private sealed record PendingCandidate(
        DateTimeOffset ReadySince,
        string OwnerKind,
        string OwnerId);
}
