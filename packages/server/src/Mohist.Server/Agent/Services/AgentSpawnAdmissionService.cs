using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Agent.Services;

public sealed record AgentSpawnAdmission(
    AgentInfo TargetAgent,
    AgentExecutionDefinition Definition,
    string ParentAgentId,
    string WorkDir,
    string RunnerId,
    string Runtime,
    string RuntimeSessionId,
    long BindingEpoch,
    string? ParentWorkspaceName = null);

[Serializable]
[Orleans.GenerateSerializer]
public sealed class AgentSpawnPreplanRejectedException : Exception
{
    public AgentSpawnPreplanRejectedException(string reason)
        : base($"The subagent spawn was rejected before planning: {reason}.") => Reason = reason;

    [Orleans.Id(0)]
    public string Reason { get; }
}

[Serializable]
[Orleans.GenerateSerializer]
public sealed class AgentSpawnPostPlanRejectedException : Exception
{
    public AgentSpawnPostPlanRejectedException(string reason)
        : base($"The subagent spawn was rejected after planning: {reason}.") => Reason = reason;

    [Orleans.Id(0)]
    public string Reason { get; }
}

[Serializable]
[Orleans.GenerateSerializer]
public sealed class AgentSpawnValidationPendingException : Exception
{
    public AgentSpawnValidationPendingException(string reason)
        : base($"The subagent spawn is waiting for validation: {reason}.") => Reason = reason;

    [Orleans.Id(0)]
    public string Reason { get; }
}

public sealed class AgentSpawnAdmissionService(
    AgentSessionQuery sessions,
    AgentQuerier agents,
    AgentReadinessService readiness,
    AgentExecutionSnapshotResolver snapshots,
    IGrainFactory grains,
    ISessionTreeMutationFenceReadPort fenceReadPort) : IScopedService
{
    public async Task<SpawnRequestFence> StartOrValidateFenceAsync(
        string projectId,
        string parentSessionId,
        string idempotencyKey,
        string targetAgentRef,
        string prompt)
    {
        var fingerprint = AgentLaunchCoordinatorCodec.SpawnFingerprint(targetAgentRef, prompt);
        var fence = grains.GetGrain<ISpawnRequestFenceGrain>(
            AgentLaunchCoordinatorCodec.KeyFor(projectId, parentSessionId, idempotencyKey));
        var current = await fence.GetAsync();
        if (current is not null
            && !string.Equals(current.RequestFingerprint, fingerprint, StringComparison.Ordinal))
        {
            throw new LaunchIdempotencyConflictException(idempotencyKey, current.RequestFingerprint);
        }

        return current ?? await fence.StartAsync(new SpawnRequestFence(
            projectId,
            parentSessionId,
            idempotencyKey,
            fingerprint,
            SpawnRequestFenceOutcome.ValidationPending));
    }

    public async Task<AgentSpawnAdmission> AdmitAsync(
        string projectId,
        string parentSessionId,
        string idempotencyKey,
        string targetAgentRef,
        string prompt,
        CancellationToken ct = default)
    {
        var fence = grains.GetGrain<ISpawnRequestFenceGrain>(
            AgentLaunchCoordinatorCodec.KeyFor(projectId, parentSessionId, idempotencyKey));
        var current = await StartOrValidateFenceAsync(
            projectId,
            parentSessionId,
            idempotencyKey,
            targetAgentRef,
            prompt);

        if (current.Outcome == SpawnRequestFenceOutcome.PreplanRejected)
            throw new AgentSpawnPreplanRejectedException(current.PreplanRejectionReason ?? "spawn_rejected");

        var parent = (await sessions.ListByIdsAsync([parentSessionId], ct)).FirstOrDefault();
        if (parent is null || !string.Equals(parent.Label(AgentSessionQueryMetadataKeys.ProjectId), projectId, StringComparison.Ordinal))
            return await RejectAsync(fence, "parent_session_not_found");
        var sourceKind = parent.Label(AgentSessionQueryMetadataKeys.SourceKind);
        if (!string.Equals(sourceKind, "agent-launch", StringComparison.Ordinal)
            && !string.Equals(sourceKind, "agent-connection", StringComparison.Ordinal))
            return await RejectAsync(fence, "parent_session_not_spawnable");

        var parentAgentId = parent.Label(GenericAgentSessionMetadata.AgentId);
        var definition = parent.Session.Settings.Definition;
        if (string.IsNullOrWhiteSpace(parentAgentId) || definition is null)
            return await RejectAsync(fence, "parent_session_missing_definition");
        if (string.IsNullOrWhiteSpace(parent.Session.Runtime.WorkDir))
            return await RejectAsync(fence, "parent_workdir_unavailable");

        var runnerId = parent.Session.Runtime.RunnerId;
        var runtime = parent.Session.Runtime.Runtime;
        var runtimeSessionId = parent.Session.Status.AgentRuntimeSessionId;
        if (parent.Session.Status.Activity == AgentSessionActivity.Unknown
            || string.IsNullOrWhiteSpace(runnerId)
            || string.IsNullOrWhiteSpace(runtime)
            || string.IsNullOrWhiteSpace(runtimeSessionId))
        {
            throw new AgentSpawnValidationPendingException("parent_runner_binding_unavailable");
        }

        var runner = grains.GetGrain<IRunnerGrain>(runnerId);
        RunnerInfo? runnerInfo;
        RunnerRuntimeState runnerState;
        try
        {
            runnerInfo = await runner.GetInfoAsync();
            runnerState = await runner.GetRuntimeStateAsync();
        }
        catch
        {
            throw new AgentSpawnValidationPendingException("parent_runner_binding_unavailable");
        }
        if (runnerInfo is null || runnerState.Status != RunnerStatus.Online)
            throw new AgentSpawnValidationPendingException("parent_runner_binding_unavailable");

        var mutationFence = await fenceReadPort.GetAsync(projectId);
        if (mutationFence.ReconciliationRequired)
            return await RejectAsync(fence, "session_tree_reconciliation_required");
        if (HasNonTerminalStopBlockingSpawn(mutationFence, parentSessionId))
            throw new AgentSpawnValidationPendingException("parent_tree_stop_in_progress");

        var allowed = definition.AllowedSubagents ?? [];
        if (!allowed.Any(item => string.Equals(item.AgentId, targetAgentRef, StringComparison.Ordinal)))
        {
            var targetByName = await agents.GetByNameAsync(projectId, targetAgentRef);
            if (targetByName is null || !allowed.Any(item => string.Equals(item.AgentId, targetByName.Id, StringComparison.Ordinal)))
                return await RejectAsync(fence, "subagent_not_allowed");
            targetAgentRef = targetByName.Id;
        }

        var target = targetAgentRef.StartsWith("agent_", StringComparison.Ordinal)
            ? await agents.GetByIdAsync(projectId, targetAgentRef, ct)
            : await agents.GetByNameAsync(projectId, targetAgentRef);
        target ??= await agents.GetByIdAsync(projectId, targetAgentRef, ct);
        if (target is null)
            return await RejectAsync(fence, "target_agent_not_found");
        if (string.Equals(target.Status, AgentStatus.Archived, StringComparison.Ordinal))
            return await RejectAsync(fence, "target_agent_archived");

        var targetExecutability = await readiness.GetAsync(projectId, target, ct);
        if (AgentExecutabilityStates.IsBlocked(targetExecutability.State))
        {
            return await RejectAsync(
                fence,
                targetExecutability.State == AgentExecutabilityStates.NotConfigured
                    ? "agent_not_configured"
                    : "agent_not_executable");
        }

        var targetDefinition = await snapshots.ResolveAsync(projectId, target.Id)
            ?? throw new AgentSpawnPreplanRejectedException("target_agent_definition_unavailable");

        return new AgentSpawnAdmission(
            target,
            targetDefinition,
            parentAgentId,
            parent.Session.Runtime.WorkDir!,
            runnerId,
            runtime,
            runtimeSessionId,
            parent.Session.BindingEpoch,
            ParentWorkspaceName: parent.Label(AgentSessionQueryMetadataKeys.WorkspaceName));
    }

    private static bool HasNonTerminalStopBlockingSpawn(
        SessionTreeMutationFence fence,
        string parentSessionId)
    {
        if (fence.StopSnapshots is not { Count: > 0 } snapshots)
            return false;
        if (snapshots.Any(item => item.Phase == SessionTreeStopSnapshotPhase.Materializing))
            return true;
        return snapshots.Any(item =>
            item.Phase == SessionTreeStopSnapshotPhase.Frozen
            && item.AdmissionOutcome is SessionTreeStopAdmissionOutcome.Running
                or SessionTreeStopAdmissionOutcome.Unknown
            && item.Membership.Any(member => member.SessionId == parentSessionId));
    }

    private static async Task<AgentSpawnAdmission> RejectAsync(
        ISpawnRequestFenceGrain fence,
        string reason)
    {
        await fence.SetOutcomeAsync(SpawnRequestFenceOutcome.PreplanRejected, reason);
        throw new AgentSpawnPreplanRejectedException(reason);
    }
}
