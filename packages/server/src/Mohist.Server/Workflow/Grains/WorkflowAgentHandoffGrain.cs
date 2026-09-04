using System.Text.Json;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Orleans.Runtime;

namespace Mohist.Server.Workflow.Grains;

/// <summary>
/// Stores one rendered Workflow Agent handoff and its first preflight result.
/// It deliberately has no AgentJob, AgentSession, or Runner dependency: an
/// accepted handoff is only a durable authorization for a later activation
/// command, never evidence that execution has started.
/// </summary>
public sealed class WorkflowAgentHandoffGrain : Grain, IWorkflowAgentHandoffGrain, IRemindable
{
    private const string ActivationReminderName = "workflow-agent-activation";
    private readonly IPersistentState<WorkflowAgentHandoffState> _state;
    private readonly IWorkflowAgentHandoffPreflight _preflight;
    private readonly TimeProvider _timeProvider;
    private IDisposable? _activationTimer;

    public WorkflowAgentHandoffGrain(
        [PersistentState("workflow-agent-handoff")] IPersistentState<WorkflowAgentHandoffState> state,
        IWorkflowAgentHandoffPreflight preflight,
        TimeProvider timeProvider)
    {
        _state = state;
        _preflight = preflight;
        _timeProvider = timeProvider;
    }

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        if (!_state.RecordExists)
            await _state.ReadStateAsync();
        if (_state.State.Plan is not { Disposition: WorkflowAgentHandoffDisposition.Accepted } plan)
            return;
        if (plan.ActivationStep == WorkflowAgentActivationStep.Completed)
        {
            // A completed handoff must reactivate reminder-free; any reminder
            // left registered by an earlier activation is stale.
            await ClearActivationReminderAsync();
            return;
        }
        await EnsureActivationReminderAsync();
        ScheduleActivation();
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
        AgentExecutionIdentitySnapshot? agent = null;
        WorkflowAgentInvocation? invocation = null;
        if (rejection is null)
        {
            var preflight = await _preflight.ResolveAgentAsync(command.ProjectId, command.AgentRef);
            agent = preflight.Agent;
            if (agent is null)
            {
                rejection = new WorkflowAgentHandoffRejection(
                    preflight.ErrorCode ?? "agent_not_ready",
                    preflight.ErrorMessage ?? $"Workflow Agent handoff references Agent '{command.AgentRef}' which is not ready to execute.");
            }
            else if (string.IsNullOrWhiteSpace(agent.ExecutionDefinition.Runtime))
            {
                rejection = new WorkflowAgentHandoffRejection(
                    "agent_runtime_unavailable",
                    $"Workflow Agent handoff references Agent '{command.AgentRef}' without a usable runtime.");
                agent = null;
            }
            else
            {
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
            ExecutionDefinition: agent?.ExecutionDefinition,
            PreparedAt: _timeProvider.GetUtcNow(),
            Rejection: rejection,
            AgentId: agent?.AgentId);
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
            ActivationStep = WorkflowAgentActivationStep.PrepareJob,
            ActivationError = null,
        };
        _state.State.Plan = plan;
        await _state.WriteStateAsync();
        await EnsureActivationReminderAsync();
        ScheduleActivation();
        return Result(_state.State.Plan!, alreadyPersisted: false);
    }

    public async Task<WorkflowAgentHandoffResult> ActivateAsync()
    {
        var plan = _state.State.Plan
            ?? throw new InvalidOperationException("Workflow Agent handoff has not been prepared.");
        if (plan.Disposition != WorkflowAgentHandoffDisposition.Accepted)
            return Result(plan, alreadyPersisted: true);
        // Reminder registration is deliberately not repeated here: a
        // reactivation or reconcile call after ActivationStep.Completed must
        // not arm a new recurring reminder for work that no longer exists.
        await AdvanceActivationAsync();
        return Result(_state.State.Plan!, alreadyPersisted: true);
    }

    public Task TriggerActivationAsync()
    {
        ScheduleActivation();
        return Task.CompletedTask;
    }

    public Task<WorkflowAgentHandoffPlan?> GetPlanAsync() =>
        Task.FromResult(_state.State.Plan);

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (!string.Equals(reminderName, ActivationReminderName, StringComparison.Ordinal))
            return;
        await AdvanceActivationAsync();
    }

    private async Task AdvanceActivationAsync()
    {
        var plan = _state.State.Plan;
        if (plan is null
            || plan.Disposition != WorkflowAgentHandoffDisposition.Accepted)
            return;
        if (plan.ActivationStep == WorkflowAgentActivationStep.Completed)
        {
            // Release reminders that survived past completion (e.g. a leak
            // from an earlier activation) instead of ticking forever.
            await ClearActivationReminderAsync();
            return;
        }

        try
        {
            while (plan.ActivationStep != WorkflowAgentActivationStep.Completed)
            {
                var invocation = plan.Invocation
                    ?? throw new InvalidOperationException("Accepted Workflow Agent handoff has no invocation.");
                var definition = plan.ExecutionDefinition
                    ?? throw new InvalidOperationException("Accepted Workflow Agent handoff has no execution definition.");
                switch (plan.ActivationStep)
                {
                    case WorkflowAgentActivationStep.PrepareJob:
                        await GrainFactory.GetGrain<IAgentJobGrain>(invocation.JobKey)
                            .PrepareManualLaunchAsync(BuildPrepareCommand(plan, invocation, definition));
                        plan = plan with
                        {
                            ActivationStep = WorkflowAgentActivationStep.EnsureSession,
                            ActivationError = null,
                        };
                        break;
                    case WorkflowAgentActivationStep.EnsureSession:
                        await EnsureSessionAsync(plan, invocation, definition);
                        plan = plan with
                        {
                            ActivationStep = WorkflowAgentActivationStep.SubmitJob,
                            ActivationError = null,
                        };
                        break;
                    case WorkflowAgentActivationStep.SubmitJob:
                        await GrainFactory.GetGrain<IAgentJobGrain>(invocation.JobKey).SubmitPreparedLaunchAsync();
                        plan = plan with
                        {
                            ActivationStep = WorkflowAgentActivationStep.Completed,
                            ActivationError = null,
                        };
                        break;
                    default:
                        throw new InvalidOperationException($"Invalid Workflow Agent activation step '{plan.ActivationStep}'.");
                }
                _state.State.Plan = plan;
                await _state.WriteStateAsync();
            }
            await ClearActivationReminderAsync();
        }
        catch (Exception ex)
        {
            _state.State.Plan = plan with { ActivationError = ex.Message };
            await _state.WriteStateAsync();
            await EnsureActivationReminderAsync();
        }
    }

    private static PrepareManualLaunchCommand BuildPrepareCommand(
        WorkflowAgentHandoffPlan plan,
        WorkflowAgentInvocation invocation,
        AgentExecutionDefinition definition)
    {
        var workspace = plan.Command.Completion?.Workspace;
        return new PrepareManualLaunchCommand(
            SessionId: invocation.SessionId,
            InputId: invocation.InputId,
            TurnId: invocation.TurnId,
            Prompt: plan.Command.Prompt,
            Model: definition.Model,
            WorkspaceName: workspace?.Name,
            WorkspacePath: workspace?.Identity?.Path,
            ProjectId: plan.Command.ProjectId,
            Runtime: definition.Runtime,
            AgentId: plan.AgentId,
            AgentInstructions: definition.Instructions,
            AgentConfig: AgentConfig(definition),
            Variant: definition.Variant,
            IssueNumber: plan.Command.Completion?.IssueNumber,
            EpicNumber: plan.Command.Completion?.EpicNumber,
            WorkflowRunId: plan.Command.WorkflowRunId,
            AllowedSubagents: definition.AllowedSubagents,
            ReasoningEffort: definition.ReasoningEffort,
            Skills: definition.Skills.ToArray(),
            WorkspaceRepositories: workspace?.Repositories,
            TimeoutMilliseconds: plan.Command.TimeoutMilliseconds,
            WorkflowOrigin: new WorkflowAgentJobOrigin(
                invocation.InvocationId,
                invocation.CommandId,
                invocation.WorkflowRunId,
                invocation.ActionAttemptId,
                plan.Command.Completion!.WorkId,
                plan.Command.Completion.Stage,
                plan.RequestFingerprint,
                plan.Command.Completion.ExpectJson,
                SerializeOrNull(plan.Command.Completion.Artifacts),
                SerializeOrNull(plan.Command.Completion.SetVars),
                SerializeOrNull(plan.Command.Completion.Recovery),
                plan.Command.Completion.RecoveryRemaining,
                plan.Command.Completion.VariablesJson));
    }

    private async Task EnsureSessionAsync(
        WorkflowAgentHandoffPlan plan,
        WorkflowAgentInvocation invocation,
        AgentExecutionDefinition definition)
    {
        var session = GrainFactory.GetGrain<IAgentSessionGrain>(invocation.SessionId);
        if (!string.IsNullOrWhiteSpace(plan.Command.ReuseSessionId))
        {
            var accepted = await session.AcceptFollowupAsync(new AcceptFollowupCommand(
                Text: plan.Command.Prompt,
                Source: "workflow",
                IdempotencyKey: invocation.CommandId,
                PreMintedInputId: invocation.InputId,
                PreMintedTurnId: invocation.TurnId,
                AllowPendingInitialLaunch: true));
            if (!string.Equals(accepted.InputId, invocation.InputId, StringComparison.Ordinal)
                || !string.Equals(accepted.TurnId, invocation.TurnId, StringComparison.Ordinal))
                throw new InvalidOperationException("Workflow named Session replay resolved conflicting Input or Turn identity.");
            return;
        }
        await session.EnsureInitialLaunchAsync(BuildSessionCommand(plan, invocation, definition));
    }

    private static EnsureInitialLaunchCommand BuildSessionCommand(
        WorkflowAgentHandoffPlan plan,
        WorkflowAgentInvocation invocation,
        AgentExecutionDefinition definition)
    {
        var completion = plan.Command.Completion!;
        var sessionName = string.IsNullOrWhiteSpace(plan.Command.Session)
            ? invocation.InvocationId
            : plan.Command.Session!;
        return new EnsureInitialLaunchCommand(
            InputId: invocation.InputId,
            TurnId: invocation.TurnId,
            Prompt: plan.Command.Prompt,
            Source: "workflow",
            JobId: invocation.JobKey,
            Metadata: WorkflowAgentSessionMetadata.Metadata(new WorkflowAgentSessionContext(
                plan.Command.ProjectId,
                plan.Command.WorkflowRunId,
                sessionName,
                WorkId: completion.WorkId,
                WorkType: WorkItemTypes.Task,
                Stage: completion.Stage,
                Title: plan.Command.ActionAttemptId)),
            Runtime: definition.Runtime,
            WorkDir: completion.Workspace?.Identity?.Path,
            Definition: definition,
            LaunchVisibility: AgentLaunchVisibility.Visible);
    }

    private static string? SerializeOrNull<T>(T? value) =>
        value is null ? null : JSON.Serialize(value);

    private static JsonElement AgentConfig(AgentExecutionDefinition definition)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["runtime"] = definition.Runtime,
            ["model"] = definition.Model,
            ["variant"] = definition.Variant,
            ["reasoningEffort"] = definition.ReasoningEffort,
        };
        return JsonSerializer.SerializeToElement(values.Where(pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
    }

    private void ScheduleActivation()
    {
        if (_activationTimer is not null) return;
        try
        {
            _activationTimer = this.RegisterGrainTimer(
                async _ =>
                {
                    try
                    {
                        await AdvanceActivationAsync();
                    }
                    finally
                    {
                        _activationTimer?.Dispose();
                        _activationTimer = null;
                    }
                },
                TimeSpan.FromMilliseconds(10),
                Timeout.InfiniteTimeSpan);
        }
        catch (InvalidOperationException)
        {
            // Direct grain contract tests construct the grain without an Orleans runtime.
            // Activation remains explicitly driveable through ActivateAsync in that seam.
        }
    }

    private async Task EnsureActivationReminderAsync() =>
        await this.RegisterOrUpdateReminder(
            ActivationReminderName,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMinutes(1));

    private async Task ClearActivationReminderAsync()
    {
        var reminder = await this.GetReminder(ActivationReminderName);
        if (reminder is not null)
            await this.UnregisterReminder(reminder);
    }

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
            || !string.Equals(persisted.Completion?.Stage, supplied.Completion?.Stage, StringComparison.Ordinal)
            || !string.Equals(persisted.ActionAttemptId, supplied.ActionAttemptId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Workflow Agent handoff grain key does not match the supplied command identity.");
        }
    }

    private void EnsurePrimaryKey(WorkflowAgentHandoffCommand command)
    {
        var expected = WorkflowAgentHandoffCodec.KeyFor(command);
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
            || string.IsNullOrWhiteSpace(command.ActionAttemptId))
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
        if (command.Completion is null
            || string.IsNullOrWhiteSpace(command.Completion.WorkId)
            || string.IsNullOrWhiteSpace(command.Completion.Stage))
        {
            return new WorkflowAgentHandoffRejection(
                "invalid_completion_snapshot",
                "Workflow Agent handoff requires a work id and stage in its completion snapshot.");
        }
        if (command.Completion.ExpectJson is { } expectJson)
        {
            try
            {
                using var _ = System.Text.Json.JsonDocument.Parse(expectJson);
            }
            catch (System.Text.Json.JsonException)
            {
                return new WorkflowAgentHandoffRejection(
                    "invalid_completion_snapshot",
                    "Workflow Agent handoff completion expect must be valid rendered JSON when supplied.");
            }
        }
        return null;
    }
}
