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
        ct.ThrowIfCancellationRequested();
        var runner = _grains.GetGrain<IRunnerGrain>(runnerId);
        var admission = await runner.TryBeginPollAsync();
        if (!admission.Admitted)
            return new RunnerPollResponse([]);

        try
        {
            return await PollCoreAsync(runner, runnerId, req, admission.Slots, ct).WaitAsync(ct);
        }
        finally
        {
            await runner.EndPollAsync(admission.AdmissionToken);
        }
    }

    private async Task<RunnerPollResponse> PollCoreAsync(
        IRunnerGrain runner,
        string runnerId,
        RunnerPollRequest req,
        int slots,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await runner.TouchPresenceAsync();
        var info = await runner.GetInfoAsync();
        if (info is null)
            return new RunnerPollResponse([]);

        await _pollObserver.AfterRunnerInfoAsync(runnerId).WaitAsync(ct);
        ct.ThrowIfCancellationRequested();

        var readiness = await runner.ObserveRuntimeReadinessAsync(
            req.ConnectionGeneration,
            req.RuntimeReadiness ?? []);

        var dispatches = new List<WorkDispatch>();
        var reportedWorkKeys = ReportedWorkKeys(req);
        ct.ThrowIfCancellationRequested();
        var desiredActiveKeys = await AddMissingRedeliveriesAsync(
            runnerId,
            reportedWorkKeys,
            dispatches,
            ct);
        // Only reported keys whose underlying run is still in the desired
        // active set reserve a slot. Stale keys (e.g. for a blocked run
        // the deadline already released) are filtered out so a different
        // eligible work item can claim the released capacity.
        var activeWorkKeys = new HashSet<string>(desiredActiveKeys, StringComparer.Ordinal);
        foreach (var reportedKey in reportedWorkKeys)
        {
            if (desiredActiveKeys.Contains(reportedKey))
                activeWorkKeys.Add(reportedKey);
        }
        var spare = slots - activeWorkKeys.Count;
        if (spare <= 0)
            return new RunnerPollResponse(dispatches);

        // A runner with unhealthy durable admission state can still reconcile
        // held work, but it must not receive fresh claims until its local
        // journals and terminal delivery are writable again.
        if (req.AdmissionReady is false)
            return new RunnerPollResponse(dispatches);

        ct.ThrowIfCancellationRequested();
        spare = await AddPendingDispatchesAsync(
            runner,
            info,
            info.ProjectId,
            runnerId,
            assigned: true,
            spare,
            dispatches,
            readiness,
            ct);
        if (spare > 0)
        {
            ct.ThrowIfCancellationRequested();
            await AddPendingDispatchesAsync(
                runner,
                info,
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
            ct.ThrowIfCancellationRequested();
            var (workKey, dispatch, reserveSlot) = await RenderActiveWorkflowAsync(
                workflowRunId,
                runnerId,
                reportedWorkKeys,
                ct);
            if (workKey is null)
                continue;

            // A recovery render is a reconciliation probe, not an execution:
            // it occupies a slot only while the runner reports holding it.
            if (reserveSlot)
                activeWorkKeys.Add(workKey);
            if (dispatch is not null)
                dispatches.Add(dispatch);
        }

        var agentWork = (await _agentJobs.ListRunningForRunnerAsync(runnerId, ct))
            .Concat(await _agentJobs.ListRecoveringForRunnerAsync(runnerId, ct));
        foreach (var record in agentWork)
        {
            ct.ThrowIfCancellationRequested();
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
        RunnerInfo info,
        string? projectId,
        string runnerId,
        bool assigned,
        int availableSlots,
        List<WorkDispatch> dispatches,
        RunnerRuntimeReadinessSnapshot readiness,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
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
            ct.ThrowIfCancellationRequested();
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

                var pendingDispatch = record is null ? null : DeserializeAgentDispatch(record);
                var expectation = pendingDispatch is null
                    ? null
                    : BuildAgentJobCapabilityExpectation(info, readiness, pendingDispatch);
                if (pendingDispatch?.AgentDefinition?.ReasoningEffort is not null && expectation is null)
                    continue;

                ct.ThrowIfCancellationRequested();
                var claim = await runner.TryClaimAgentJobAsync(candidate.OwnerId, projectId, expectation);
                dispatch = claim?.Dispatch;
            }
            else
            {
                var requiredRuntimes = await ResolveWorkflowRuntimeRequirementsAsync(candidate.OwnerId, ct);
                if (!readiness.Allows(requiredRuntimes))
                    continue;

                ct.ThrowIfCancellationRequested();
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

    private static CapabilityClaimExpectation? BuildAgentJobCapabilityExpectation(
        RunnerInfo info,
        RunnerRuntimeReadinessSnapshot readiness,
        WorkDispatch dispatch)
    {
        var definition = dispatch.AgentDefinition;
        if (definition is null || string.IsNullOrWhiteSpace(dispatch.AgentJobId))
            return null;

        var catalog = FindRuntimeCatalog(info, definition.Runtime);
        if (catalog is null)
            return null;

        var witness = readiness.Witnesses.FirstOrDefault(candidate =>
            string.Equals(candidate.Runtime, definition.Runtime, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(definition.ReasoningEffort)
            && (catalog.SupportsReasoningEffort != true
                || catalog.Complete != true
                || string.IsNullOrWhiteSpace(catalog.CapabilityRevision)
                || witness?.Ready != true
                || witness.Generation is not > 0))
            return null;

        return new CapabilityClaimExpectation(
            WorkDispatchOwnerKinds.AgentJob,
            dispatch.AgentJobId,
            dispatch.WorkId,
            definition.Runtime,
            definition.Model,
            definition.ReasoningEffort,
            definition.Variant,
            catalog.CapabilityRevision,
            witness?.Generation,
            info.ConnectionGeneration);
    }

    private static RuntimeCatalogEntry? FindRuntimeCatalog(RunnerInfo info, string runtime)
    {
        if (info.RuntimeCatalogs is null)
            return null;

        foreach (var entry in info.RuntimeCatalogs)
        {
            if (string.Equals(entry.Key, runtime, StringComparison.OrdinalIgnoreCase))
                return entry.Value;
        }

        return null;
    }

    private async Task<(string? WorkKey, WorkDispatch? Dispatch, bool ReserveSlot)> RenderActiveWorkflowAsync(
        string workflowRunId,
        string runnerId,
        IReadOnlySet<string> reportedWorkKeys,
        CancellationToken ct)
    {
        var run = await _workflowRuns.LoadAsync(workflowRunId, ct);
        if (run is null)
            return (WorkflowOwnerKey(workflowRunId), null, ReserveSlot: true);
        if (run.HasUnresolvedAgentResult())
            return await RenderUnresolvedAgentRecoveryAsync(run, workflowRunId, runnerId, reportedWorkKeys, ct);

        var activeWork = run.CurrentActiveWorkFor(runnerId);
        if (activeWork is null)
            return (WorkflowOwnerKey(workflowRunId), null, ReserveSlot: true);

        var workId = activeWork.WorkId;
        var workKey = WorkflowWorkKey(workflowRunId, workId);
        if (reportedWorkKeys.Contains(workKey))
            return (workKey, null, ReserveSlot: true);

        try
        {
            if (activeWork.IsTask)
            {
                var storedJson = await _dispatchSnapshots.LoadJsonAsync(workflowRunId, workId, ct);
                if (storedJson is not null)
                    return (workKey, JSON.Deserialize<WorkDispatch>(storedJson)!, ReserveSlot: true);
            }

            var dispatch = await _translator.TranslateToDispatchAsync(activeWork.Item, workflowRunId, run, runnerId);
            var concrete = WithIssueFromRun(dispatch, run);
            if (activeWork.IsChecks)
                return (workKey, concrete, ReserveSlot: true);

            var saved = await StoreDispatchAsync(workflowRunId, runnerId, workId, concrete);
            return (workKey, saved, ReserveSlot: true);
        }
        catch (WorkflowDispatchRejectedException ex)
        {
            ct.ThrowIfCancellationRequested();
            await RejectWorkflowDispatchAsync(workflowRunId, runnerId, workId, ex);
            return (null, null, ReserveSlot: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "failed to render redelivery dispatch for run {run} work {work}",
                workflowRunId,
                workId);
            return (workKey, null, ReserveSlot: true);
        }
    }

    /// <summary>
    /// Unresolved agent work is re-delivered only to the runner identity
    /// recorded on the attempt and only while a full runtime binding lets
    /// the runner reconcile (adopt or surface unknown) instead of
    /// re-executing. The dispatch is re-rendered from persisted facts —
    /// settlement reconciliation deletes dispatch snapshots — and is never
    /// stored: snapshot storage is closed for unresolved runs.
    /// </summary>
    private async Task<(string? WorkKey, WorkDispatch? Dispatch, bool ReserveSlot)> RenderUnresolvedAgentRecoveryAsync(
        WorkflowRun run,
        string workflowRunId,
        string runnerId,
        IReadOnlySet<string> reportedWorkKeys,
        CancellationToken ct)
    {
        var settlementTask = run.FindUnresolvedAgentResultSettlementTask();
        if (settlementTask is null)
            return (null, null, ReserveSlot: false);

        // A blocked settlement has already crossed the durable release
        // boundary; nothing should be redelivered or recovered for it.
        var settlement = settlementTask.Task.AgentResultSettlement!;
        // Update-interrupted work waits for a recovery receipt and a fresh
        // replacement dispatch; it never reconciles through redelivery.
        if (settlement.State != AgentResultSettlementState.Unknown
            || !string.IsNullOrWhiteSpace(settlement.UpdateOperationId))
            return (null, null, ReserveSlot: false);
        var binding = settlement.Runtime is not null && settlement.RuntimeSessionId is not null
            ? new AgentRecoveryBinding(settlement.Runtime, settlement.RuntimeSessionId)
            : null;
        var activeWork = run.CurrentActiveWorkFor(runnerId);
        if (binding is null
            || activeWork is not { IsTask: true }
            || !string.Equals(activeWork.TaskRunId, settlementTask.Task.Id, StringComparison.Ordinal)
            || !string.Equals(settlement.RunnerId, runnerId, StringComparison.Ordinal))
        {
            return (null, null, ReserveSlot: false);
        }

        var workKey = WorkflowWorkKey(workflowRunId, activeWork.WorkId);
        if (reportedWorkKeys.Contains(workKey))
            return (workKey, null, ReserveSlot: false);

        try
        {
            ct.ThrowIfCancellationRequested();
            var dispatch = await _translator.TranslateToDispatchAsync(activeWork.Item, workflowRunId, run, runnerId);
            var recovery = WithIssueFromRun(dispatch, run) with { AgentRecovery = binding };
            return (workKey, recovery, ReserveSlot: false);
        }
        catch (WorkflowDispatchRejectedException ex)
        {
            ct.ThrowIfCancellationRequested();
            await RejectWorkflowDispatchAsync(workflowRunId, runnerId, activeWork.WorkId, ex);
            return (null, null, ReserveSlot: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "failed to render agent recovery dispatch for run {run} work {work}",
                workflowRunId,
                activeWork.WorkId);
            return (workKey, null, ReserveSlot: false);
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
        ct.ThrowIfCancellationRequested();
        try
        {
            item = await runner.TryClaimWorkflowAsync(workflowRunId, projectId, assignWorker);
        }
        catch (OperationCanceledException)
        {
            throw;
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

        ct.ThrowIfCancellationRequested();
        var run = await _workflowRuns.LoadAsync(workflowRunId, ct);
        if (run is null)
            return null;

        try
        {
            var dispatch = await _translator.TranslateToDispatchAsync(item, workflowRunId, run, runnerId);
            var concrete = WithIssueFromRun(dispatch, run);
            if (item.IsChecks)
                return concrete;

            return await StoreDispatchAsync(workflowRunId, runnerId, item.Id!, concrete);
        }
        catch (WorkflowDispatchRejectedException ex)
        {
            ct.ThrowIfCancellationRequested();
            await RejectWorkflowDispatchAsync(workflowRunId, runnerId, item.Id!, ex);
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
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
