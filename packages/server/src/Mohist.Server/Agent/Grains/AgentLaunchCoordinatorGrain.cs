using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Agent.Services;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Orleans.Runtime;

namespace Mohist.Server.Agent.Grains;

/// <summary>
/// Durably-keyed manual-agent-launch coordinator. The coordinator is a
/// narrow application process manager: it persists the canonical
/// request, the generated Job/Session/Input/Turn ids, and a
/// one-at-a-time command fence, then drives the four launch steps
/// across the AgentJob and AgentSession participants. It does not
/// mirror Job status, Session activity, transcript, or Runner state.
/// </summary>
public sealed partial class AgentLaunchCoordinatorGrain : Grain, IAgentLaunchCoordinatorGrain
{
    internal const string RecoveryReminderName = "agent-launch-coordinator-recovery";

    private static readonly TimeSpan RecoveryReminderDue = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RecoveryReminderPeriod = TimeSpan.FromSeconds(1);

    private readonly IPersistentState<AgentLaunchCoordinatorState> _state;
    private readonly IGrainFactory _grains;
    private readonly TimeProvider _timeProvider;
    private readonly IAgentLaunchParticipantProbe _participantProbe;
    private readonly ILogger<AgentLaunchCoordinatorGrain> _log;

    public AgentLaunchCoordinatorGrain(
        [PersistentState("agent-launch-coordinator")] IPersistentState<AgentLaunchCoordinatorState> state,
        IGrainFactory grains,
        TimeProvider timeProvider,
        IAgentLaunchParticipantProbe participantProbe,
        ILogger<AgentLaunchCoordinatorGrain> log)
    {
        _state = state;
        _grains = grains;
        _timeProvider = timeProvider;
        _participantProbe = participantProbe;
        _log = log;
    }

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        if (!_state.RecordExists)
            await _state.ReadStateAsync();

        if (_state.State.Plan is null)
        {
            await UnregisterReminderAsync();
            return;
        }

        if (_state.State.Plan.Completed)
        {
            await UnregisterReminderAsync();
            return;
        }

        await EnsureRecoveryReminderAsync();
        await AdvanceAsync();
    }

    public async Task<AgentLaunchCoordinatorResult> LaunchAsync(AgentLaunchCoordinatorCommandEnvelope command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Request);

        if (string.IsNullOrWhiteSpace(command.ProjectId))
            throw new ArgumentException("ProjectId is required.", nameof(command));
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey))
            throw new ArgumentException("IdempotencyKey is required.", nameof(command));
        if (string.IsNullOrWhiteSpace(command.AgentId))
            throw new ArgumentException("AgentId is required.", nameof(command));
        if (string.IsNullOrWhiteSpace(command.AgentName))
            throw new ArgumentException("AgentName is required.", nameof(command));
        if (string.IsNullOrEmpty(command.Prompt)
            && (command.Attachments is null || command.Attachments.Count == 0))
        {
            throw new ArgumentException(
                "Prompt is required unless at least one attachment is accepted.",
                nameof(command));
        }

        var fingerprint = AgentLaunchCoordinatorCodec.Fingerprint(command.Request, command.ConnectionOrigin);

        var existing = _state.State.Plan;
        if (existing is not null)
        {
            if (!string.Equals(existing.ProjectId, command.ProjectId, StringComparison.Ordinal)
                || !string.Equals(existing.IdempotencyKey, command.IdempotencyKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "AgentLaunchCoordinatorGrain primary key does not match the supplied (ProjectId, IdempotencyKey).");
            }
            if (!string.Equals(existing.RequestFingerprint, fingerprint, StringComparison.Ordinal))
            {
                throw new LaunchIdempotencyConflictException(command.IdempotencyKey, existing.RequestFingerprint);
            }

            if (!existing.Completed)
            {
                await EnsureRecoveryReminderAsync();
                await AdvanceAsync();
            }
        }
        else
        {
            var plan = new AgentLaunchCoordinatorPlan(
                ProjectId: command.ProjectId,
                IdempotencyKey: command.IdempotencyKey,
                RequestFingerprint: fingerprint,
                JobKey: $"agent-job-launch-{Guid.NewGuid():N}",
                SessionId: string.IsNullOrWhiteSpace(command.PreMintedSessionId)
                    ? $"agent-session-{Guid.NewGuid():N}"
                    : command.PreMintedSessionId!,
                InputId: string.IsNullOrWhiteSpace(command.PreMintedInputId)
                    ? Guid.NewGuid().ToString("N")
                    : command.PreMintedInputId!,
                TurnId: string.IsNullOrWhiteSpace(command.PreMintedTurnId)
                    ? Guid.NewGuid().ToString("N")
                    : command.PreMintedTurnId!,
                AgentId: command.AgentId,
                AgentName: command.AgentName,
                AgentInstructions: command.AgentInstructions,
                AgentConfigJson: command.AgentConfigJson,
                Model: command.Model,
                Variant: command.Variant,
                Runtime: command.Runtime,
                ReasoningEffort: command.ReasoningEffort,
                Prompt: command.Prompt,
                WorkspaceName: command.WorkspaceName,
                WorkspacePath: command.WorkspacePath,
                IssueNumber: command.IssueNumber,
                EpicNumber: command.EpicNumber,
                Repository: command.Repository,
                Title: command.Title,
                AgentRef: command.Request.AgentRef,
                Completed: false,
                ConnectionOrigin: command.ConnectionOrigin,
                Attachments: command.Attachments,
                StartupContext: command.StartupContext,
                AllowedSubagents: command.AllowedSubagents,
                PinnedRunnerId: command.PinnedRunnerId,
                AgentSessionStartup: command.AgentSessionStartup,
                ParentSessionId: command.ParentSessionId,
                ParentAgentId: command.ParentAgentId,
                ParentExpectedWorkDir: command.ParentExpectedWorkDir,
                ParentExpectedRunnerId: command.ParentExpectedRunnerId,
                ParentExpectedRuntime: command.ParentExpectedRuntime,
                ParentExpectedRuntimeSessionId: command.ParentExpectedRuntimeSessionId,
                ParentLinkEdgeId: command.ParentLinkEdgeId,
                SpawnRequestFingerprint: command.SpawnRequestFingerprint,
                ParentExpectedBindingEpoch: command.ParentExpectedBindingEpoch,
                WorkspaceRepositories: command.WorkspaceRepositories,
                Origin: command.Origin ?? command.Request.Origin,
                TargetId: command.TargetId ?? command.Request.TargetId,
                AttachmentResults: command.AttachmentResults,
                DefinitionCreatedByLaunch: command.DefinitionCreatedByLaunch);
            _state.State.Plan = plan;
            await SaveStateAsync();
            await EnsureRecoveryReminderAsync();
            await AdvanceAsync();
        }

        var final = _state.State.Plan
            ?? throw new InvalidOperationException("Coordinator plan disappeared after advance.");
        if (final.RejectionReason is not null)
            ThrowRejection(final);
        if (!final.Completed)
            throw new LaunchSetupPendingException(final.IdempotencyKey);
        return new AgentLaunchCoordinatorResult(
            JobKey: final.JobKey,
            SessionId: final.SessionId,
            InputId: final.InputId,
            TurnId: final.TurnId,
            AgentId: final.AgentId,
            AgentName: final.AgentName,
            AlreadyPersisted: existing?.Completed == true,
            ParentLinkEdgeId: final.ParentLinkEdgeId,
            WorkspaceName: final.WorkspaceName,
            Origin: final.Origin,
            TargetId: final.TargetId,
            AttachmentResults: final.AttachmentResults);
    }

    public async Task<AgentLaunchCoordinatorResult?> ResumeAsync(AgentLaunchCoordinatorRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var existing = _state.State.Plan;
        if (existing is null)
            return null;

        var fingerprint = AgentLaunchCoordinatorCodec.Fingerprint(request, existing!.ConnectionOrigin);
        if (!string.Equals(existing.RequestFingerprint, fingerprint, StringComparison.Ordinal))
            throw new LaunchIdempotencyConflictException(existing.IdempotencyKey, existing.RequestFingerprint);

        if (!existing.Completed)
        {
            await EnsureRecoveryReminderAsync();
            await AdvanceAsync();
        }

        var final = _state.State.Plan
            ?? throw new InvalidOperationException("Coordinator plan disappeared after resume.");
        if (final.RejectionReason is not null)
            ThrowRejection(final);
        if (!final.Completed)
            throw new LaunchSetupPendingException(final.IdempotencyKey);
        return new AgentLaunchCoordinatorResult(
            JobKey: final.JobKey,
            SessionId: final.SessionId,
            InputId: final.InputId,
            TurnId: final.TurnId,
            AgentId: final.AgentId,
            AgentName: final.AgentName,
            AlreadyPersisted: true,
            ParentLinkEdgeId: final.ParentLinkEdgeId,
            WorkspaceName: final.WorkspaceName,
            Origin: final.Origin,
            TargetId: final.TargetId,
            AttachmentResults: final.AttachmentResults);
    }

    public async Task<AgentLaunchCoordinatorResult?> ResumeExistingSpawnAsync(string spawnRequestFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spawnRequestFingerprint);

        var existing = _state.State.Plan;
        if (existing is null)
            return null;
        if (!string.Equals(existing.SpawnRequestFingerprint, spawnRequestFingerprint, StringComparison.Ordinal))
            throw new LaunchIdempotencyConflictException(existing.IdempotencyKey, existing.RequestFingerprint);

        if (!existing.Completed)
        {
            await EnsureRecoveryReminderAsync();
            await AdvanceAsync();
        }

        var final = _state.State.Plan
            ?? throw new InvalidOperationException("Coordinator plan disappeared after spawn resume.");
        if (final.RejectionReason is not null)
            ThrowRejection(final);
        if (!final.Completed)
            throw new LaunchSetupPendingException(final.IdempotencyKey);
        return new AgentLaunchCoordinatorResult(
            JobKey: final.JobKey,
            SessionId: final.SessionId,
            InputId: final.InputId,
            TurnId: final.TurnId,
            AgentId: final.AgentId,
            AgentName: final.AgentName,
            AlreadyPersisted: true,
            ParentLinkEdgeId: final.ParentLinkEdgeId,
            WorkspaceName: final.WorkspaceName,
            Origin: final.Origin,
            TargetId: final.TargetId,
            AttachmentResults: final.AttachmentResults);
    }

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (!string.Equals(reminderName, RecoveryReminderName, StringComparison.Ordinal))
            return;
        if (_state.State.Plan is null || _state.State.Plan.Completed)
        {
            await UnregisterReminderAsync();
            return;
        }
        await AdvanceAsync();
    }

    private async Task AdvanceAsync()
    {
        var plan = _state.State.Plan;
        if (plan is null || plan.Completed)
            return;

        try
        {
            if (plan.Pending is null)
                await BeginPrepareAsync(plan);
            else
            {
                await ResumePendingAsync(plan);
            }
        }
        catch (Exception ex)
        {
            if (ex is AgentSpawnPostPlanRejectedException postPlanRejection)
            {
                await BeginAbortAfterRejectionAsync(
                    _state.State.Plan ?? plan,
                    postPlanRejection.Reason,
                    postPlan: true);
                return;
            }
            if (ex is AgentSpawnPreplanRejectedException rejection)
            {
                await BeginAbortAfterRejectionAsync(
                    _state.State.Plan ?? plan,
                    rejection.Reason,
                    postPlan: false);
                return;
            }
            _log.LogWarning(ex,
                "AgentLaunchCoordinatorGrain {Key} advance failed; reminder will retry",
                PrimaryKeyString());
        }
    }

    private async Task BeginPrepareAsync(AgentLaunchCoordinatorPlan plan)
    {
        var commandId = plan.Pending?.CommandId ?? Guid.NewGuid().ToString("N");
        _state.State.Plan = plan with
        {
            Pending = new AgentLaunchCoordinatorPending(
                CommandId: commandId,
                Kind: AgentLaunchCoordinatorCommand.PrepareJob,
                Payload: null,
                ExpectedRevision: null),
        };
        await SaveStateAsync();

        var jobGrain = _grains.GetGrain<IAgentJobGrain>(plan.JobKey);
        await jobGrain.PrepareManualLaunchAsync(new PrepareManualLaunchCommand(
            SessionId: plan.SessionId,
            InputId: plan.InputId,
            TurnId: plan.TurnId,
            Prompt: plan.Prompt,
            Model: plan.Model,
            WorkspaceName: plan.WorkspaceName,
            WorkspacePath: plan.WorkspacePath,
            ProjectId: plan.ProjectId,
            Runtime: plan.Runtime,
            AgentId: plan.AgentId,
            AgentInstructions: plan.AgentInstructions,
            AgentConfig: DeserializeAgentConfig(plan.AgentConfigJson),
            Variant: plan.Variant,
            ReasoningEffort: plan.ReasoningEffort,
            IssueNumber: plan.IssueNumber,
            EpicNumber: plan.EpicNumber,
            WorkflowRunId: null,
            ConnectionOrigin: plan.ConnectionOrigin,
            Attachments: plan.Attachments,
            StartupContext: plan.StartupContext,
            AllowedSubagents: plan.AllowedSubagents,
            PinnedRunnerId: plan.PinnedRunnerId,
            AgentSessionStartup: plan.AgentSessionStartup,
            SpawnOrigin: SpawnOriginFor(plan),
            WorkspaceRepositories: plan.WorkspaceRepositories));
        await _participantProbe.OnPrepareJobAsync(plan.JobKey, commandId);

        _state.State.Plan = plan with
        {
            Pending = new AgentLaunchCoordinatorPending(
                CommandId: Guid.NewGuid().ToString("N"),
                Kind: plan.ParentSessionId is null
                    ? AgentLaunchCoordinatorCommand.EnsureInitialLaunch
                    : AgentLaunchCoordinatorCommand.ReserveLink,
                Payload: null,
                ExpectedRevision: null),
        };
        await SaveStateAsync();
        await AdvanceAsync();
    }

    private static AgentJobSpawnOrigin? SpawnOriginFor(AgentLaunchCoordinatorPlan plan)
    {
        if (string.IsNullOrWhiteSpace(plan.ParentSessionId)
            || string.IsNullOrWhiteSpace(plan.ParentAgentId)
            || string.IsNullOrWhiteSpace(plan.ParentLinkEdgeId))
            return null;

        return new AgentJobSpawnOrigin(
            plan.ParentSessionId,
            plan.ParentAgentId,
            plan.ParentLinkEdgeId,
            plan.SessionId,
            plan.JobKey,
            plan.TurnId);
    }

    private async Task BeginAbortAfterRejectionAsync(
        AgentLaunchCoordinatorPlan plan,
        string reason,
        bool postPlan)
    {
        var abortPlan = plan with
        {
            // A task-first rejection is not recorded as terminal until its
            // created definition has been archived. The pending payload
            // keeps the reason available to reminder recovery meanwhile.
            RejectionReason = plan.DefinitionCreatedByLaunch ? null : reason,
            PostPlanRejected = postPlan,
            Pending = new AgentLaunchCoordinatorPending(
                Guid.NewGuid().ToString("N"),
                AgentLaunchCoordinatorCommand.AbortLaunch,
                reason,
                null),
            AbortFenceAcknowledged = false,
            AbortJobAcknowledged = false,
            AbortSessionAcknowledged = false,
            AbortParentBindingAcknowledged = false,
        };
        _state.State.Plan = abortPlan;
        await SaveStateAsync();
        await EnsureRecoveryReminderAsync();
        await BeginAbortLaunchAsync(abortPlan);
    }

    private async Task BeginReserveLinkAsync(AgentLaunchCoordinatorPlan plan)
    {
        var commandId = plan.Pending?.CommandId ?? Guid.NewGuid().ToString("N");
        _state.State.Plan = plan with
        {
            Pending = new AgentLaunchCoordinatorPending(
                commandId,
                AgentLaunchCoordinatorCommand.ReserveLink,
                null,
                null),
        };
        await SaveStateAsync();

        var edgeId = plan.ParentLinkEdgeId
            ?? throw new AgentSpawnPreplanRejectedException("parent_link_edge_missing");
        var result = await _grains
            .GetGrain<ISessionTreeMutationFenceGrain>(plan.ProjectId)
            .ReserveAsync(new ReserveSessionTreeLinkCommand(
                plan.ProjectId,
                edgeId,
                plan.ParentSessionId!,
                plan.SessionId,
                plan.ParentExpectedWorkDir,
                plan.ParentExpectedRunnerId,
                plan.ParentExpectedRuntime,
                plan.ParentExpectedRuntimeSessionId,
                commandId,
                plan.JobKey,
                plan.ParentAgentId,
                plan.ParentExpectedBindingEpoch,
                SessionTreeExpectedLinkState.Absent));
        if (result.State == LinkReservationState.Rejected)
            throw new AgentSpawnPostPlanRejectedException("parent_link_rejected");

        await _grains.GetGrain<ISpawnRequestFenceGrain>(
            AgentLaunchCoordinatorCodec.KeyFor(
                plan.ProjectId,
                plan.ParentSessionId!,
                plan.IdempotencyKey))
            .SetOutcomeAsync(SpawnRequestFenceOutcome.Admitted);

        await _participantProbe.OnReserveLinkAsync(edgeId, commandId);

        _state.State.Plan = plan with
        {
            ParentLinkRevision = result.Revision,
            Pending = new AgentLaunchCoordinatorPending(
                commandId,
                AgentLaunchCoordinatorCommand.EnsureInitialLaunch,
                null,
                null),
        };
        await SaveStateAsync();
        await AdvanceAsync();
    }

    private async Task ResumePendingAsync(AgentLaunchCoordinatorPlan plan)
    {
        switch (plan.Pending!.Kind)
        {
            case AgentLaunchCoordinatorCommand.PrepareJob:
                await BeginPrepareAsync(plan);
                break;
            case AgentLaunchCoordinatorCommand.EnsureInitialLaunch:
                await BeginEnsureInitialLaunchAsync(plan);
                break;
            case AgentLaunchCoordinatorCommand.ReserveLink:
                await BeginReserveLinkAsync(plan);
                break;
            case AgentLaunchCoordinatorCommand.EnsureParentLink:
                await BeginEnsureParentLinkAsync(plan);
                break;
            case AgentLaunchCoordinatorCommand.AbortLaunch:
                await BeginAbortLaunchAsync(plan);
                break;
            case AgentLaunchCoordinatorCommand.SubmitJob:
                await BeginSubmitAsync(plan);
                break;
            default:
                _state.State.Plan = plan with { Pending = null };
                await SaveStateAsync();
                break;
        }
    }

    private async Task BeginEnsureInitialLaunchAsync(AgentLaunchCoordinatorPlan plan)
    {
        if (plan.ParentSessionId is not null)
            await EnsureParentReservationAdmittedAsync(plan);

        var commandId = plan.Pending?.CommandId ?? Guid.NewGuid().ToString("N");
        _state.State.Plan = plan with
        {
            Pending = new AgentLaunchCoordinatorPending(
                CommandId: commandId,
                Kind: AgentLaunchCoordinatorCommand.EnsureInitialLaunch,
                Payload: null,
                ExpectedRevision: null),
        };
        await SaveStateAsync();

        var sessionGrain = _grains.GetGrain<IAgentSessionGrain>(plan.SessionId);
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = plan.ProjectId,
            [AgentSessionQueryMetadataKeys.SourceKind] = plan.ConnectionOrigin is null
                ? "agent-launch"
                : "agent-connection",
            [GenericAgentSessionMetadata.AgentId] = plan.AgentId,
            [GenericAgentSessionMetadata.AgentName] = plan.AgentName,
        };
        if (plan.IssueNumber is > 0)
            labels[GenericAgentSessionMetadata.IssueNumber] = plan.IssueNumber.Value.ToString();
        if (plan.EpicNumber is > 0)
            labels[GenericAgentSessionMetadata.EpicNumber] = plan.EpicNumber.Value.ToString();
        if (!string.IsNullOrWhiteSpace(plan.WorkspacePath))
            labels[GenericAgentSessionMetadata.WorkspacePath] = plan.WorkspacePath!;
        if (!string.IsNullOrWhiteSpace(plan.WorkspaceName))
            labels[GenericAgentSessionMetadata.WorkspaceName] = plan.WorkspaceName!;
        if (!string.IsNullOrWhiteSpace(plan.Repository))
            labels[GenericAgentSessionMetadata.Repository] = plan.Repository!;
        if (!string.IsNullOrWhiteSpace(plan.Origin))
            labels[GenericAgentSessionMetadata.Origin] = plan.Origin!;
        if (!string.IsNullOrWhiteSpace(plan.TargetId))
            labels[GenericAgentSessionMetadata.TargetId] = plan.TargetId!;
        if (plan.ConnectionOrigin is { } origin)
        {
            labels[AgentSessionQueryMetadataKeys.ConnectionId] = origin.ConnectionId;
            labels[AgentSessionQueryMetadataKeys.SlackWorkspaceTeamId] = origin.WorkspaceTeamId;
            labels[AgentSessionQueryMetadataKeys.SlackUserId] = origin.SlackUserId;
            labels[AgentSessionQueryMetadataKeys.SlackConversationId] = origin.ConversationId;
            if (!string.IsNullOrWhiteSpace(origin.ThreadTs))
                labels[AgentSessionQueryMetadataKeys.SlackThreadTs] = origin.ThreadTs;
        }

        var metadata = new AgentSessionMetadata(labels, null);
        await sessionGrain.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            InputId: plan.InputId,
            TurnId: plan.TurnId,
            Prompt: plan.Prompt,
            Source: plan.ConnectionOrigin is null ? "agent-launch" : "agent-connection",
            JobId: plan.JobKey,
            Metadata: metadata,
            Runtime: plan.Runtime ?? AgentConfigSchema.OpenCodeRuntime,
            WorkDir: plan.WorkspacePath,
            Attachments: plan.Attachments,
            Provenance: plan.ConnectionOrigin is { } provenanceOrigin
                ? new AgentSessionInputProvenance(
                    ProviderKind: "slack",
                    WorkspaceId: provenanceOrigin.WorkspaceTeamId,
                    ConversationId: provenanceOrigin.ConversationId,
                    ThreadId: provenanceOrigin.ThreadTs,
                    MemberId: provenanceOrigin.SlackUserId,
                    MessageId: provenanceOrigin.MessageTs,
                    ConnectionId: provenanceOrigin.ConnectionId)
                : null,
            StartupContext: plan.StartupContext,
            Definition: new AgentExecutionDefinition(
                plan.AgentInstructions ?? string.Empty,
                plan.Runtime ?? AgentConfigSchema.OpenCodeRuntime,
                plan.Model,
                plan.Variant,
                [],
                plan.AllowedSubagents,
                plan.ReasoningEffort),
            AgentSessionStartup: plan.AgentSessionStartup,
            LaunchVisibility: plan.ParentSessionId is null
                ? AgentLaunchVisibility.Visible
                : AgentLaunchVisibility.Provisional));
        await _participantProbe.OnEnsureInitialLaunchAsync(plan.SessionId, commandId);

        _state.State.Plan = plan with
        {
            Pending = new AgentLaunchCoordinatorPending(
                CommandId: commandId,
                Kind: plan.ParentSessionId is null
                    ? AgentLaunchCoordinatorCommand.SubmitJob
                    : AgentLaunchCoordinatorCommand.EnsureParentLink,
                Payload: null,
                ExpectedRevision: null),
        };
        await SaveStateAsync();
        await AdvanceAsync();
    }

    private async Task BeginEnsureParentLinkAsync(AgentLaunchCoordinatorPlan plan)
    {
        var commandId = plan.Pending?.CommandId ?? Guid.NewGuid().ToString("N");
        _state.State.Plan = plan with
        {
            Pending = new AgentLaunchCoordinatorPending(
                commandId,
                AgentLaunchCoordinatorCommand.EnsureParentLink,
                null,
                null),
        };
        await SaveStateAsync();

        var edgeId = plan.ParentLinkEdgeId
            ?? throw new AgentSpawnPreplanRejectedException("parent_link_edge_missing");
        var parentSessionId = plan.ParentSessionId
            ?? throw new AgentSpawnPreplanRejectedException("parent_session_missing");
        var bindingReceipt = plan.ParentBindingUseReceipt;
        if (bindingReceipt is null)
        {
            if (plan.ParentExpectedBindingEpoch is not > 0
                || string.IsNullOrWhiteSpace(plan.ParentAgentId))
            {
                throw new AgentSpawnPostPlanRejectedException("parent_binding_changed");
            }

            var acquired = await _grains
                .GetGrain<IAgentSessionGrain>(parentSessionId)
                .AcquireChildAttachBindingAsync(new AcquireChildAttachBindingCommand(
                    plan.ProjectId,
                    commandId,
                    edgeId,
                    parentSessionId,
                    plan.ParentExpectedWorkDir,
                    plan.ParentExpectedRunnerId,
                    plan.ParentExpectedRuntime,
                    plan.ParentExpectedRuntimeSessionId,
                    plan.ParentExpectedBindingEpoch.Value,
                    plan.ParentAgentId));
            if (acquired.State == SessionTreeBindingAcquireState.BindingChanged)
                throw new AgentSpawnPostPlanRejectedException("parent_binding_changed");
            if (acquired.State == SessionTreeBindingAcquireState.ReconciliationRequired
                || acquired.Receipt is null
                || acquired.Receipt.State != SessionTreeBindingUseState.Held)
            {
                throw new AgentSpawnPostPlanRejectedException("session_tree_reconciliation_required");
            }

            bindingReceipt = acquired.Receipt;
            plan = plan with { ParentBindingUseReceipt = bindingReceipt };
            _state.State.Plan = plan;
            await SaveStateAsync();
        }
        var begun = await _grains
            .GetGrain<ISessionTreeMutationFenceGrain>(plan.ProjectId)
            .BeginFinalizeAsync(commandId, edgeId, bindingReceipt);
        if (begun.RejectionReason is "finalize_busy" or "session_tree_mutation_busy" or "parent_tree_mutation_busy")
            throw new AgentSpawnValidationPendingException("finalize_busy");
        if (begun.ReconciliationRequired || begun.RejectionReason == "parent_binding_changed")
            throw new AgentSpawnPostPlanRejectedException(
                begun.RejectionReason ?? "parent_binding_changed");
        if (begun.State == LinkReservationState.Rejected)
            throw new AgentSpawnPostPlanRejectedException("parent_link_rejected");
        if (begun.State == LinkReservationState.Attached)
        {
            if (begun.Revision <= (plan.ParentLinkRevision ?? -1))
                throw new AgentSpawnPostPlanRejectedException("parent_tree_link_revision_mismatch");
            await PromoteAndQueueSubmitAsync(plan);
            return;
        }
        if (begun.State != LinkReservationState.Reserved
            || begun.Revision <= (plan.ParentLinkRevision ?? -1))
            throw new AgentSpawnValidationPendingException("parent_tree_link_not_ready_to_finalize");

        var attached = await _grains.GetGrain<IAgentSessionGrain>(plan.SessionId)
            .ApplyParentLinkAttachAsync(new ApplyParentLinkAttachCommand(
                commandId,
                edgeId,
                plan.ParentSessionId!,
                plan.ParentAgentId!,
                plan.JobKey,
                begun.Revision,
                plan.ParentExpectedWorkDir,
                plan.ParentExpectedRunnerId,
                plan.ParentExpectedRuntime,
                plan.ParentExpectedRuntimeSessionId,
                plan.ProjectId,
                plan.ParentExpectedBindingEpoch ?? 0,
                bindingReceipt.ReceiptId,
                SessionTreeExpectedLinkState.Absent));
        if (attached.State == SessionTreeAttachMutationState.ReconciliationRequired)
            throw new AgentSpawnPostPlanRejectedException(attached.RejectionReason ?? "parent_link_reconciliation_required");
        if (attached.State != SessionTreeAttachMutationState.Attached || attached.Receipt is null)
            throw new AgentSpawnValidationPendingException("parent_link_attach_not_acknowledged");
        var acknowledged = await _grains
            .GetGrain<ISessionTreeMutationFenceGrain>(plan.ProjectId)
            .AcknowledgeFinalizeAsync(attached.Receipt);
        if (acknowledged.ReconciliationRequired)
            throw new AgentSpawnPostPlanRejectedException("parent_link_reconciliation_required");
        if (acknowledged.State != LinkReservationState.Reserved
            || acknowledged.Revision != begun.Revision)
            throw new AgentSpawnValidationPendingException("parent_link_attach_not_acknowledged");
        var finalized = await _grains
            .GetGrain<ISessionTreeMutationFenceGrain>(plan.ProjectId)
            .CommitFinalizeAsync(commandId, edgeId, begun.Revision);
        if (finalized.ReconciliationRequired)
            throw new AgentSpawnPostPlanRejectedException(
                finalized.RejectionReason ?? "parent_link_reconciliation_required");
        if (finalized.State != LinkReservationState.Attached)
            throw new AgentSpawnValidationPendingException("parent_tree_link_not_attached");

        await _participantProbe.OnParentLinkCommittedAsync(edgeId, commandId);
        await PromoteAndQueueSubmitAsync(plan);
    }

    private async Task EnsureParentReservationAdmittedAsync(AgentLaunchCoordinatorPlan plan)
    {
        var fence = await _grains.GetGrain<ISessionTreeMutationFenceGrain>(plan.ProjectId).GetAsync();
        var reservation = (fence.Reservations
            ?? (fence.Reservation is { } legacy ? [legacy] : []))
            .FirstOrDefault(item => item.EdgeId == plan.ParentLinkEdgeId);
        if (reservation?.State == LinkReservationState.Rejected)
            throw new AgentSpawnPostPlanRejectedException("parent_link_rejected");
        if (reservation is null)
            throw new AgentSpawnPostPlanRejectedException("parent_link_rejected");
    }

    private async Task PromoteAndQueueSubmitAsync(AgentLaunchCoordinatorPlan plan)
    {
        await EnsureParentLinkReadyForPromotionAsync(plan);
        await _grains.GetGrain<IAgentSessionGrain>(plan.SessionId).PromoteProvisionalLaunchAsync();
        await _grains.GetGrain<IAgentJobGrain>(plan.JobKey).PromotePreparedLaunchAsync();

        _state.State.Plan = plan with
        {
            Pending = new AgentLaunchCoordinatorPending(
                Guid.NewGuid().ToString("N"),
                AgentLaunchCoordinatorCommand.SubmitJob,
                null,
                null),
        };
        await SaveStateAsync();
        await AdvanceAsync();
    }

    private async Task EnsureParentLinkReadyForPromotionAsync(AgentLaunchCoordinatorPlan plan)
    {
        if (plan.ParentSessionId is null)
            return;

        var fence = await _grains.GetGrain<ISessionTreeMutationFenceGrain>(plan.ProjectId).GetAsync();
        if (fence.ReconciliationRequired)
            throw new AgentSpawnPostPlanRejectedException(
                fence.ReconciliationReason ?? "parent_link_reconciliation_required");
        if (fence.ReleaseObligation is not null)
            throw new AgentSpawnValidationPendingException("parent_tree_release_pending");

        var reservation = (fence.Reservations
            ?? (fence.Reservation is { } legacy ? [legacy] : []))
            .FirstOrDefault(item => item.EdgeId == plan.ParentLinkEdgeId);
        if (reservation?.State == LinkReservationState.Rejected)
            throw new AgentSpawnPostPlanRejectedException("parent_link_rejected");
        if (reservation?.State != LinkReservationState.Attached)
            throw new AgentSpawnValidationPendingException("parent_tree_link_not_attached");
    }

    private async Task BeginAbortLaunchAsync(AgentLaunchCoordinatorPlan plan)
    {
        var current = plan;
        var reason = plan.RejectionReason
            ?? plan.Pending?.Payload
            ?? "spawn_rejected";
        var fence = _grains.GetGrain<ISessionTreeMutationFenceGrain>(plan.ProjectId);

        if (!current.AbortFenceAcknowledged)
        {
            var snapshot = await fence.GetAsync();
            var reservations = snapshot.Reservations
                ?? (snapshot.Reservation is { } legacy ? [legacy] : []);
            var reservation = reservations.FirstOrDefault(item => item.EdgeId == plan.ParentLinkEdgeId);
            if (reservation is not null && reservation.State == LinkReservationState.Reserved)
            {
                var pending = (snapshot.PendingMutations
                    ?? (snapshot.PendingMutation is { } legacyPending ? [legacyPending] : []))
                    .FirstOrDefault(item => item.EdgeId == plan.ParentLinkEdgeId);
                var rejected = await fence.RejectAsync(
                    pending?.CommandId ?? reservation.CommandId ?? string.Empty,
                    plan.ParentLinkEdgeId!,
                    reason);
                if (rejected.ReconciliationRequired)
                    return;
            }
            current = current with { AbortFenceAcknowledged = true };
            _state.State.Plan = current;
            await SaveStateAsync();
        }

        if (!current.AbortParentBindingAcknowledged)
        {
            if (current.ParentBindingUseReceipt is not null)
            {
                var released = await _grains
                    .GetGrain<IAgentSessionGrain>(current.ParentBindingUseReceipt.ParentSessionId)
                    .ReleaseChildAttachBindingAsync(new ReleaseChildAttachBindingCommand(
                        current.ParentBindingUseReceipt,
                        "rejected"));
                if (released.State == SessionTreeBindingReleaseState.ReconciliationRequired)
                    return;
            }
            current = current with { AbortParentBindingAcknowledged = true };
            _state.State.Plan = current;
            await SaveStateAsync();
        }

        if (!current.AbortJobAcknowledged)
        {
            await _grains.GetGrain<IAgentJobGrain>(plan.JobKey).AbortPreparedLaunchAsync(reason);
            current = current with { AbortJobAcknowledged = true };
            _state.State.Plan = current;
            await SaveStateAsync();
        }

        if (!current.AbortSessionAcknowledged)
        {
            var sessionGrain = _grains.GetGrain<IAgentSessionGrain>(plan.SessionId);
            if (await sessionGrain.GetAsync() is not null)
                await sessionGrain.AbortProvisionalLaunchAsync(plan.JobKey, plan.TurnId, reason);
            current = current with { AbortSessionAcknowledged = true };
            _state.State.Plan = current;
            await SaveStateAsync();
        }

        if (current.DefinitionCreatedByLaunch)
        {
            // Archiving is part of the durable abort convergence. If the
            // process dies after this call but before the completed plan is
            // persisted, the reminder repeats the idempotent archive before
            // it records the terminal rejection as complete.
            await _participantProbe.OnArchiveDefinitionAsync(
                current.AgentId,
                current.Pending?.CommandId ?? string.Empty);
            await _grains
                .GetGrain<IAgentGrain>(GrainKey.Agent(current.ProjectId, current.AgentId))
                .ArchiveAsync();
        }

        _state.State.Plan = current with
        {
            RejectionReason = reason,
            Pending = null,
            Completed = true,
        };
        await SaveStateAsync();
        await UnregisterReminderAsync();
    }

    private async Task BeginSubmitAsync(AgentLaunchCoordinatorPlan plan)
    {
        if (plan.ParentSessionId is not null)
        {
            var fence = await _grains.GetGrain<ISessionTreeMutationFenceGrain>(plan.ProjectId).GetAsync();
            var reservation = (fence.Reservations
                ?? (fence.Reservation is { } legacy ? [legacy] : []))
                .FirstOrDefault(item =>
                string.Equals(item.EdgeId, plan.ParentLinkEdgeId, StringComparison.Ordinal));
            if (fence.ReconciliationRequired)
                throw new AgentSpawnPostPlanRejectedException(
                    fence.ReconciliationReason ?? "parent_link_reconciliation_required");
            if (fence.ReleaseObligation is not null)
                throw new AgentSpawnValidationPendingException("parent_tree_release_pending");
            if (reservation?.State != LinkReservationState.Attached)
                throw new AgentSpawnValidationPendingException("parent_tree_link_not_attached");
        }

        var commandId = plan.Pending?.CommandId ?? Guid.NewGuid().ToString("N");
        _state.State.Plan = plan with
        {
            Pending = new AgentLaunchCoordinatorPending(
                CommandId: commandId,
                Kind: AgentLaunchCoordinatorCommand.SubmitJob,
                Payload: null,
                ExpectedRevision: null),
        };
        await SaveStateAsync();

        var jobGrain = _grains.GetGrain<IAgentJobGrain>(plan.JobKey);
        await jobGrain.SubmitPreparedLaunchAsync();
        await _participantProbe.OnSubmitJobAsync(plan.JobKey, commandId);

        _state.State.Plan = plan with
        {
            Pending = null,
            Completed = true,
        };
        await SaveStateAsync();
        await UnregisterReminderAsync();
    }

}

[GenerateSerializer]
public sealed class AgentLaunchCoordinatorState
{
    [Id(0)] public AgentLaunchCoordinatorPlan? Plan { get; set; }
}

/// <summary>
/// Public envelope the coordinator route forwards. Carries the
/// canonical request snapshot plus the resolved Agent fields the
/// coordinator needs to populate the plan and the metadata the
/// AgentSession will receive.
/// </summary>
[GenerateSerializer]
public sealed record AgentLaunchCoordinatorCommandEnvelope(
    [property: Id(0)] string ProjectId,
    [property: Id(1)] string IdempotencyKey,
    [property: Id(2)] string AgentId,
    [property: Id(3)] string AgentName,
    [property: Id(4)] string? AgentInstructions,
    [property: Id(5)] string? AgentConfigJson,
    [property: Id(6)] string? Model,
    [property: Id(7)] string? Variant,
    [property: Id(8)] string? Runtime,
    [property: Id(9)] string Prompt,
    [property: Id(10)] string? WorkspacePath,
    [property: Id(11)] int? IssueNumber,
    [property: Id(12)] int? EpicNumber,
    [property: Id(13)] string? Repository,
    [property: Id(14)] string? Title,
    [property: Id(15)] AgentLaunchCoordinatorRequest Request = null!,
    [property: Id(16)] ConnectionLaunchOrigin? ConnectionOrigin = null,
    /// <summary>
    /// Pre-minted input id the route wants the coordinator to use.
    /// When non-null the coordinator adopts this id verbatim
    /// instead of minting a fresh one. Required when the launch
    /// carries attachments so the route can validate+bind them
    /// before the plan is committed (binding keys on the input id).
    /// Append-only Orleans field id (next free after
    /// <see cref="ConnectionOrigin"/>).
    /// </summary>
    [property: Id(17)] string? PreMintedInputId = null,
    /// <summary>
    /// Pre-minted turn id the route wants the coordinator to use.
    /// Mirrors <see cref="PreMintedInputId"/>: when non-null the
    /// coordinator adopts this id verbatim. Append-only Orleans
    /// field id (next free after <see cref="PreMintedInputId"/>).
    /// </summary>
    [property: Id(18)] string? PreMintedTurnId = null,
    /// <summary>
    /// Accepted attachment descriptors the route already bound to
    /// <see cref="PreMintedInputId"/>. Persisted on the durable
    /// plan so recovery replays the same accepted set; the
    /// AgentSession initial-launch and AgentJob dispatch builders
    /// project these onto the durable SessionInput child record
    /// and the AgentJob dispatch envelope. Append-only Orleans
    /// field id (next free after <see cref="PreMintedTurnId"/>).
    /// </summary>
    [property: Id(19)] IReadOnlyList<AgentSessionInputAttachmentDescriptor>? Attachments = null,
    [property: Id(20)] string? PreMintedSessionId = null,
    /// <summary>
    /// Optional bounded external discussion the caller attaches to
    /// the first launch as read-only background. Carried verbatim
    /// onto the durable plan (<see cref="StartupContext"/>) so a
    /// recovery replay returns the first-accepted snapshot rather
    /// than recomputing it. Composed at dispatch time as an
    /// explicit read-only block prepended to the task prompt;
    /// <see cref="AgentJobInput.Prompt"/> and the SessionInput text
    /// stay task-only so the work label stays clean. Append-only
    /// Orleans field id (next free after
    /// <see cref="PreMintedSessionId"/>).
    /// </summary>
    [property: Id(21)] AgentStartupContext? StartupContext = null,
    [property: Id(22)] AllowedSubagentSnapshot[]? AllowedSubagents = null,
    [property: Id(23)] string? PinnedRunnerId = null,
    [property: Id(24)] AgentSessionStartup? AgentSessionStartup = null,
    [property: Id(25)] string? ParentSessionId = null,
    [property: Id(26)] string? ParentAgentId = null,
    [property: Id(27)] string? ParentExpectedWorkDir = null,
    [property: Id(28)] string? ParentExpectedRunnerId = null,
    [property: Id(29)] string? ParentExpectedRuntime = null,
    [property: Id(30)] string? ParentExpectedRuntimeSessionId = null,
    [property: Id(31)] string? ParentLinkEdgeId = null,
    [property: Id(32)] string? SpawnRequestFingerprint = null,
    [property: Id(33)] long? ParentExpectedBindingEpoch = null,
    [property: Id(34)] string? WorkspaceName = null,
    [property: Id(35)] IReadOnlyList<WorkspaceRepositorySnapshot>? WorkspaceRepositories = null,
    [property: Id(36)] string? Origin = null,
    [property: Id(37)] string? TargetId = null,
    [property: Id(38)] string? ReasoningEffort = null,
    /// <summary>
    /// Response attachment verdicts captured before the coordinator plan
    /// was committed. This is append-only so older envelopes deserialize
    /// with no response metadata.
    /// </summary>
    [property: Id(39)] IReadOnlyList<AgentInputAttachmentAcceptance>? AttachmentResults = null,
    /// <summary>
    /// Marks a task-first envelope whose definition was created before the
    /// canonical launch plan. Append-only Orleans field id.
    /// </summary>
    [property: Id(40)] bool DefinitionCreatedByLaunch = false);
