using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Mohist.Server.Agent.Services;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Infrastructure.Serialization;
using Mohist.Server.Runner.Domain;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Workspace.Grains;
using Orleans.Runtime;

namespace Mohist.Server.Agent.Grains;

/// <summary>
/// Authoritative owner of a standalone agent job's lifecycle, terminal
/// result, and dispatch ledger. State lives entirely on the
/// <c>AgentJobs</c> relational row; the grain hydrates from it on
/// activation and writes back through <see cref="IAgentJobStore"/> with
/// optimistic revision checking. There is no Orleans persistent state
/// for this grain — the relational row is the single durable source.
///
/// Admission writes the AgentJob ledger row directly with
/// <see cref="AgentJobLedgerRecord.AssignedRunnerId"/>,
/// <see cref="AgentJobLedgerRecord.ReadySince"/>, and
/// <see cref="AgentJobLedgerRecord.DispatchJson"/>; no
/// <see cref="IRunnerGrain"/> call, no <c>RunnerWorks</c> row. The
/// poll-time path claims the job via
/// <see cref="ClaimNextAsync"/>.
///
/// State machine: <c>Pending</c> → <c>Running</c> (claim) →
/// <c>Completed</c> or <c>Failed</c> (terminal). <c>Pending</c> →
/// <c>Failed</c> when the readiness deadline elapses without a
/// successful claim.
///
/// Every terminal transition persists a <see cref="PendingSessionClose"/>
/// payload (with a stable delivery id and the recorded timestamp) and
/// registers a durable <c>agent-job-recovery</c> Orleans reminder.
/// The reminder drives retries until acknowledgement so a process
/// restart, an activation loss, or a Session-persistence failure
/// cannot lose the terminal fact.
/// </summary>
public sealed partial class AgentJobGrain : Grain, IAgentJobGrain
{
    internal const string RecoveryReminderName = "agent-job-recovery";

    private static readonly TimeSpan RecoveryReminderDue = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RecoveryReminderPeriod = TimeSpan.FromSeconds(1);

    private readonly ILogger<AgentJobGrain> _log;
    private readonly AgentJobOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly IAgentJobStore _jobStore;
    private readonly IEventStore _eventStore;
    private readonly IBackgroundTaskLauncher _backgroundTasks;
    private readonly IGrainFactory _grains;
    private readonly IAgentJobDispatchObserver _dispatchObserver;
    private readonly TaskCompletionSource<AgentJobTerminalResult> _terminalCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IDisposable? _jobTimeoutTimer;

    private AgentJobState? _state;
    private AgentJobLedgerRecord? _ledger;
    private bool _hydrated;

    public AgentJobGrain(
        ILogger<AgentJobGrain> log,
        IOptions<AgentJobOptions> options,
        TimeProvider timeProvider,
        IAgentJobStore jobStore,
        IEventStore eventStore,
        IBackgroundTaskLauncher backgroundTasks,
        IGrainFactory grains,
        IAgentJobDispatchObserver dispatchObserver)
    {
        _log = log;
        _options = options.Value;
        _timeProvider = timeProvider;
        _jobStore = jobStore;
        _eventStore = eventStore;
        _backgroundTasks = backgroundTasks;
        _grains = grains;
        _dispatchObserver = dispatchObserver;
        _runnerLossRecoveryTimeout = ValidateRunnerLossRecoveryTimeout(_options.RunnerLossRecoveryTimeout);
    }

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        await HydrateAsync();

        if (IsTerminal && State.TerminalResult is not null)
            _terminalCompletion.TrySetResult(State.TerminalResult);

        _log.LogInformation("AgentJob {Id} OnActivateAsync: status={Status}, input={Input}, routedPlan={RoutedPlan}",
            Key, State.Status, State.Input is not null, State.RoutedPlan is not null);

        if (IsTerminal)
        {
            if (State.PendingSessionClose is not null
                || State.PendingFailureEvent is not null
                || State.PendingTerminalDeliveryEvent is not null
                || State.PendingSubagentTerminalEvent is not null
                || State.PendingUpdateInterruptionEvent is not null)
            {
                await EnsureRecoveryReminderAsync();
                if (State.PendingSessionClose is not null)
                    await DeliverTerminalToSessionAsync(State.PendingSessionClose);
                if (State.PendingFailureEvent is not null)
                    await EmitFailureEventAsync(State.PendingFailureEvent);
                if (State.PendingTerminalDeliveryEvent is not null)
                    await EmitTerminalDeliveryEventAsync(State.PendingTerminalDeliveryEvent);
                if (State.PendingSubagentTerminalEvent is not null)
                    await EmitSubagentTerminalEventAsync(State.PendingSubagentTerminalEvent);
                if (State.PendingUpdateInterruptionEvent is not null)
                    await EmitUpdateInterruptionEventAsync(State.PendingUpdateInterruptionEvent);
            }
            return;
        }

        if (State.Input is null && State.RoutedPlan is null)
            return;

        if (State.RoutedPlan is not null && State.Input is null && !State.RunnerAccepted)
        {
            await EnsureRecoveryReminderAsync();
            await AdvancePreparedLaunchAsync();
            return;
        }

        if (State.Status == AgentJobStatus.Running)
        {
            if (JobTimeoutExceeded())
            {
                await OnJobTimeoutAsync();
                return;
            }
            ArmJobTimeout();
            return;
        }

        if (State.Status == AgentJobStatus.Unknown)
        {
            if (await FailRecoveringJobIfDueAsync())
                return;

            if (EnsureUnknownInitialTurnDelivery(State.FailureReason ?? AgentJobFailureReasons.RunnerUnavailable))
            {
                await EnsureRecoveryReminderAsync();
                await PersistAsync();
            }
            else if (IsRecovering)
            {
                await EnsureRecoveryReminderAsync();
            }
            return;
        }

        if (State.Status == AgentJobStatus.RecoverablyInterrupted)
        {
            if (State.PendingUpdateInterruptionEvent is { } pending)
                await EmitUpdateInterruptionEventAsync(pending);
            if (State.PendingUpdateInterruptionEvent is null)
                await UnregisterSelfAsync(RecoveryReminderName);
            return;
        }

        if (State.Status == AgentJobStatus.Pending)
            await EvaluatePendingAsync();
    }

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken ct)
    {
        _jobTimeoutTimer?.Dispose();
        _jobTimeoutTimer = null;
        _state = null;
        _ledger = null;
        _hydrated = false;
        return Task.CompletedTask;
    }

    private string Key => this.GetPrimaryKeyString();
    private AgentJobState State => _state ?? throw new InvalidOperationException(
        $"AgentJob '{Key}' state accessed before hydration");
    private bool IsTerminal => State.Status is AgentJobStatus.Completed
        or AgentJobStatus.Failed
        or AgentJobStatus.Cancelled;

    private bool IsDispatchable => State.Status is AgentJobStatus.Pending;

    private bool IsReconcilable => State.Status is AgentJobStatus.Pending
        or AgentJobStatus.Running
        or AgentJobStatus.Unknown;

    public Task<AgentJobStatus> GetStatusAsync() => Task.FromResult(State.Status);

    public async Task<AgentJobCancelResult> CancelAsync()
    {
        await HydrateAsync();

        if (State.Status == AgentJobStatus.Pending)
        {
            await EnterTerminalStateAsync(
                AgentJobStatus.Cancelled,
                null,
                null,
                null,
                null,
                "cancelled",
                null,
                null,
                null);
            return new AgentJobCancelResult(AgentJobCancelDisposition.Cancelled, State.Status);
        }

        return new AgentJobCancelResult(
            State.Status == AgentJobStatus.Running
                ? AgentJobCancelDisposition.Executing
                : AgentJobCancelDisposition.AlreadyEnded,
            State.Status);
    }

    public Task<string?> GetCurrentWorkIdAsync() => Task.FromResult(State.WorkId);

    public Task<AgentJobRuntimeSnapshot> GetRuntimeSnapshotAsync() =>
        Task.FromResult(new AgentJobRuntimeSnapshot(
            State.Status,
            State.RunnerId,
            State.WorkId,
            State.FailureReason,
            State.RunnerAccepted,
            State.PendingSessionClose is not null,
            State.Input?.ProjectId ?? State.RoutedPlan?.ProjectId,
            ExecutionDefinitionFrom(State.Input),
            AgentSessionId: State.Input?.AgentSessionId ?? State.RoutedPlan?.SessionId,
            InitialInputId: State.Input?.InitialInputId,
            InitialTurnId: State.Input?.InitialTurnId,
            RecoveryDeadlineAt: State.RecoveryDeadlineAt,
            IsRecovering: IsRecovering));

    public Task<AgentJobTerminalResult> GetTerminalResultAsync()
    {
        if (State.TerminalResult is not null)
            return Task.FromResult(State.TerminalResult);

        return Task.FromResult(new AgentJobTerminalResult(
            State.Status, null, null, null, State.FailureReason, null));
    }

    public Task<AgentJobTerminalResult> WaitForTerminalAsync() =>
        IsTerminal
            ? Task.FromResult(State.TerminalResult!)
            : _terminalCompletion.Task;

    public Task<ClaimResult?> ClaimNextAsync(string runnerId) =>
        ClaimNextAsync(runnerId, null);

    public async Task<ClaimResult?> ClaimNextAsync(
        string runnerId,
        CapabilityClaimExpectation? expectation = null)
    {
        await HydrateAsync();

        if (string.IsNullOrWhiteSpace(runnerId))
            return null;

        // Validate the assignment under the row's revision. A concurrent
        // admission can move the AssignedRunnerId; in that case the
        // claim is skipped (the caller observes the new assignee on a
        // later poll).
        if (!string.Equals(State.RunnerId, runnerId, StringComparison.Ordinal))
            return null;
        if (State.Status != AgentJobStatus.Pending)
            return null;
        if (string.IsNullOrWhiteSpace(State.WorkId))
            return null;

        var pendingDispatch = DeserializeDispatch(_ledger?.DispatchJson);
        if (pendingDispatch is null)
            throw new AgentJobLedgerReconstructionException(
                $"AgentJob '{Key}' claim has no parseable dispatch snapshot");

        if (State.Input?.ReasoningEffort is not null && expectation is null)
            return null;

        if (expectation is not null
            && !MatchesCapabilityExpectation(expectation, pendingDispatch, runnerId))
        {
            return null;
        }

        var claimDispatch = expectation is null
            ? pendingDispatch
            : pendingDispatch with { CapabilityClaim = expectation };

        var record = expectation is null
            ? await _jobStore.ClaimAsync(Key, runnerId, _timeProvider.GetUtcNow())
            : await _jobStore.ClaimAsync(
                Key,
                runnerId,
                _timeProvider.GetUtcNow(),
                expectation.WorkId,
                JsonSerializer.Serialize(claimDispatch, JSON.Options));
        _hydrated = false;
        await HydrateAsync();
        var dispatch = DeserializeDispatch(record.DispatchJson)
            ?? throw new AgentJobLedgerReconstructionException(
                $"AgentJob '{Key}' claim returned a row without a parseable dispatch snapshot");

        ArmJobTimeout();
        await SafeRunnerAcceptedAsync(runnerId, State.WorkId!);
        if (State.ConcurrencyPermitHeld
            && State.ConcurrencyPermitId is not null
            && State.ConcurrencyDispatchId is not null
            && State.Input?.ProjectId is { } projectId
            && State.Input.AgentId is { } agentId)
        {
            await _grains.GetGrain<IAgentConcurrencyGrain>(GrainKey.Agent(projectId, agentId))
                .MarkExecutingAsync(
                    projectId,
                    agentId,
                    State.ConcurrencyPermitToken!,
                    State.ConcurrencyPermitId,
                    State.ConcurrencyDispatchId);
            State.ConcurrencyGateStatus = AgentConcurrencyPermitStatus.Executing;
            State.WaitingReason = null;
            await PersistAsync();
        }

        return new ClaimResult(Key, runnerId, State.WorkId!, dispatch);
    }

    public async Task<bool> RecordRuntimeSessionBindingAsync(
        string runnerId,
        string workId,
        string sessionId,
        string runtimeSessionId)
    {
        if (string.IsNullOrWhiteSpace(runtimeSessionId)
            || !string.Equals(State.RunnerId, runnerId, StringComparison.Ordinal)
            || !string.Equals(State.WorkId, workId, StringComparison.Ordinal)
            || !string.Equals(State.Input?.AgentSessionId, sessionId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(State.RuntimeSessionId))
            return string.Equals(State.RuntimeSessionId, runtimeSessionId, StringComparison.Ordinal);

        State.RuntimeSessionId = runtimeSessionId;
        await PersistAsync();
        return true;
    }

    public async Task<AgentJobReportResult> ReportResultAsync(string runnerId, string workId, WorkResult result)
    {
        await HydrateAsync();

        // A late report cannot win the recovery-deadline race merely because
        // the durable reminder has not executed yet. Terminalize the
        // recovering job first, then return Stale so the reporter retires its
        // journal entry.
        if (await FailRecoveringJobIfDueAsync())
            return new AgentJobReportResult(false, "stale");

        if (IsTerminal)
        {
            _log.LogDebug(
                "AgentJob {Id} rejecting report from {Runner} for {Work}: already in terminal {Status}",
                Key, runnerId, workId, State.Status);
            if (State.PendingSessionClose is not null)
                await DeliverTerminalToSessionAsync(State.PendingSessionClose);
            if (State.PendingTerminalDeliveryEvent is not null)
                await EmitTerminalDeliveryEventAsync(State.PendingTerminalDeliveryEvent);
            if (State.PendingSubagentTerminalEvent is not null)
                await EmitSubagentTerminalEventAsync(State.PendingSubagentTerminalEvent);
            return new AgentJobReportResult(false, "stale");
        }

        if (State.Status is not (AgentJobStatus.Running or AgentJobStatus.Unknown))
        {
            _log.LogWarning(
                "AgentJob {Id} rejecting report from {Runner} for {Work}: unexpected status {Status}",
                Key, runnerId, workId, State.Status);
            return new AgentJobReportResult(false, "not-running");
        }

        if (!string.Equals(State.RunnerId, runnerId, StringComparison.Ordinal)
            || !string.Equals(State.WorkId, workId, StringComparison.Ordinal))
        {
            _log.LogWarning(
                "AgentJob {Id} rejecting report from {Runner} for {Work}: expected {ExpectedRunner}/{ExpectedWork}",
                Key, runnerId, workId, State.RunnerId, State.WorkId);
            return new AgentJobReportResult(false, "runner-or-work-mismatch");
        }
        if (string.Equals(result.Status, "unknown", StringComparison.OrdinalIgnoreCase)) return await ReportUnknownResultAsync(result);
        var isSuccess = string.Equals(result.Status, "completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(result.Status, "pass", StringComparison.OrdinalIgnoreCase)
            || string.Equals(result.Status, "ok", StringComparison.OrdinalIgnoreCase)
            || string.Equals(result.Status, "success", StringComparison.OrdinalIgnoreCase);

        var failureReason = isSuccess
            ? null
            : (string.IsNullOrWhiteSpace(result.Message) ? result.Status : result.Message);

        var failureCategory = isSuccess
            ? null
            : FailureCategoryFromOutput(result.Output)
                ?? FailureCategoryFromErrorCode(result.ErrorCode)
                ?? FailureCategoryFromStatus(result.Status);

        await EnterTerminalStateAsync(
            isSuccess ? AgentJobStatus.Completed : AgentJobStatus.Failed,
            isSuccess ? (int?)0 : (result.ExitCode ?? 1),
            failureReason,
            failureCategory,
            failureReason,
            result.Message,
            result.Output?.ValueKind == System.Text.Json.JsonValueKind.Object || result.Output?.ValueKind == System.Text.Json.JsonValueKind.Array
                ? result.Output.Value.GetRawText()
                : null,
            result.ArtifactUploadIds,
            result.ExitCode);

        return new AgentJobReportResult(true);
    }

    public async Task FailAsync(string reason, string? agentId = null)
    {
        await HydrateAsync();

        if (IsTerminal)
        {
            return;
        }

        var resolvedAgentId = !string.IsNullOrWhiteSpace(agentId)
            ? agentId
            : State.Input?.AgentId ?? State.RoutedPlan?.AgentId;
        if (string.IsNullOrWhiteSpace(resolvedAgentId))
            throw new InvalidOperationException(
                $"AgentJob '{Key}' cannot fail without a resolved Agent identity");

        if (State.Input is null)
        {
            var plan = State.RoutedPlan;
            State.Input = new AgentJobInput(
                Prompt: plan?.Prompt ?? string.Empty,
                Model: plan?.Model,
                ProjectId: plan?.ProjectId,
                Runtime: plan?.Runtime,
                AgentId: resolvedAgentId,
                AgentSessionId: plan?.SessionId,
                Variant: plan?.Variant,
                ReasoningEffort: plan?.ReasoningEffort,
                IssueNumber: plan?.IssueNumber,
                EpicNumber: plan?.EpicNumber,
                WorkflowRunId: plan?.WorkflowRunId);
        }
        else if (string.IsNullOrWhiteSpace(State.Input.AgentId))
        {
            State.Input = State.Input with { AgentId = resolvedAgentId };
        }

        await EnterTerminalStateAsync(
            AgentJobStatus.Failed,
            null,
            reason,
            reason,
            reason,
            reason,
            null,
            null,
            null);
    }

    public async Task SubmitAsync(AgentJobInput input)
    {
        await HydrateAsync();

        if (State.Input is not null)
        {
            var existingInput = InputWithAgentConfig()!;
            if (EquivalentInput(existingInput, input))
            {
                if (!IsTerminal && State.Status == AgentJobStatus.Pending)
                    await TryAdmitAsync();
                return;
            }
            throw new InvalidOperationException(
                $"AgentJob '{Key}' cannot accept a different submission after it has started " +
                $"({DescribeInputDifferences(existingInput, input)})");
        }

        if (State.Status != AgentJobStatus.Pending)
            throw new InvalidOperationException(
                $"AgentJob '{Key}' cannot be re-submitted; current status is {State.Status}");

        if (input is null
            || (string.IsNullOrEmpty(input.Prompt)
                && (input.Attachments is null || input.Attachments.Count == 0)))
            throw new ArgumentException(
                "AgentJobInput.Prompt is required unless at least one attachment is accepted.",
                nameof(input));
        if (string.IsNullOrWhiteSpace(input.AgentId))
            throw new ArgumentException("AgentJobInput.AgentId is required", nameof(input));

        State.AgentConfigJson = SerializeAgentConfig(input.AgentConfig);
        State.Input = input with { AgentConfig = null };
        State.SubmittedAt = _timeProvider.GetUtcNow();
        await PersistAsync();
        await TryAdmitAsync();
    }

    public async Task EnsureSubmittedAsync(AgentJobInput input)
    {
        await HydrateAsync();

        if (State.Input is not null)
        {
            if (!IsTerminal && State.Status == AgentJobStatus.Pending)
                await TryAdmitAsync();
            return;
        }

        await SubmitAsync(input);
    }

    public async Task<RoutedAgentLaunchPlan> EnsurePreparedAsync(RoutedAgentLaunchPlan plan)
    {
        await HydrateAsync();
        ArgumentNullException.ThrowIfNull(plan);
        if (string.IsNullOrWhiteSpace(plan.AgentId))
            throw new ArgumentException("RoutedAgentLaunchPlan.AgentId is required", nameof(plan));

        if (State.RoutedPlan is { } existing)
        {
            await EnsureRecoveryReminderAsync();
            return existing;
        }

        await EnsureRecoveryReminderAsync();

        State.RoutedPlan = plan;
        await PersistAsync();
        return plan;
    }

    public async Task AdvancePreparedLaunchAsync()
    {
        await HydrateAsync();

        var plan = State.RoutedPlan;
        _log.LogInformation("AgentJob {Id} AdvancePreparedLaunchAsync: plan={Plan}, disposition={Disp}",
            Key, plan is not null, plan?.Disposition);
        if (plan is null)
            return;
        if (string.IsNullOrWhiteSpace(plan.AgentId))
            throw new InvalidOperationException(
                $"AgentJob '{Key}' cannot advance without a resolved Agent identity");

        if (State.RunnerAccepted)
        {
            await UnregisterSelfAsync(RecoveryReminderName);
            return;
        }

        if (IsTerminal)
        {
            return;
        }

        await EnsureRecoveryReminderAsync();

        var sessionGrain = GrainFactory.GetGrain<IAgentSessionGrain>(plan.SessionId);
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = plan.ProjectId,
            [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
            [GenericAgentSessionMetadata.AgentId] = plan.AgentId ?? string.Empty,
            [GenericAgentSessionMetadata.AgentName] = plan.AgentName ?? string.Empty,
            [GenericAgentSessionMetadata.TriggerEventId] = plan.EventId,
            [GenericAgentSessionMetadata.TriggerRuleId] = plan.RuleId,
            [GenericAgentSessionMetadata.Origin] = "event-router",
            [GenericAgentSessionMetadata.TargetId] = plan.AgentId ?? string.Empty,
        };
        if (plan.IssueNumber is > 0)
            labels[GenericAgentSessionMetadata.IssueNumber] = plan.IssueNumber.Value.ToString();
        if (plan.EpicNumber is > 0)
            labels[GenericAgentSessionMetadata.EpicNumber] = plan.EpicNumber.Value.ToString();
        if (!string.IsNullOrWhiteSpace(plan.WorkspacePath))
            labels["mohist.io/agent-launch/workspace-path"] = plan.WorkspacePath!;
        var metadata = new AgentSessionMetadata(labels, null);

        await sessionGrain.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: string.Empty,
            AgentRuntime: plan.Runtime ?? AgentConfigSchema.OpenCodeRuntime,
            WorkDir: plan.WorkspacePath,
            Metadata: metadata,
            Definition: new AgentExecutionDefinition(
                plan.AgentInstructions ?? string.Empty,
                plan.Runtime ?? AgentConfigSchema.OpenCodeRuntime,
                plan.Model,
                plan.Variant,
                plan.Skills ?? [],
                ReasoningEffort: plan.ReasoningEffort)));

        if (plan.Disposition == RoutedLaunchDisposition.PreflightFailed)
        {
            State.LaunchReady = true;
            State.AgentConfigJson = plan.AgentConfigJson;
            State.Input = new AgentJobInput(
                Prompt: plan.Prompt ?? string.Empty,
                Model: plan.Model,
                WorkspacePath: plan.WorkspacePath,
                ProjectId: plan.ProjectId,
                Runtime: plan.Runtime ?? AgentConfigSchema.OpenCodeRuntime,
                AgentId: plan.AgentId,
                AgentInstructions: plan.AgentInstructions,
                AgentConfig: null,
                AgentSessionId: plan.SessionId,
                Variant: plan.Variant,
                ReasoningEffort: plan.ReasoningEffort,
                IssueNumber: plan.IssueNumber,
                EpicNumber: plan.EpicNumber,
                WorkflowRunId: plan.WorkflowRunId,
                Skills: plan.Skills);
            await PersistAsync();

            var reason = plan.PreflightReason ?? AgentJobFailureReasons.WorkspaceUnavailable;
            var category = plan.PreflightCategory ?? AgentJobFailureReasons.WorkspaceUnavailable;
            await EnterTerminalStateAsync(
                AgentJobStatus.Failed,
                null,
                reason,
                category,
                reason,
                reason,
                null,
                null,
                null);
            return;
        }

        if (State.Input is null && !string.IsNullOrWhiteSpace(plan.Prompt))
        {
            var input = new AgentJobInput(
                Prompt: plan.Prompt!,
                Model: plan.Model,
                WorkspacePath: plan.WorkspacePath,
                ProjectId: plan.ProjectId,
                Runtime: plan.Runtime ?? AgentConfigSchema.OpenCodeRuntime,
                AgentId: plan.AgentId,
                AgentInstructions: plan.AgentInstructions,
                AgentConfig: DeserializeAgentConfig(plan.AgentConfigJson),
                AgentSessionId: plan.SessionId,
                Variant: plan.Variant,
                ReasoningEffort: plan.ReasoningEffort,
                IssueNumber: plan.IssueNumber,
                EpicNumber: plan.EpicNumber,
                WorkflowRunId: plan.WorkflowRunId,
                Skills: plan.Skills);
            State.AgentConfigJson = plan.AgentConfigJson;
            State.Input = input with { AgentConfig = null };
            State.SubmittedAt = _timeProvider.GetUtcNow();
        }

        if (!State.LaunchReady)
        {
            State.LaunchReady = true;
            await PersistAsync();
        }

        await TryAdmitAsync();
    }

    public async Task<AgentJobInput> PrepareManualLaunchAsync(PrepareManualLaunchCommand command)
    {
        await HydrateAsync();
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrEmpty(command.Prompt)
            && (command.Attachments is null || command.Attachments.Count == 0))
        {
            throw new ArgumentException(
                "Prompt is required unless at least one attachment is accepted.",
                nameof(command));
        }
        if (string.IsNullOrWhiteSpace(command.AgentId))
            throw new ArgumentException("AgentId is required.", nameof(command));
        if (string.IsNullOrWhiteSpace(command.SessionId))
            throw new ArgumentException("SessionId is required.", nameof(command));
        if (string.IsNullOrWhiteSpace(command.InputId))
            throw new ArgumentException("InputId is required.", nameof(command));
        if (string.IsNullOrWhiteSpace(command.TurnId))
            throw new ArgumentException("TurnId is required.", nameof(command));

        if (State.RoutedPlan is not null)
            throw new InvalidOperationException(
                $"AgentJob '{Key}' cannot accept a manual launch plan; routed launch already prepared.");

        if (State.ManualPlan is not null)
        {
            if (PlansEquivalent(State.ManualPlan, command))
            {
                return BuildManualInput(State.ManualPlan);
            }
            throw new InvalidOperationException(
                $"AgentJob '{Key}' already prepared a different manual launch plan.");
        }

        if (IsTerminal)
            throw new InvalidOperationException(
                $"AgentJob '{Key}' cannot accept a manual launch plan; already terminal.");

        if (State.Input is not null)
        {
            throw new InvalidOperationException(
                $"AgentJob '{Key}' already has input; manual launch preparation must be the first write.");
        }

        State.ManualPlan = command;
        State.LaunchVisibility = command.AgentSessionStartup?.ParentSessionId is null
            ? AgentLaunchVisibility.Visible
            : AgentLaunchVisibility.Provisional;
        var input = BuildManualInput(command);
        State.AgentConfigJson = SerializeAgentConfig(command.AgentConfig);
        State.Input = input with { AgentConfig = null };
        State.SubmittedAt = _timeProvider.GetUtcNow();
        await PersistAsync();
        await EnsureRecoveryReminderAsync();
        return input;
    }

    public async Task SubmitPreparedLaunchAsync()
    {
        await HydrateAsync();
        if (State.RoutedPlan is not null)
            throw new InvalidOperationException(
                $"AgentJob '{Key}' cannot submit a manual launch; routed launch path owns this job.");
        if (State.ManualPlan is null)
            throw new InvalidOperationException(
                $"AgentJob '{Key}' has no manual launch plan to submit.");
        if (State.Input is null)
            throw new InvalidOperationException(
                $"AgentJob '{Key}' manual launch plan has no input to submit.");
        if (State.LaunchVisibility != AgentLaunchVisibility.Visible)
            throw new InvalidOperationException("AgentJob launch is not visible and must not submit.");
        if (IsTerminal)
            return;
        if (!State.LaunchReady)
        {
            State.LaunchReady = true;
            await PersistAsync();
        }
        await TryAdmitAsync();
    }

    public async Task PromotePreparedLaunchAsync()
    {
        await HydrateAsync();
        if (State.LaunchVisibility == AgentLaunchVisibility.Rejected)
            throw new InvalidOperationException("Rejected AgentJob launch cannot be promoted.");
        if (State.LaunchVisibility == AgentLaunchVisibility.Visible)
            return;
        State.LaunchVisibility = AgentLaunchVisibility.Visible;
        await PersistAsync();
    }

    public async Task AbortPreparedLaunchAsync(string reason)
    {
        await HydrateAsync();
        if (State.ManualPlan is null && State.Input is null)
            return;
        if (State.LaunchVisibility == AgentLaunchVisibility.Rejected && IsTerminal)
            return;
        State.LaunchVisibility = AgentLaunchVisibility.Rejected;
        await EnterTerminalStateAsync(
            AgentJobStatus.Cancelled,
            exitCode: null,
            failureReason: reason,
            failureCategory: null,
            pendingReason: reason,
            message: "cancelled",
            output: null,
            artifactUploadIds: null,
            terminalExitCode: null);
    }

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (!string.Equals(reminderName, RecoveryReminderName, StringComparison.Ordinal))
            return;

        await HydrateAsync();

        if (IsTerminal)
        {
            await TryReleaseConcurrencyPermitAsync();
            if (State.PendingSessionClose is not null)
                await DeliverTerminalToSessionAsync(State.PendingSessionClose);
            if (State.PendingFailureEvent is not null)
                await EmitFailureEventAsync(State.PendingFailureEvent);
            if (State.PendingTerminalDeliveryEvent is not null)
                await EmitTerminalDeliveryEventAsync(State.PendingTerminalDeliveryEvent);
            if (State.PendingSubagentTerminalEvent is not null)
                await EmitSubagentTerminalEventAsync(State.PendingSubagentTerminalEvent);
            if (State.PendingUpdateInterruptionEvent is not null)
                await EmitUpdateInterruptionEventAsync(State.PendingUpdateInterruptionEvent);
            if (State.PendingSessionClose is null
                && State.PendingFailureEvent is null
                && State.PendingTerminalDeliveryEvent is null
                && State.PendingSubagentTerminalEvent is null
                && State.PendingUpdateInterruptionEvent is null
                && !State.ConcurrencyReleasePending)
            {
                await UnregisterSelfAsync(reminderName);
                return;
            }
            return;
        }

        if (State.Input is null && State.RoutedPlan is null)
        {
            await UnregisterSelfAsync(reminderName);
            return;
        }

        if (State.RoutedPlan is not null && State.Input is null && !State.RunnerAccepted)
        {
            await AdvancePreparedLaunchAsync();
            return;
        }

        if (State.Status == AgentJobStatus.Unknown)
        {
            if (State.PendingInitialTurnTerminalDelivery is { } pending)
                await DeliverInitialTurnTerminalAsync(pending);
            if (State.PendingInitialTurnTerminalDelivery is null)
                await UnregisterSelfAsync(reminderName);
            return;
        }

        if (State.Status == AgentJobStatus.RecoverablyInterrupted)
        {
            if (State.PendingUpdateInterruptionEvent is { } pending)
                await EmitUpdateInterruptionEventAsync(pending);
            if (State.PendingUpdateInterruptionEvent is null)
                await UnregisterSelfAsync(reminderName);
            return;
        }

        if (State.Status == AgentJobStatus.Pending)
        {
            await EvaluatePendingAsync();
            return;
        }

        // Non-terminal, non-prepared state: no recoverable obligation.
        await UnregisterSelfAsync(reminderName);
    }    private async Task UnregisterSelfAsync(string reminderName)
    {
        try
        {
            var reminder = await this.GetReminder(reminderName);
            if (reminder is not null)
                await this.UnregisterReminder(reminder);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex,
                "AgentJob {Id} could not self-unregister orphan reminder {Reminder}",
                Key, reminderName);
        }
    }

    private static string? FailureCategoryFromOutput(JsonElement? output)
    {
        if (output is not { ValueKind: JsonValueKind.Object } element) return null;
        return element.TryGetProperty("failureCategory", out var category)
            && category.ValueKind == JsonValueKind.String
            ? category.GetString()
            : null;
    }

    private static string? FailureCategoryFromErrorCode(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : code;

    private static string? FailureCategoryFromStatus(string? status) =>
        string.IsNullOrWhiteSpace(status) ? null : status;

    /// <summary>
    /// Reads the AgentJob ledger row and hydrates the in-memory state
    /// caches. Called on activation and after every state transition
    /// that flows through the store.
    /// </summary>
    private async Task HydrateAsync()
    {
        if (_hydrated && _ledger is not null)
            return;

        var record = await _jobStore.LoadLedgerAsync(Key);
        if (record is null)
        {
            _state = new AgentJobState();
            _ledger = null;
            _hydrated = true;
            return;
        }

        _ledger = record;
        _state = JsonSerializer.Deserialize<AgentJobState>(record.StateJson, JSON.Options) ?? new AgentJobState();
        // Backfill scheduling fields from the row so callers that read
        // state see the indexed values too.
        _state.RunnerId ??= record.AssignedRunnerId;
        _state.WorkId ??= record.WorkId;
        _state.SubmittedAt ??= record.ReadySince;
        _state.ReadySince ??= record.ReadySince;
        _state.RunningSince ??= record.RunningSince;
        if (Enum.TryParse<AgentLaunchVisibility>(record.LaunchVisibility, true, out var visibility))
            _state.LaunchVisibility = visibility;
        _hydrated = true;
    }

    /// <summary>
    /// Persists the current in-memory state back to the AgentJob ledger
    /// row. Optimistic revision check: if the row was updated
    /// concurrently the save throws <see cref="AgentJobLedgerConflictException"/>;
    /// the caller reloads via <see cref="HydrateAsync"/> and retries the
    /// idempotent command.
    /// </summary>
    private async Task PersistAsync()
    {
        var stateJson = JsonSerializer.Serialize(State, JSON.Options);
        var record = new AgentJobLedgerRecord(
            JobKey: Key,
            StateJson: stateJson,
            Revision: _ledger?.Revision ?? 0,
            AssignedRunnerId: State.RunnerId,
            WorkId: State.WorkId,
            ReadySince: ResolveReadySinceForPersist(),
            RunningSince: State.RunningSince,
            DispatchJson: ResolveDispatchJsonForPersist(),
            WorkType: State.RunnerId is null ? null : "agent-job",
            Stage: State.RunnerId is null ? null : "agent",
            Title: State.RunnerId is null ? null : "Agent Job",
            IssueProjectId: State.Input?.ProjectId,
            IssueNumber: State.Input?.IssueNumber,
            AgentSessionId: State.Input?.AgentSessionId,
            InitialInputId: State.Input?.InitialInputId,
            InitialTurnId: State.Input?.InitialTurnId,
            PinnedRunnerId: State.Input?.PinnedRunnerId,
            LaunchVisibility: State.LaunchVisibility.ToString().ToLowerInvariant(),
            TerminalLogOwnership: State.TerminalLogOwnership is null
                ? null
                : new TerminalLogOwnership(
                    State.TerminalLogOwnership.OwnerKind,
                    State.TerminalLogOwnership.OwnerId,
                    State.TerminalLogOwnership.WorkId,
                    State.TerminalLogOwnership.RunnerId));

        if (_ledger is null)
        {
            var inserted = await _jobStore.InsertLedgerAsync(record);
            _ledger = inserted;
        }
        else
        {
            try
            {
                var saved = await _jobStore.SaveLedgerAsync(record);
                _ledger = saved;
            }
            catch (AgentJobLedgerConflictException)
            {
                _hydrated = false;
                await HydrateAsync();
                throw;
            }
        }
    }

    /// <summary>
    /// Pending jobs without an AssignedRunnerId re-arm admission and
    /// clear the readiness timestamp on persist so the next admission
    /// resets the deadline. Pending jobs with an AssignedRunnerId
    /// preserve the timestamp the admission wrote; terminal jobs have
    /// no readiness projection.
    /// </summary>
    private DateTimeOffset? ResolveReadySinceForPersist()
    {
        if (State.Status != AgentJobStatus.Pending)
            return null;
        if (!string.IsNullOrWhiteSpace(State.RunnerId))
            return _ledger?.ReadySince ?? State.ReadySince;
        return null;
    }

    /// <summary>
    /// The dispatch snapshot is owned by the row. Subsequent saves
    /// (terminal transition, session close, etc.) must not clobber it
    /// with null.
    /// </summary>
    private string? ResolveDispatchJsonForPersist() => _ledger?.DispatchJson;

    private static string StableWorkId(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return $"agent-work-{Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant()}";
    }

    private void DisposeJobTimeoutTimer()
    {
        _jobTimeoutTimer?.Dispose();
        _jobTimeoutTimer = null;
    }
}
