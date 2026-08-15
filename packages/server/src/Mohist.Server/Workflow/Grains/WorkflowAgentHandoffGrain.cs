using Mohist.Server.Infrastructure;
using Mohist.Server.Workflow.Services;
using Orleans.Runtime;

namespace Mohist.Server.Workflow.Grains;

/// <summary>
/// Stores one rendered Workflow Agent handoff and its first preflight result.
/// It deliberately has no AgentJob, AgentSession, or Runner dependency: an
/// accepted handoff is only a durable authorization for a later activation
/// command, never evidence that execution has started.
/// </summary>
public sealed class WorkflowAgentHandoffGrain : Grain, IWorkflowAgentHandoffGrain
{
    private readonly IPersistentState<WorkflowAgentHandoffState> _state;
    private readonly IWorkflowAgentHandoffPreflight _preflight;
    private readonly TimeProvider _timeProvider;

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
            agent = await _preflight.ResolveAgentAsync(command.ProjectId, command.AgentRef);
            if (agent is null)
            {
                rejection = new WorkflowAgentHandoffRejection(
                    "agent_not_found",
                    $"Workflow Agent handoff references Agent '{command.AgentRef}' which does not exist or is archived.");
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
        };
        _state.State.Plan = plan;
        await _state.WriteStateAsync();
        return Result(plan, alreadyPersisted: false);
    }

    public Task<WorkflowAgentHandoffPlan?> GetPlanAsync() =>
        Task.FromResult(_state.State.Plan);

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
