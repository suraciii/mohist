using Microsoft.Extensions.Logging;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Workflow.Services;
using Orleans.Runtime;

namespace Mohist.Server.Workflow.Grains;

/// <summary>
/// Stores one rendered Workflow Agent handoff, its first preflight result,
/// and — after an accepted receipt — the durable activation cursor that
/// materializes the reserved AgentJob, AgentSession, first SessionInput, and
/// first AgentTurn. The grain is a narrow process manager mirroring
/// <see cref="AgentLaunchCoordinatorGrain"/>: one persisted step at a time, a
/// recovery reminder that resumes an incomplete cursor, and a participant
/// probe seam. It never mirrors Job status, Session activity, transcript, or
/// Runner state.
/// </summary>
public sealed class WorkflowAgentHandoffGrain : Grain, IWorkflowAgentHandoffGrain
{
    internal const string ActivationReminderName = "workflow-agent-handoff-activation";

    private static readonly TimeSpan ActivationReminderDue = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ActivationReminderPeriod = TimeSpan.FromSeconds(1);

    private readonly IPersistentState<WorkflowAgentHandoffState> _state;
    private readonly IWorkflowAgentHandoffPreflight _preflight;
    private readonly IWorkflowAgentHandoffParticipantProbe _participantProbe;
    private readonly IGrainFactory _grains;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WorkflowAgentHandoffGrain> _log;

    public WorkflowAgentHandoffGrain(
        [PersistentState("workflow-agent-handoff")] IPersistentState<WorkflowAgentHandoffState> state,
        IWorkflowAgentHandoffPreflight preflight,
        IWorkflowAgentHandoffParticipantProbe participantProbe,
        IGrainFactory grains,
        TimeProvider timeProvider,
        ILogger<WorkflowAgentHandoffGrain> log)
    {
        _state = state;
        _preflight = preflight;
        _participantProbe = participantProbe;
        _grains = grains;
        _timeProvider = timeProvider;
        _log = log;
    }

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        if (!_state.RecordExists)
            await _state.ReadStateAsync();

        var plan = _state.State.Plan;
        var activation = _state.State.Activation;
        if (plan is null
            || plan.Disposition != WorkflowAgentHandoffDisposition.Accepted
            || activation is null
            || activation.CompletedAt is not null)
        {
            await UnregisterReminderAsync();
            return;
        }

        // An activation was interrupted mid-step: the reminder keeps resuming
        // the persisted cursor until every participant write is acknowledged.
        await EnsureActivationReminderAsync();
        await AdvanceActivationAsync();
    }

    public async Task<WorkflowAgentHandoffResult> PrepareAsync(WorkflowAgentHandoffCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsurePrimaryKey(command);

        var fingerprint = WorkflowAgentHandoffCodec.Fingerprint(command);
        var existing = _state.State.Plan;
        if (existing is not null)
        {
            EnsureIdentity(existing.Command, command);
            if (!string.Equals(existing.RequestFingerprint, fingerprint, StringComparison.Ordinal))
                throw new WorkflowAgentHandoffConflictException(command.CommandId, existing.RequestFingerprint);
            return Result(existing, alreadyPersisted: true);
        }

        var rejection = Validate(command);
        WorkflowAgentHandoffAgentSnapshot? agent = null;
        WorkflowAgentHandoffRunContext? runContext = null;
        AgentExecutionDefinition? definition = null;
        WorkflowAgentInvocation? invocation = null;
        if (rejection is null)
        {
            // One preflight pass freezes everything activation needs: the
            // agent identity next to the execution definition, the rendered
            // task contract, the logical session name, and the run-scoped
            // workspace binding. Replays never re-read any of it.
            runContext = await _preflight.ResolveRunContextAsync(command.WorkflowRunId);
            agent = await _preflight.ResolveAgentAsync(command.ProjectId, command.AgentRef);
            if (agent is null)
            {
                rejection = new WorkflowAgentHandoffRejection(
                    "agent_not_found",
                    $"Workflow Agent handoff references Agent '{command.AgentRef}' which does not exist or is archived.");
            }
            else if (string.IsNullOrWhiteSpace(agent.Definition.Runtime))
            {
                rejection = new WorkflowAgentHandoffRejection(
                    "agent_runtime_unavailable",
                    $"Workflow Agent handoff references Agent '{command.AgentRef}' without a usable runtime.");
            }
            else
            {
                definition = agent.Definition;
                invocation = WorkflowAgentHandoffCodec.InvocationFor(command);
            }
        }

        var plan = new WorkflowAgentHandoffPlan(
            Command: command,
            RequestFingerprint: fingerprint,
            Disposition: rejection is null
                ? WorkflowAgentHandoffDisposition.Prepared
                : WorkflowAgentHandoffDisposition.Rejected,
            Invocation: invocation,
            ExecutionDefinition: definition,
            PreparedAt: _timeProvider.GetUtcNow(),
            Rejection: rejection,
            AgentId: agent?.AgentId,
            AgentName: agent?.AgentName,
            SessionName: rejection is null ? SessionNameFor(command) : null,
            RunContext: runContext);
        _state.State.Plan = plan;
        await _state.WriteStateAsync();
        return Result(plan, alreadyPersisted: false);
    }

    public async Task<WorkflowAgentHandoffResult> AcceptAsync(WorkflowAgentHandoffAcceptance acceptance)
    {
        ArgumentNullException.ThrowIfNull(acceptance);

        var plan = _state.State.Plan
            ?? throw new InvalidOperationException("Workflow Agent handoff has not been prepared.");
        if (!string.Equals(plan.Command.CommandId, acceptance.CommandId, StringComparison.Ordinal)
            || !string.Equals(plan.RequestFingerprint, acceptance.RequestFingerprint, StringComparison.Ordinal))
        {
            throw new WorkflowAgentHandoffConflictException(
                acceptance.CommandId,
                plan.RequestFingerprint);
        }

        if (plan.Disposition != WorkflowAgentHandoffDisposition.Prepared)
            return Result(plan, alreadyPersisted: true);

        plan = plan with
        {
            Disposition = WorkflowAgentHandoffDisposition.Accepted,
            AcceptedAt = _timeProvider.GetUtcNow(),
        };
        _state.State.Plan = plan;
        await _state.WriteStateAsync();
        return Result(plan, alreadyPersisted: false);
    }

    public async Task<WorkflowAgentHandoffActivationResult> ActivateAsync()
    {
        var plan = _state.State.Plan
            ?? throw new InvalidOperationException("Workflow Agent handoff has not been prepared.");
        switch (plan.Disposition)
        {
            case WorkflowAgentHandoffDisposition.Rejected:
                throw new WorkflowAgentHandoffRejectedException(plan.Rejection!);
            case WorkflowAgentHandoffDisposition.Prepared:
                throw new InvalidOperationException(
                    "Workflow Agent handoff has not been accepted; activation requires an accepted receipt.");
            case WorkflowAgentHandoffDisposition.Activated:
                return new WorkflowAgentHandoffActivationResult(
                    plan.Disposition,
                    plan.Invocation,
                    AlreadyActivated: true);
        }

        if (_state.State.Activation is null)
        {
            _state.State.Activation = new WorkflowAgentHandoffActivation(
                CommandId: Guid.NewGuid().ToString("N"),
                NextStep: WorkflowAgentHandoffActivationStep.PrepareJob,
                StartedAt: _timeProvider.GetUtcNow());
            await _state.WriteStateAsync();
        }
        await EnsureActivationReminderAsync();
        await AdvanceActivationAsync();

        var final = _state.State.Plan
            ?? throw new InvalidOperationException("Workflow Agent handoff plan disappeared during activation.");
        if (final.Disposition != WorkflowAgentHandoffDisposition.Activated)
            throw new WorkflowAgentHandoffActivationPendingException(final.Command.CommandId);
        return new WorkflowAgentHandoffActivationResult(
            final.Disposition,
            final.Invocation,
            AlreadyActivated: false);
    }

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (!string.Equals(reminderName, ActivationReminderName, StringComparison.Ordinal))
            return;
        var plan = _state.State.Plan;
        var activation = _state.State.Activation;
        if (plan is null
            || plan.Disposition != WorkflowAgentHandoffDisposition.Accepted
            || activation is null
            || activation.CompletedAt is not null)
        {
            await UnregisterReminderAsync();
            return;
        }
        await AdvanceActivationAsync();
    }

    public Task<WorkflowAgentHandoffPlan?> GetPlanAsync() =>
        Task.FromResult(_state.State.Plan);

    private async Task AdvanceActivationAsync()
    {
        var plan = _state.State.Plan;
        var activation = _state.State.Activation;
        if (plan is null
            || plan.Disposition != WorkflowAgentHandoffDisposition.Accepted
            || activation is null
            || activation.CompletedAt is not null)
        {
            return;
        }

        try
        {
            switch (activation.NextStep)
            {
                case WorkflowAgentHandoffActivationStep.PrepareJob:
                    await PrepareJobAsync(plan, activation);
                    break;
                case WorkflowAgentHandoffActivationStep.EnsureInitialLaunch:
                    await EnsureInitialLaunchAsync(plan, activation);
                    break;
                case WorkflowAgentHandoffActivationStep.SubmitJob:
                    await SubmitJobAsync(plan, activation);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "WorkflowAgentHandoff {Key} activation step {Step} failed; reminder will retry",
                this.GetPrimaryKeyString(),
                _state.State.Activation?.NextStep);
        }
    }

    /// <summary>
    /// Durable job input under the minted JobKey: no Runner work, the job
    /// stays Visible (a workflow handoff has no parent link or approval
    /// gate). Idempotent — a replay of an equivalent plan returns the stored
    /// input; a conflicting plan throws without mutating anything.
    /// </summary>
    private async Task PrepareJobAsync(
        WorkflowAgentHandoffPlan plan,
        WorkflowAgentHandoffActivation activation)
    {
        var invocation = RequiredInvocation(plan);
        await _grains.GetGrain<IAgentJobGrain>(invocation.JobKey)
            .PrepareManualLaunchAsync(ManualLaunchCommandFor(plan));
        await _participantProbe.OnPrepareJobAsync(invocation.JobKey, activation.CommandId);

        await AdvanceCursorAsync(
            plan,
            activation,
            WorkflowAgentHandoffActivationStep.EnsureInitialLaunch);
        await AdvanceActivationAsync();
    }

    /// <summary>
    /// Materializes the AgentSession, first SessionInput, and first AgentTurn
    /// under the minted SessionId with the frozen execution definition and
    /// the workflow lineage labels. Idempotent by input and turn id.
    /// </summary>
    private async Task EnsureInitialLaunchAsync(
        WorkflowAgentHandoffPlan plan,
        WorkflowAgentHandoffActivation activation)
    {
        var invocation = RequiredInvocation(plan);
        await _grains.GetGrain<IAgentSessionGrain>(invocation.SessionId)
            .EnsureInitialLaunchAsync(EnsureInitialLaunchCommandFor(plan));
        await _participantProbe.OnEnsureInitialLaunchAsync(invocation.SessionId, activation.CommandId);

        await AdvanceCursorAsync(
            plan,
            activation,
            WorkflowAgentHandoffActivationStep.SubmitJob);
        await AdvanceActivationAsync();
    }

    /// <summary>
    /// Submits the prepared job into shared admission. Idempotent — a replay
    /// on an admitted, running, or terminal job is a no-op.
    /// </summary>
    private async Task SubmitJobAsync(
        WorkflowAgentHandoffPlan plan,
        WorkflowAgentHandoffActivation activation)
    {
        var invocation = RequiredInvocation(plan);
        await _grains.GetGrain<IAgentJobGrain>(invocation.JobKey)
            .SubmitPreparedLaunchAsync();
        await _participantProbe.OnSubmitJobAsync(invocation.JobKey, activation.CommandId);

        // Terminal activation: both writes persist atomically with the
        // disposition so a replay of ActivateAsync is a pure no-op.
        _state.State.Plan = plan with
        {
            Disposition = WorkflowAgentHandoffDisposition.Activated,
            ActivatedAt = _timeProvider.GetUtcNow(),
        };
        _state.State.Activation = activation with { CompletedAt = _timeProvider.GetUtcNow() };
        await _state.WriteStateAsync();
        await UnregisterReminderAsync();
    }

    private async Task AdvanceCursorAsync(
        WorkflowAgentHandoffPlan plan,
        WorkflowAgentHandoffActivation activation,
        WorkflowAgentHandoffActivationStep next)
    {
        _state.State.Plan = plan;
        _state.State.Activation = activation with { NextStep = next };
        await _state.WriteStateAsync();
    }

    private PrepareManualLaunchCommand ManualLaunchCommandFor(WorkflowAgentHandoffPlan plan)
    {
        var command = plan.Command;
        var invocation = RequiredInvocation(plan);
        var definition = RequiredDefinition(plan);
        return new PrepareManualLaunchCommand(
            SessionId: invocation.SessionId,
            InputId: invocation.InputId,
            TurnId: invocation.TurnId,
            Prompt: command.Prompt,
            Model: definition.Model,
            WorkspaceName: plan.RunContext?.Workspace?.Name,
            WorkspacePath: plan.RunContext?.Workspace?.Path,
            ProjectId: command.ProjectId,
            Runtime: definition.Runtime,
            AgentId: plan.AgentId!,
            AgentInstructions: definition.Instructions,
            AgentConfig: null,
            Variant: definition.Variant,
            IssueNumber: plan.RunContext?.IssueNumber,
            EpicNumber: plan.RunContext?.EpicNumber,
            WorkflowRunId: command.WorkflowRunId,
            AllowedSubagents: definition.AllowedSubagents,
            Skills: definition.Skills,
            WorkflowInvocation: new AgentJobWorkflowInvocation(
                InvocationId: invocation.InvocationId,
                TaskRunId: command.TaskRunId,
                WorkId: command.CommandId),
            // D4: the frozen timeout becomes the per-invocation deadline;
            // an omitted timeout resolves to the runtime action default so
            // the handoff matches inline mohist/opencode / mohist/pi
            // semantics instead of the shorter global JobTimeout backstop.
            TimeoutMilliseconds: command.TimeoutMilliseconds
                ?? WorkflowAgentHandoffDeadline.DefaultTimeoutMilliseconds,
            Expect: command.Expect);
    }

    private EnsureInitialLaunchCommand EnsureInitialLaunchCommandFor(WorkflowAgentHandoffPlan plan)
    {
        var command = plan.Command;
        var invocation = RequiredInvocation(plan);
        var definition = RequiredDefinition(plan);
        var run = plan.RunContext;
        var context = new WorkflowAgentSessionContext(
            ProjectId: command.ProjectId,
            WorkflowRunId: command.WorkflowRunId,
            SessionName: plan.SessionName!,
            IssueNumber: run?.IssueNumber,
            WorkId: command.CommandId,
            WorkType: "task",
            EpicNumber: run?.EpicNumber);
        var labels = new Dictionary<string, string>(WorkflowAgentSessionMetadata.Labels(context), StringComparer.Ordinal)
        {
            [GenericAgentSessionMetadata.AgentId] = plan.AgentId!,
            [GenericAgentSessionMetadata.AgentName] = plan.AgentName!,
            [AgentSessionQueryMetadataKeys.TaskRunId] = command.TaskRunId,
            [AgentSessionQueryMetadataKeys.InvocationId] = invocation.InvocationId,
        };
        if (!string.IsNullOrWhiteSpace(run?.Workspace?.Name))
            labels[GenericAgentSessionMetadata.WorkspaceName] = run!.Workspace!.Name!;
        if (!string.IsNullOrWhiteSpace(run?.Workspace?.Path))
            labels[GenericAgentSessionMetadata.WorkspacePath] = run!.Workspace!.Path!;

        return new EnsureInitialLaunchCommand(
            InputId: invocation.InputId,
            TurnId: invocation.TurnId,
            Prompt: command.Prompt,
            Source: "workflow",
            JobId: invocation.JobKey,
            Metadata: new AgentSessionMetadata(labels, null),
            Runtime: definition.Runtime,
            WorkDir: run?.Workspace?.Path,
            Definition: definition,
            LaunchVisibility: AgentLaunchVisibility.Visible);
    }

    private static string SessionNameFor(WorkflowAgentHandoffCommand command) =>
        string.IsNullOrWhiteSpace(command.Session)
            ? command.CommandId
            : command.Session!;

    private static WorkflowAgentInvocation RequiredInvocation(WorkflowAgentHandoffPlan plan) =>
        plan.Invocation
        ?? throw new InvalidOperationException(
            "Workflow Agent handoff plan has no reserved invocation to activate.");

    private static AgentExecutionDefinition RequiredDefinition(WorkflowAgentHandoffPlan plan) =>
        plan.ExecutionDefinition
        ?? throw new InvalidOperationException(
            "Workflow Agent handoff plan has no frozen execution definition to activate.");

    private static WorkflowAgentHandoffResult Result(
        WorkflowAgentHandoffPlan plan,
        bool alreadyPersisted) =>
        new(plan.Disposition, plan.Invocation, plan.Rejection, alreadyPersisted);

    private static void EnsureIdentity(
        WorkflowAgentHandoffCommand persisted,
        WorkflowAgentHandoffCommand supplied)
    {
        if (!string.Equals(persisted.CommandId, supplied.CommandId, StringComparison.Ordinal)
            || !string.Equals(persisted.ProjectId, supplied.ProjectId, StringComparison.Ordinal)
            || !string.Equals(persisted.WorkflowRunId, supplied.WorkflowRunId, StringComparison.Ordinal)
            || !string.Equals(persisted.TaskRunId, supplied.TaskRunId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Workflow Agent handoff grain key does not match the supplied command identity.");
        }
    }

    private void EnsurePrimaryKey(WorkflowAgentHandoffCommand command)
    {
        var expected = WorkflowAgentHandoffCodec.KeyFor(
            command.ProjectId,
            command.WorkflowRunId,
            command.TaskRunId,
            command.CommandId);
        if (!string.Equals(this.GetPrimaryKeyString(), expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Workflow Agent handoff grain key does not match the supplied command identity.");
        }
    }

    private static WorkflowAgentHandoffRejection? Validate(WorkflowAgentHandoffCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.CommandId)
            || string.IsNullOrWhiteSpace(command.ProjectId)
            || string.IsNullOrWhiteSpace(command.WorkflowRunId)
            || string.IsNullOrWhiteSpace(command.TaskRunId))
        {
            return new WorkflowAgentHandoffRejection(
                "invalid_handoff_identity",
                "Workflow Agent handoff requires command, project, workflow run, and task run identities.");
        }
        if (string.IsNullOrWhiteSpace(command.AgentRef)
            || string.IsNullOrWhiteSpace(command.Prompt))
        {
            return new WorkflowAgentHandoffRejection(
                "invalid_agent_input",
                "Workflow Agent handoff requires a non-empty Agent name and prompt.");
        }
        if (command.TimeoutMilliseconds is <= 0)
        {
            return new WorkflowAgentHandoffRejection(
                "invalid_agent_input",
                "Workflow Agent handoff timeout must be positive when supplied.");
        }
        return null;
    }

    private async Task EnsureActivationReminderAsync()
    {
        await this.RegisterOrUpdateReminder(
            ActivationReminderName,
            ActivationReminderDue,
            ActivationReminderPeriod);
    }

    private async Task UnregisterReminderAsync()
    {
        try
        {
            var reminder = await this.GetReminder(ActivationReminderName);
            if (reminder is not null)
                await this.UnregisterReminder(reminder);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex,
                "WorkflowAgentHandoff {Key} could not unregister orphan activation reminder",
                this.GetPrimaryKeyString());
        }
    }
}
