using System.Text.Json;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Project.Domain;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Slack.Services;
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
    private readonly IManagerDeploymentEpoch? _managerEpoch;

    public DispatchService(
        IGrainFactory grains,
        WorkflowRunQuerier workflowRuns,
        IAgentJobStore agentJobs,
        IDispatchSnapshotStore dispatchSnapshots,
        WorkflowItemTranslator translator,
        ILogger<DispatchService> log,
        IDispatchPollObserver? pollObserver = null,
        IManagerDeploymentEpoch? managerEpoch = null)
    {
        _grains = grains;
        _workflowRuns = workflowRuns;
        _agentJobs = agentJobs;
        _dispatchSnapshots = dispatchSnapshots;
        _translator = translator;
        _log = log;
        _pollObserver = pollObserver ?? NoopDispatchPollObserver.Instance;
        _managerEpoch = managerEpoch;
    }

    public async Task<RunnerPollResponse> PollAsync(
        string runnerId,
        RunnerPollRequest req,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var runner = _grains.GetGrain<IRunnerGrain>(runnerId);
        var processGeneration = req.ProcessGeneration ?? string.Empty;
        var admission = await runner.TryBeginPollAsync(processGeneration);
        if (!admission.Admitted)
            return new RunnerPollResponse([]);

        try
        {
            var response = await PollCoreAsync(runner, runnerId, req, processGeneration, admission.Slots, ct).WaitAsync(ct);
            return await runner.ValidatePollAsync(admission.AdmissionToken, processGeneration)
                ? response
                : new RunnerPollResponse([]);
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
        string processGeneration,
        int slots,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await runner.TouchPresenceAsync();
        var info = await runner.GetInfoAsync();
        if (info is null)
            return new RunnerPollResponse([]);
        if (_managerEpoch is { Available: true } epoch
            && !string.IsNullOrWhiteSpace(req.DeploymentEpoch)
            && !string.Equals(req.DeploymentEpoch, epoch.Current, StringComparison.Ordinal))
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
            processGeneration,
            info,
            readiness,
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

        // A Runner that reports admission unavailable can still reconcile
        // held work, but it must not receive fresh claims until its local
        // admission preconditions recover.
        if (req.AdmissionReady is false)
            return new RunnerPollResponse(dispatches);

        ct.ThrowIfCancellationRequested();
        spare = await AddPendingDispatchesAsync(
            runner,
            info,
            info.ProjectId,
            runnerId,
            processGeneration,
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
                processGeneration,
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
        string processGeneration,
        RunnerInfo info,
        RunnerRuntimeReadinessSnapshot readiness,
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
                processGeneration,
                reportedWorkKeys,
                ct);
            if (workKey is null)
                continue;

            // Owner state controls capacity independently of whether this
            // reconciliation round emits an execution dispatch.
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
            if (!string.Equals(record.ClaimedProcessGeneration, processGeneration, StringComparison.Ordinal))
                continue;
            var dispatch = DeserializeAgentDispatch(record);
            var isManagerExecution = dispatch is not null && ManagerExecutionBinding.TryRead(dispatch, out _);
            // An unknown Manager turn may have reached the Server before the
            // Runner disappeared. Its natural-language prompt is not a safe
            // recovery command, so the durable Manager recovery transition
            // owns inspection and the original dispatch is never replayed.
            if (isManagerExecution && IsUnknownAgentJob(record.StateJson))
                continue;

            var workKey = AgentJobWorkKey(record.JobKey, record.WorkId);
            activeWorkKeys.Add(workKey);
            if (reportedWorkKeys.Contains(workKey))
                continue;

            if (dispatch is not null
                && (!isManagerExecution
                    || (ManagerExecutionRuntimeCapabilities.Supports(info, dispatch.AgentDefinition?.Runtime)
                        && readiness.Allows(RuntimeRequirementsFromDispatch(dispatch)))))
                dispatches.Add(dispatch);
        }

        return activeWorkKeys;
    }

    private async Task<int> AddPendingDispatchesAsync(
        IRunnerGrain runner,
        RunnerInfo info,
        string? projectId,
        string runnerId,
        string processGeneration,
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
                    candidate.WorkflowRunId,
                    RequiresFeedbackReconciliation: false))
                .Concat((await _workflowRuns.FindAssignedReconciliationCandidatesAsync(
                        runnerId,
                        candidateLimit,
                        ct))
                    .Select(candidate => new PendingCandidate(
                        candidate.ReadySince,
                        WorkDispatchOwnerKinds.Workflow,
                        candidate.WorkflowRunId,
                        RequiresFeedbackReconciliation: true)))
            : (await _workflowRuns.FindAssignableCandidatesAsync(projectId, candidateLimit, ct))
                .Select(candidate => new PendingCandidate(
                    candidate.ReadySince,
                    WorkDispatchOwnerKinds.Workflow,
                    candidate.WorkflowRunId,
                    RequiresFeedbackReconciliation: false));

        var agentCandidates = assigned
            ? (await _agentJobs.ListAssignedPendingForRunnerAsync(runnerId, candidateLimit, ct))
                .Select(record => new PendingCandidate(
                    record.ReadySince ?? DateTimeOffset.MinValue,
                    WorkDispatchOwnerKinds.AgentJob,
                    record.JobKey,
                    RequiresFeedbackReconciliation: false))
            : (await _agentJobs.ListEligiblePendingAsync(projectId, candidateLimit, ct))
                .Select(record => new PendingCandidate(
                    record.ReadySince ?? DateTimeOffset.MinValue,
                    WorkDispatchOwnerKinds.AgentJob,
                    record.JobKey,
                    RequiresFeedbackReconciliation: false));

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
                var pendingDispatch = record is null ? null : DeserializeAgentDispatch(record);
                var isManagerExecution = pendingDispatch is not null
                    && ManagerExecutionBinding.TryRead(pendingDispatch, out _);
                if (isManagerExecution
                    && !ManagerExecutionRuntimeCapabilities.Supports(info, pendingDispatch?.AgentDefinition?.Runtime))
                    continue;
                var requiredRuntimes = record is null
                    ? null
                    : RuntimeRequirementsFromDispatch(pendingDispatch);
                if (!readiness.Allows(requiredRuntimes))
                    continue;
                var expectation = pendingDispatch is null
                    ? null
                    : BuildAgentJobCapabilityExpectation(info, readiness, pendingDispatch);
                if (expectation is null)
                    continue;

                ct.ThrowIfCancellationRequested();
                var claim = await runner.TryClaimAgentJobAsync(candidate.OwnerId, projectId, expectation, processGeneration);
                dispatch = claim?.Dispatch;
            }
            else
            {
                var requiredRuntimes = await ResolveWorkflowRuntimeRequirementsAsync(
                    candidate.OwnerId,
                    runnerId,
                    candidate.RequiresFeedbackReconciliation,
                    ct);
                if (!readiness.Allows(requiredRuntimes))
                    continue;

                ct.ThrowIfCancellationRequested();
                dispatch = await ClaimAndRenderWorkflowAsync(
                    runner,
                    candidate.OwnerId,
                    projectId,
                    runnerId,
                    assignWorker: !assigned,
                    candidate.RequiresFeedbackReconciliation,
                    processGeneration,
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
        if (witness?.Ready != true
            || witness.Generation is not > 0
            || string.IsNullOrWhiteSpace(info.ConnectionGeneration)
            || !string.Equals(readiness.ConnectionGeneration, info.ConnectionGeneration, StringComparison.Ordinal))
            return null;

        if (!string.IsNullOrWhiteSpace(definition.ReasoningEffort)
            && (catalog.SupportsReasoningEffort != true
                || catalog.Complete != true
                || string.IsNullOrWhiteSpace(catalog.CapabilityRevision)))
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
            info.ConnectionGeneration,
            RequiredAgentJobCapabilities(dispatch, definition.Runtime));
    }

    private static string[]? RequiredAgentJobCapabilities(WorkDispatch dispatch, string runtime)
    {
        var required = HasExplicitExecutionSource(dispatch)
            ? new List<string> { AgentExecutionSources.Version1Capability }
            : [];
        if (ManagerExecutionBinding.TryRead(dispatch, out _))
        {
            required.AddRange(ManagerExecutionRuntimeCapabilities.Required);
            if (string.Equals(runtime, "opencode", StringComparison.OrdinalIgnoreCase))
                required.Add(ManagerExecutionRuntimeCapabilities.IsolatedOpenCodeV1);
        }
        return required.Count == 0 ? null : required.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static bool HasExplicitExecutionSource(WorkDispatch dispatch)
    {
        if (string.IsNullOrWhiteSpace(dispatch.With))
            return false;
        try
        {
            using var document = JsonDocument.Parse(dispatch.With);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("executionSource", out _);
        }
        catch (JsonException)
        {
            return false;
        }
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
        string processGeneration,
        IReadOnlySet<string> reportedWorkKeys,
        CancellationToken ct)
    {
        var run = await _workflowRuns.LoadAsync(workflowRunId, ct);
        if (run is null)
            return (WorkflowOwnerKey(workflowRunId), null, ReserveSlot: true);
        var activeWork = run.CurrentActiveWorkFor(runnerId);
        if (activeWork is null)
            return (null, null, ReserveSlot: false);
        if (!string.Equals(activeWork.ProcessGeneration, processGeneration, StringComparison.Ordinal))
            return (null, null, ReserveSlot: false);

        var workId = activeWork.WorkId;
        var workKey = WorkflowWorkKey(workflowRunId, workId);
        if (reportedWorkKeys.Contains(workKey))
            return (workKey, null, ReserveSlot: true);

        try
        {
            var storedJson = await _dispatchSnapshots.LoadJsonAsync(workflowRunId, workId, ct);
            if (storedJson is not null)
            {
                var stored = JSON.Deserialize<WorkDispatch>(storedJson)!;
                await ValidateWorkflowDispatchAsync(workflowRunId, stored);
                return (workKey, stored, ReserveSlot: true);
            }

            var dispatch = await _translator.TranslateToDispatchAsync(activeWork.Item, workflowRunId, run, runnerId);
            var concrete = WithIssueFromRun(dispatch, run);
            await ValidateWorkflowDispatchAsync(workflowRunId, concrete);
            if (activeWork.IsChecks)
                return (workKey, concrete, ReserveSlot: true);

            var saved = await StoreDispatchAsync(workflowRunId, runnerId, workId, concrete);
            return (workKey, saved, ReserveSlot: true);
        }
        catch (WorkflowDispatchRejectedException ex) when (IsPullRequestIdentityConflict(ex))
        {
            _log.LogWarning(ex,
                "refused dispatch with conflicting Pull Request identity for run {run} work {work}",
                workflowRunId,
                workId);
            if (activeWork.IsChecks
                && await FailWorkflowChecksDispatchAsync(
                    workflowRunId, runnerId, workId, processGeneration, ex.Error, ct))
                return (null, null, ReserveSlot: false);
            return (workKey, null, ReserveSlot: true);
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

    private async Task<WorkDispatch?> ClaimAndRenderWorkflowAsync(
        IRunnerGrain runner,
        string workflowRunId,
        string? projectId,
        string runnerId,
        bool assignWorker,
        bool requiresFeedbackReconciliation,
        string processGeneration,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var pendingRun = await _workflowRuns.LoadAsync(workflowRunId, ct);
        var pendingWork = pendingRun is null
            ? null
            : requiresFeedbackReconciliation
                ? pendingRun.NextAssignedFeedbackReconciliationWork(runnerId)
                : pendingRun.NextWork();
        var pendingItem = BuildWorkflowWorkItem(pendingWork);
        if (pendingRun is null || pendingItem is null)
            return null;

        try
        {
            var preview = await _translator.TranslateToDispatchPreviewAsync(
                pendingItem, workflowRunId, pendingRun);
            await ValidateWorkflowDispatchAsync(workflowRunId, preview);
            await _pollObserver.BeforeWorkflowClaimAsync(workflowRunId).WaitAsync(ct);
        }
        catch (WorkflowDispatchRejectedException ex) when (IsPullRequestIdentityConflict(ex))
        {
            _log.LogWarning(ex,
                "refused dispatch with conflicting Pull Request identity before claiming run {run}",
                workflowRunId);
            return null;
        }

        WorkItem? item;
        try
        {
            item = await runner.TryClaimWorkflowAsync(workflowRunId, projectId, assignWorker, processGeneration);
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
            await ValidateWorkflowDispatchAsync(workflowRunId, concrete);
            if (item.IsChecks)
                return concrete;

            return await StoreDispatchAsync(workflowRunId, runnerId, item.Id!, concrete);
        }
        catch (WorkflowDispatchRejectedException ex) when (IsPullRequestIdentityConflict(ex))
        {
            _log.LogWarning(ex,
                "refused dispatch with conflicting Pull Request identity after claiming run {run} work {work}",
                workflowRunId,
                item.Id);
            if (item.IsChecks)
                await FailWorkflowChecksDispatchAsync(
                    workflowRunId, runnerId, item.Id!, processGeneration, ex.Error, ct);
            return null;
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

    private static bool IsUnknownAgentJob(string stateJson)
    {
        try
        {
            using var document = JsonDocument.Parse(stateJson);
            return document.RootElement.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String
                && string.Equals(status.GetString(), "unknown", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<IReadOnlyList<string>?> ResolveWorkflowRuntimeRequirementsAsync(
        string workflowRunId,
        string runnerId,
        bool requiresFeedbackReconciliation,
        CancellationToken ct)
    {
        var run = await _workflowRuns.LoadAsync(workflowRunId, ct);
        var next = run is null
            ? null
            : requiresFeedbackReconciliation
                ? run.NextAssignedFeedbackReconciliationWork(runnerId)
                : run.NextWork();
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

    private async Task ValidateWorkflowDispatchAsync(
        string workflowRunId,
        WorkDispatch dispatch) =>
        await _grains.GetGrain<IWorkflowGrain>(workflowRunId)
            .ValidateDispatchAsync(dispatch);

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

    private async Task<bool> FailWorkflowChecksDispatchAsync(
        string workflowRunId,
        string runnerId,
        string workId,
        string processGeneration,
        ExecutionError error,
        CancellationToken ct)
    {
        var reason = $"{error.Code}: {error.Message}";
        try
        {
            var verdict = await _grains.GetGrain<IWorkflowGrain>(workflowRunId)
                .FailActiveWorkAsync(runnerId, workId, processGeneration, reason);
            return verdict == WorkReportVerdict.Accepted;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ct.ThrowIfCancellationRequested();
            _log.LogWarning(ex,
                "failed to settle conflicting checks dispatch for run {run} work {work}",
                workflowRunId,
                workId);
            return false;
        }
    }

    private static WorkItem? BuildWorkflowWorkItem(WorkflowWork? work)
    {
        return work switch
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
                task.RecoveryRemaining,
                task.Expect),
            WorkflowChecksWork checks => WorkItem.Checks(
                checks.Stage,
                WorkflowRunExtensions.ChecksWorkIdFor(checks.Stage),
                checks.Items),
            _ => null,
        };
    }

    private static bool IsPullRequestIdentityConflict(WorkflowDispatchRejectedException exception) =>
        string.Equals(exception.Error.Code, "pull_request_identity_conflict", StringComparison.Ordinal);

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
        string OwnerId,
        bool RequiresFeedbackReconciliation);
}
