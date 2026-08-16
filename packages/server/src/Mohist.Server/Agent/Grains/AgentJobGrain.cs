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
                || State.PendingSubagentTerminalEvent is not null)
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
            || (string.IsNullOrWhiteSpace(input.Prompt)
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
        if (string.IsNullOrWhiteSpace(command.Prompt)
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

    private bool EnsureUnknownInitialTurnDelivery(string reason)
    {
        var sessionId = State.Input?.AgentSessionId;
        if (string.IsNullOrWhiteSpace(sessionId)
            || string.IsNullOrWhiteSpace(State.Input?.InitialTurnId))
            return false;

        if (State.PendingInitialTurnTerminalDelivery is not null)
            return false;

        State.PendingInitialTurnTerminalDelivery = new PendingInitialTurnTerminalDelivery(
            AgentJobSessionDeliveryIds.InitialTurnUnknownDeliveryId(Key),
            sessionId,
            State.Input!.InitialTurnId!,
            AgentTurnStatus.Unknown,
            new AgentTurnResult(
                Message: reason,
                Output: null,
                FailureReason: reason,
                FailureCategory: "unknown",
                ExitCode: null));
        return true;
    }

    private async Task DeliverInitialTurnTerminalAsync(PendingInitialTurnTerminalDelivery pending)
    {
        try
        {
            var sessionGrain = _grains.GetGrain<IAgentSessionGrain>(pending.SessionId);
            await sessionGrain.MarkInitialTurnTerminalAsync(
                Key,
                pending.Status,
                pending.Result);
            State.PendingInitialTurnTerminalDelivery = null;
            await PersistAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "AgentJob {Id} could not deliver initial-turn verdict to session {SessionId}; deliveryId={DeliveryId} retained for retry",
                Key, pending.SessionId, pending.DeliveryId);
        }
    }

    private static AgentJobInput BuildManualInput(PrepareManualLaunchCommand command) =>
        new(
            Prompt: command.Prompt,
            Model: command.Model,
            WorkspaceName: command.WorkspaceName,
            WorkspacePath: command.WorkspacePath,
            ProjectId: command.ProjectId,
            Runtime: command.Runtime ?? AgentConfigSchema.OpenCodeRuntime,
            AgentId: command.AgentId,
            AgentInstructions: string.IsNullOrWhiteSpace(command.AgentInstructions) ? null : command.AgentInstructions,
            AgentConfig: command.AgentConfig,
            AgentSessionId: command.SessionId,
            Variant: command.Variant,
            ReasoningEffort: command.ReasoningEffort,
            IssueNumber: command.IssueNumber,
            EpicNumber: command.EpicNumber,
            WorkflowRunId: command.WorkflowRunId,
            InitialInputId: command.InputId,
            InitialTurnId: command.TurnId,
            Attachments: command.Attachments,
            StartupContext: command.StartupContext,
            SlackExecutionContext: SlackExecutionContextFor(command),
            AllowedSubagents: command.AllowedSubagents,
            PinnedRunnerId: command.PinnedRunnerId,
            AgentSessionStartup: command.AgentSessionStartup,
            SpawnOrigin: command.SpawnOrigin,
            WorkspaceRepositories: command.WorkspaceRepositories);

    private static AgentSlackExecutionContext? SlackExecutionContextFor(PrepareManualLaunchCommand command)
    {
        var origin = command.ConnectionOrigin;
        return origin is null
            ? null
            : SlackExecutionContextFactory.Create(
                origin.WorkspaceTeamId,
                origin.ConversationId,
                origin.ThreadTs,
                origin.MessageTs,
                origin.SlackUserId,
                origin.ConnectionId,
                command.SessionId,
                $"slack:{command.SessionId}:{command.InputId}");
    }

    private static bool PlansEquivalent(PrepareManualLaunchCommand left, PrepareManualLaunchCommand right) =>
        string.Equals(left.Prompt, right.Prompt, StringComparison.Ordinal)
        && string.Equals(left.Model, right.Model, StringComparison.Ordinal)
        && string.Equals(left.ReasoningEffort, right.ReasoningEffort, StringComparison.Ordinal)
        && string.Equals(left.WorkspaceName, right.WorkspaceName, StringComparison.Ordinal)
        && string.Equals(left.WorkspacePath, right.WorkspacePath, StringComparison.Ordinal)
        && string.Equals(left.ProjectId, right.ProjectId, StringComparison.Ordinal)
        && string.Equals(left.Runtime ?? string.Empty, right.Runtime ?? string.Empty, StringComparison.Ordinal)
        && string.Equals(left.AgentId, right.AgentId, StringComparison.Ordinal)
        && string.Equals(left.AgentInstructions ?? string.Empty, right.AgentInstructions ?? string.Empty, StringComparison.Ordinal)
        && string.Equals(left.SessionId, right.SessionId, StringComparison.Ordinal)
        && string.Equals(left.InputId, right.InputId, StringComparison.Ordinal)
        && string.Equals(left.TurnId, right.TurnId, StringComparison.Ordinal)
        && string.Equals(left.Variant ?? string.Empty, right.Variant ?? string.Empty, StringComparison.Ordinal)
        && left.IssueNumber == right.IssueNumber
        && left.EpicNumber == right.EpicNumber
        && string.Equals(left.WorkflowRunId ?? string.Empty, right.WorkflowRunId ?? string.Empty, StringComparison.Ordinal)
        && JsonEquals(left.AgentConfig, right.AgentConfig)
        && AttachmentDescriptorsEquivalent(left.Attachments, right.Attachments);

    private static bool AttachmentDescriptorsEquivalent(
        IReadOnlyList<AgentSessionInputAttachmentDescriptor>? left,
        IReadOnlyList<AgentSessionInputAttachmentDescriptor>? right)
    {
        var leftCount = left?.Count ?? 0;
        var rightCount = right?.Count ?? 0;
        if (leftCount != rightCount) return false;
        if (leftCount == 0) return true;
        for (var index = 0; index < leftCount; index++)
        {
            var a = left![index];
            var b = right![index];
            if (!string.Equals(a.Id, b.Id, StringComparison.Ordinal)) return false;
            if (!string.Equals(a.OriginalFileName, b.OriginalFileName, StringComparison.Ordinal)) return false;
            if (!string.Equals(a.ContentType, b.ContentType, StringComparison.Ordinal)) return false;
            if (a.Size != b.Size) return false;
        }
        return true;
    }

    private static System.Text.Json.JsonElement? DeserializeAgentConfig(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? null
            : System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();

    private static AgentExecutionDefinition? ExecutionDefinitionFrom(AgentJobInput? input) =>
        input is null || string.IsNullOrWhiteSpace(input.AgentId)
            ? null
            : new AgentExecutionDefinition(
                input.AgentInstructions ?? string.Empty,
                input.Runtime ?? AgentConfigSchema.OpenCodeRuntime,
                input.Model,
                input.Variant,
                input.Skills ?? [],
                input.AllowedSubagents,
                input.ReasoningEffort);

    private static bool EquivalentInput(AgentJobInput left, AgentJobInput right) =>
        string.Equals(left.Prompt, right.Prompt, StringComparison.Ordinal)
        && string.Equals(left.Model, right.Model, StringComparison.Ordinal)
        && string.Equals(left.WorkspaceName, right.WorkspaceName, StringComparison.Ordinal)
        && string.Equals(left.WorkspacePath, right.WorkspacePath, StringComparison.Ordinal)
        && string.Equals(left.ProjectId, right.ProjectId, StringComparison.Ordinal)
        && string.Equals(left.Runtime, right.Runtime, StringComparison.Ordinal)
        && string.Equals(left.AgentId, right.AgentId, StringComparison.Ordinal)
        && string.Equals(left.AgentInstructions, right.AgentInstructions, StringComparison.Ordinal)
        && string.Equals(left.AgentSessionId, right.AgentSessionId, StringComparison.Ordinal)
        && string.Equals(left.Variant, right.Variant, StringComparison.Ordinal)
        && string.Equals(left.ReasoningEffort, right.ReasoningEffort, StringComparison.Ordinal)
        && JsonEquals(left.AgentConfig, right.AgentConfig)
        && AttachmentDescriptorsEquivalent(left.Attachments, right.Attachments)
        && Equals(left.StartupContext, right.StartupContext)
        && Equals(left.AllowedSubagents, right.AllowedSubagents)
        && string.Equals(left.PinnedRunnerId, right.PinnedRunnerId, StringComparison.Ordinal)
        && Equals(left.AgentSessionStartup, right.AgentSessionStartup)
        && Equals(left.SlackExecutionContext, right.SlackExecutionContext);

    private static string DescribeInputDifferences(AgentJobInput left, AgentJobInput right)
    {
        var fields = new List<string>();
        if (!string.Equals(left.Prompt, right.Prompt, StringComparison.Ordinal)) fields.Add(nameof(AgentJobInput.Prompt));
        if (!string.Equals(left.Model, right.Model, StringComparison.Ordinal)) fields.Add(nameof(AgentJobInput.Model));
        if (!string.Equals(left.WorkspaceName, right.WorkspaceName, StringComparison.Ordinal)) fields.Add(nameof(AgentJobInput.WorkspaceName));
        if (!string.Equals(left.WorkspacePath, right.WorkspacePath, StringComparison.Ordinal)) fields.Add(nameof(AgentJobInput.WorkspacePath));
        if (!string.Equals(left.ProjectId, right.ProjectId, StringComparison.Ordinal)) fields.Add(nameof(AgentJobInput.ProjectId));
        if (!string.Equals(left.Runtime, right.Runtime, StringComparison.Ordinal)) fields.Add(nameof(AgentJobInput.Runtime));
        if (!string.Equals(left.AgentId, right.AgentId, StringComparison.Ordinal)) fields.Add(nameof(AgentJobInput.AgentId));
        if (!string.Equals(left.AgentInstructions, right.AgentInstructions, StringComparison.Ordinal)) fields.Add(nameof(AgentJobInput.AgentInstructions));
        if (!string.Equals(left.AgentSessionId, right.AgentSessionId, StringComparison.Ordinal)) fields.Add(nameof(AgentJobInput.AgentSessionId));
        if (!string.Equals(left.Variant, right.Variant, StringComparison.Ordinal)) fields.Add(nameof(AgentJobInput.Variant));
        if (!string.Equals(left.ReasoningEffort, right.ReasoningEffort, StringComparison.Ordinal)) fields.Add(nameof(AgentJobInput.ReasoningEffort));
        if (!JsonEquals(left.AgentConfig, right.AgentConfig)) fields.Add(nameof(AgentJobInput.AgentConfig));
        if (!AttachmentDescriptorsEquivalent(left.Attachments, right.Attachments)) fields.Add(nameof(AgentJobInput.Attachments));
        if (!Equals(left.AllowedSubagents, right.AllowedSubagents)) fields.Add(nameof(AgentJobInput.AllowedSubagents));
        if (!string.Equals(left.PinnedRunnerId, right.PinnedRunnerId, StringComparison.Ordinal)) fields.Add(nameof(AgentJobInput.PinnedRunnerId));
        if (!Equals(left.AgentSessionStartup, right.AgentSessionStartup)) fields.Add(nameof(AgentJobInput.AgentSessionStartup));
        if (!Equals(left.SlackExecutionContext, right.SlackExecutionContext)) fields.Add(nameof(AgentJobInput.SlackExecutionContext));
        return string.Join(", ", fields);
    }

    private static bool JsonEquals(JsonElement? left, JsonElement? right)
    {
        var hasLeft = left is { ValueKind: not JsonValueKind.Undefined };
        var hasRight = right is { ValueKind: not JsonValueKind.Undefined };
        if (!hasLeft || !hasRight)
            return hasLeft == hasRight;
        return JsonElement.DeepEquals(left!.Value, right!.Value);
    }

    private AgentJobInput? InputWithAgentConfig() =>
        State.Input is null
            ? null
            : State.Input with
            {
                AgentConfig = string.IsNullOrWhiteSpace(State.AgentConfigJson)
                    ? null
                    : JSON.DeserializeElement(State.AgentConfigJson),
            };

    private static string? SerializeAgentConfig(JsonElement? config) =>
        config is { ValueKind: not JsonValueKind.Undefined } element
            ? element.GetRawText()
            : null;

    public async Task CheckTimeoutsAsync()
    {
        await HydrateAsync();

        if (State.Status == AgentJobStatus.Pending)
        {
            await EvaluatePendingAsync();
            return;
        }

        if (State.Status == AgentJobStatus.Running && JobTimeoutExceeded())
        {
            await OnJobTimeoutAsync();
        }
    }

    private async Task TryAdmitAsync()
    {
        if (State.LaunchVisibility != AgentLaunchVisibility.Visible)
            return;
        if (State.Status != AgentJobStatus.Pending || State.Input is null || State.SubmittedAt is null)
            return;

        // If the row already carries a dispatch snapshot (a previous
        // admission succeeded), the next claim race is owned by the
        // poll path. Re-admitting here would clobber ReadySince and
        // extend the deadline; only re-admit when no runner was found.
        var pinnedRunnerId = State.Input.PinnedRunnerId;
        if (!string.IsNullOrWhiteSpace(State.RunnerId)
            && !string.IsNullOrWhiteSpace(_ledger?.DispatchJson)
            && !string.IsNullOrWhiteSpace(State.WorkId))
        {
            if (!string.IsNullOrWhiteSpace(pinnedRunnerId)
                && !string.Equals(State.RunnerId, pinnedRunnerId, StringComparison.Ordinal))
            {
                State.RunnerId = null;
                State.WorkId = null;
                State.RunnerAccepted = false;
                State.RunningSince = null;
                State.ReadySince = null;
                await PersistAsync();
            }
            else
            {
            var assignedRunner = GrainFactory.GetGrain<IRunnerGrain>(State.RunnerId);
            if ((await assignedRunner.GetRuntimeStateAsync()).Status == RunnerStatus.Online)
                return;

            State.RunnerId = null;
            State.RunnerAccepted = false;
            State.RunningSince = null;
            State.ReadySince = null;
            await PersistAsync();
            }
        }

        if (!await AcquireConcurrencyPermitAsync())
            return;

        State.WaitingReason = null;
        await PersistAsync();

        var projectId = State.Input.ProjectId ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(pinnedRunnerId))
        {
            State.WaitingReason = null;
            if (!await TryAdmitOnRunnerAsync(pinnedRunnerId))
            {
                State.WaitingReason = AgentAvailabilityWaitReasons.NoOnlineRunner;
                await ReleaseConcurrencyPermitAsync();
                await PersistAsync();
            }
            return;
        }

        var registry = GrainFactory.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        var runners = await registry.ListEligibleRunnersAsync(projectId);
        if (runners.Count == 0)
        {
            State.WaitingReason = AgentAvailabilityWaitReasons.NoOnlineRunner;
            await ReleaseConcurrencyPermitAsync();
            return;
        }

        // Workspace affinity: a bound job routes to the workspace's home
        // runner first. A stale home (runner offline) is cleared and the
        // job falls back to the generic election; the runner that wins
        // materializes the workspace and reports the new home.
        if (!string.IsNullOrWhiteSpace(State.Input.WorkspaceName)
            && !string.IsNullOrWhiteSpace(State.Input.ProjectId))
        {
            var workspace = GrainFactory.GetGrain<IWorkspaceGrain>(
                GrainKey.Workspace(State.Input.ProjectId, State.Input.WorkspaceName));
            var home = await workspace.GetHomeAsync();
            if (home is not null)
            {
                var homeRunner = GrainFactory.GetGrain<IRunnerGrain>(home.RunnerId);
                var homeState = await homeRunner.GetRuntimeStateAsync();
                if (homeState.Status == RunnerStatus.Online
                    && await TryAdmitOnRunnerAsync(home.RunnerId))
                {
                    return;
                }

                if (homeState.Status != RunnerStatus.Online)
                    await workspace.ClearHomeIfAsync(home.RunnerId);
            }
        }

        foreach (var runnerInfo in runners)
        {
            if (await TryAdmitOnRunnerAsync(runnerInfo.RunnerId))
                return;
        }

        State.WaitingReason = AgentAvailabilityWaitReasons.CapacityFull;
        await ReleaseConcurrencyPermitAsync();
        await PersistAsync();
    }

    private async Task<bool> AcquireConcurrencyPermitAsync()
    {
        if (State.Input is null)
            return false;

        var projectId = State.Input.ProjectId;
        var agentId = State.Input.AgentId;
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(agentId))
            return true;

        var token = State.ConcurrencyPermitToken ??= $"{Key}:execution";
        var dispatchId = State.ConcurrencyDispatchId ??= $"job:{Key}";
        var gate = _grains.GetGrain<IAgentConcurrencyGrain>(GrainKey.Agent(projectId, agentId));
        var result = await gate.AcquireAsync(
            projectId,
            agentId,
            token,
            Key,
            AgentConcurrencyPermitOwnerKind.Job,
            dispatchId);
        if (result == AgentConcurrencyAcquireResult.Waiting)
        {
            var waiter = (await gate.GetSnapshotAsync()).Waiters.FirstOrDefault(candidate =>
                string.Equals(candidate.Token, token, StringComparison.Ordinal)
                && string.Equals(candidate.OwnerId, Key, StringComparison.Ordinal));
            State.ConcurrencyPermitHeld = false;
            State.ConcurrencyPermitId = null;
            State.ConcurrencyWaiterId = waiter?.WaiterId;
            State.ConcurrencyGeneration = waiter?.Generation ?? State.ConcurrencyGeneration;
            State.ConcurrencyGateStatus = AgentConcurrencyPermitStatus.DispatchPending;
            State.WaitingReason = AgentAvailabilityWaitReasons.CapacityFull;
            await PersistAsync();
            return false;
        }

        var permit = await gate.GetPermitAsync(token);
        State.ConcurrencyPermitHeld = permit is not null;
        State.ConcurrencyPermitId = permit?.PermitId;
        State.ConcurrencyWaiterId = null;
        State.ConcurrencyGeneration = permit?.Generation ?? 0;
        State.ConcurrencyDispatchId = permit?.DispatchId ?? dispatchId;
        State.ConcurrencyGateStatus = AgentConcurrencyPermitStatus.DispatchPending;
        State.WaitingReason = AgentAvailabilityWaitReasons.DispatchPending;
        await PersistAsync();
        if (permit is not null)
            await gate.ConfirmDispatchPendingAsync(projectId, agentId, token, permit.PermitId!, permit.DispatchId!);
        return true;
    }

    public async Task ConcurrencyPermitGrantedAsync(
        string? token = null,
        string? permitId = null,
        string? dispatchId = null)
    {
        await HydrateAsync();
        if (token is not null
            && !string.Equals(State.ConcurrencyPermitToken, token, StringComparison.Ordinal))
            return;
        if (permitId is not null
            && State.ConcurrencyPermitId is not null
            && !string.Equals(State.ConcurrencyPermitId, permitId, StringComparison.Ordinal))
            return;
        if (dispatchId is not null
            && State.ConcurrencyDispatchId is not null
            && !string.Equals(State.ConcurrencyDispatchId, dispatchId, StringComparison.Ordinal))
            return;
        if (State.Status == AgentJobStatus.Pending)
            await TryAdmitAsync();
    }

    private async Task ReleaseConcurrencyPermitAsync()
    {
        if (State.Input is null)
            return;

        var projectId = State.Input.ProjectId;
        var agentId = State.Input.AgentId;
        var token = State.ConcurrencyPermitToken;
        if (string.IsNullOrWhiteSpace(projectId)
            || string.IsNullOrWhiteSpace(agentId)
            || string.IsNullOrWhiteSpace(token))
        {
            State.ConcurrencyPermitHeld = false;
            return;
        }

        State.ConcurrencyPermitHeld = false;
        State.ConcurrencyReleasePending = true;
        await PersistAsync();
        await TryReleaseConcurrencyPermitAsync();
    }

    private async Task TryReleaseConcurrencyPermitAsync()
    {
        if (!State.ConcurrencyReleasePending
            || State.Input is null
            || string.IsNullOrWhiteSpace(State.Input.ProjectId)
            || string.IsNullOrWhiteSpace(State.Input.AgentId)
            || string.IsNullOrWhiteSpace(State.ConcurrencyPermitToken))
            return;

        try
        {
            var projectId = State.Input.ProjectId;
            var agentId = State.Input.AgentId;
            var gate = _grains.GetGrain<IAgentConcurrencyGrain>(GrainKey.Agent(projectId, agentId));
            if (State.ConcurrencyPermitId is not null
                && State.ConcurrencyDispatchId is not null)
            {
                await gate.MarkTerminalAsync(
                    projectId,
                    agentId,
                    State.ConcurrencyPermitToken,
                    State.ConcurrencyPermitId,
                    State.ConcurrencyDispatchId,
                    State.Status == AgentJobStatus.Cancelled);
            }
            await gate.ReleaseAsync(
                projectId,
                agentId,
                State.ConcurrencyPermitToken,
                State.ConcurrencyPermitId,
                State.ConcurrencyGeneration == 0 ? null : State.ConcurrencyGeneration,
                State.ConcurrencyWaiterId);
            State.ConcurrencyReleasePending = false;
            await PersistAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "AgentJob {Id} could not release concurrency permit {Token}; recovery reminder will retry",
                Key,
                State.ConcurrencyPermitToken);
        }
    }

    private async Task<bool> TryAdmitOnRunnerAsync(string runnerId)
    {
        var runner = GrainFactory.GetGrain<IRunnerGrain>(runnerId);
        var state = await runner.GetRuntimeStateAsync();
        if (state.Status != RunnerStatus.Online)
            return false;

        var maxSlots = await runner.GetSlotsAsync();
        var activeWorkCount = state.ActiveWorks
            .Select(w => w.OwnerId)
            .Distinct(StringComparer.Ordinal)
            .Count();
        if (activeWorkCount >= maxSlots)
            return false;

        // Admission writes the ledger row directly. The grain does not
        // call RunnerGrain.AssignAgentJobAsync and does not transition
        // the job to Running; the next poll claim does that.
        var now = _timeProvider.GetUtcNow();
        var workId = StableWorkId(Key);
        var dispatch = await BuildDispatchAsync(workId);

        State.RunnerId = runnerId;
        State.WorkId = workId;
        State.ReadySince = now;
        State.RunnerAccepted = false;
        State.RunningSince = null;

        var record = new AgentJobLedgerRecord(
            JobKey: Key,
            StateJson: JsonSerializer.Serialize(State, JSON.Options),
            Revision: _ledger?.Revision ?? 0,
            AssignedRunnerId: runnerId,
            WorkId: workId,
            ReadySince: now,
            RunningSince: null,
            DispatchJson: JsonSerializer.Serialize(dispatch, JSON.Options),
            WorkType: "agent-job",
            Stage: "agent",
            Title: "Agent Job",
            IssueProjectId: State.Input?.ProjectId,
            IssueNumber: State.Input?.IssueNumber,
            AgentSessionId: State.Input?.AgentSessionId,
            InitialInputId: State.Input?.InitialInputId,
            InitialTurnId: State.Input?.InitialTurnId,
            PinnedRunnerId: State.Input?.PinnedRunnerId,
            LaunchVisibility: State.LaunchVisibility.ToString().ToLowerInvariant());

        if (_ledger is null)
        {
            var inserted = await _jobStore.InsertLedgerAsync(record);
            _ledger = inserted;
            await HydrateAsync();
        }
        else
        {
            var saved = await _jobStore.SaveLedgerAsync(record);
            _ledger = saved;
            await HydrateAsync();
        }

        _log.LogInformation(
            "AgentJob {Id} admitted to runner {Runner} as work {Work} (readySince={ReadySince})",
            Key, runnerId, workId, now);

        await EnsureRecoveryReminderAsync();

        if (State.ConcurrencyPermitHeld
            && State.ConcurrencyPermitId is not null
            && State.ConcurrencyDispatchId is not null
            && State.Input?.ProjectId is { } projectId
            && State.Input.AgentId is { } agentId)
        {
            await _grains.GetGrain<IAgentConcurrencyGrain>(GrainKey.Agent(projectId, agentId))
                .MarkDispatchedAsync(
                    projectId,
                    agentId,
                    State.ConcurrencyPermitToken!,
                    State.ConcurrencyPermitId,
                    State.ConcurrencyDispatchId);
            State.ConcurrencyGateStatus = AgentConcurrencyPermitStatus.Dispatched;
            await PersistAsync();
        }

        // The test-only signal is the admission boundary: all durable
        // assignment and concurrency state must be visible before polling.
        await SafeAssignmentPreparedAsync(runnerId, workId);

        return true;
    }

    private async Task SafeAssignmentPreparedAsync(string runnerId, string workId)
    {
        try
        {
            await _dispatchObserver.AssignmentPreparedAsync(Key, runnerId, workId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "AgentJob {Id} dispatch observer AssignmentPrepared threw; ledger row remains authoritative",
                Key);
        }
    }

    private async Task SafeRunnerAcceptedAsync(string runnerId, string workId)
    {
        try
        {
            await _dispatchObserver.RunnerAcceptedAsync(Key, runnerId, workId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "AgentJob {Id} dispatch observer RunnerAccepted threw; claim remains authoritative",
                Key);
        }
    }

    private static WorkDispatch? DeserializeDispatch(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<WorkDispatch>(json, JSON.Options);
        }
        catch
        {
            return null;
        }
    }

    private void ArmJobTimeout()
    {
        if (_options.JobTimeout <= TimeSpan.Zero)
            return;

        var dueTime = _options.JobTimeout;
        if (State.RunningSince is { } runningSince)
        {
            var remaining = runningSince + _options.JobTimeout - _timeProvider.GetUtcNow();
            dueTime = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        DisposeJobTimeoutTimer();
        _jobTimeoutTimer = this.RegisterGrainTimer(
            _ => OnJobTimeoutAsync(),
            dueTime,
            TimeSpan.FromMilliseconds(-1));
    }

    private async Task OnJobTimeoutAsync()
    {
        if (IsTerminal || State.RunnerId is null)
            return;

        var runnerLost = await IsRunnerAwayAsync();
        var reason = runnerLost
            ? AgentJobFailureReasons.RunnerLost
            : $"{AgentJobFailureReasons.ReportTimeout}: report timeout after {_options.JobTimeout}";
        DateTimeOffset? recoveryDeadlineAt = runnerLost
            ? _timeProvider.GetUtcNow() + _runnerLossRecoveryTimeout
            : null;

        _log.LogWarning(
            "AgentJob {Id} report timeout after {Timeout}; transitioning to unknown with reason {Reason}",
            Key, _options.JobTimeout, reason);
        await EnterUnknownStateAsync(reason, recoveryDeadlineAt);
    }

    private async Task EvaluatePendingAsync()
    {
        if (string.IsNullOrWhiteSpace(State.RunnerId)
            || string.IsNullOrWhiteSpace(_ledger?.DispatchJson))
        {
            await TryAdmitAsync();
        }
    }

    private bool JobTimeoutExceeded()
    {
        return State.RunnerId is not null
            && State.RunningSince is not null
            && _options.JobTimeout > TimeSpan.Zero
            && _timeProvider.GetUtcNow() >= State.RunningSince.Value + _options.JobTimeout;
    }

    internal async Task EnterTerminalStateAsync(
        AgentJobStatus terminalStatus,
        int? exitCode,
        string? failureReason,
        string? failureCategory,
        string? pendingReason,
        string? message,
        string? output,
        string[]? artifactUploadIds,
        int? terminalExitCode)
    {
        var pending = BuildPendingSessionClose(terminalStatus, exitCode, failureReason, failureCategory, pendingReason);

        if (IsTerminal)
        {
            State.PendingSessionClose ??= pending;
            if (State.PendingSessionClose is not null
                || State.PendingFailureEvent is not null
                || State.PendingTerminalDeliveryEvent is not null
                || State.PendingSubagentTerminalEvent is not null)
                await EnsureRecoveryReminderAsync();
            await PersistAsync();
            await TryReleaseConcurrencyPermitAsync();
            if (State.PendingSessionClose is not null)
                await DeliverTerminalToSessionAsync(State.PendingSessionClose);
            if (State.PendingFailureEvent is not null)
                await EmitFailureEventAsync(State.PendingFailureEvent);
            if (State.PendingTerminalDeliveryEvent is not null)
                await EmitTerminalDeliveryEventAsync(State.PendingTerminalDeliveryEvent);
            if (State.PendingSubagentTerminalEvent is not null)
                await EmitSubagentTerminalEventAsync(State.PendingSubagentTerminalEvent);
            return;
        }

        if (State.TerminalLogOwnership is null
            && !string.IsNullOrWhiteSpace(State.RunnerId)
            && !string.IsNullOrWhiteSpace(State.WorkId))
        {
            State.TerminalLogOwnership = new AgentJobTerminalLogOwnership(
                TerminalLogOwnerKinds.AgentJob,
                Key,
                State.WorkId,
                State.RunnerId);
        }

        State.Status = terminalStatus;
        State.FailureReason = failureReason;
        State.RecoveryDeadlineAt = null;
        State.RunningSince = null;
        State.TerminalAt = _timeProvider.GetUtcNow();
        State.TerminalResult = new AgentJobTerminalResult(
            terminalStatus,
            message,
            output,
            artifactUploadIds,
            failureReason,
            terminalExitCode ?? exitCode);
        State.PendingSessionClose = pending;
        StageTerminalDeliveryEvent(
            terminalStatus,
            message,
            output,
            failureReason,
            failureCategory,
            artifactUploadIds,
            terminalExitCode ?? exitCode);
        StageSubagentTerminalEvent(terminalStatus);

        if (terminalStatus == AgentJobStatus.Failed)
        {
            State.PendingFailureEvent = new PendingFailureEvent(
                EventId: AgentJobSessionDeliveryIds.FailureEventId(Key),
                FailureReason: failureReason ?? pendingReason,
                FailureCategory: failureCategory,
                RecordedAt: _timeProvider.GetUtcNow());
        }

        DisposeJobTimeoutTimer();

        State.ConcurrencyGateStatus = terminalStatus == AgentJobStatus.Cancelled
            ? AgentConcurrencyPermitStatus.Cancelled
            : AgentConcurrencyPermitStatus.Terminal;
        State.ConcurrencyReleasePending = State.ConcurrencyPermitId is not null
            || State.ConcurrencyPermitHeld
            || State.ConcurrencyWaiterId is not null;
        await EnsureRecoveryReminderAsync();
        await PersistAsync();
        await TryReleaseConcurrencyPermitAsync();
        _terminalCompletion.TrySetResult(State.TerminalResult);

        _log.LogInformation(
            "AgentJob {Id} terminal: {Status} ({Reason}, category={Category}, deliveryId={DeliveryId})",
            Key, State.Status, State.FailureReason ?? "ok",
            State.PendingSessionClose?.FailureCategory ?? "-",
            State.PendingSessionClose?.DeliveryId ?? "-");

        await DeliverTerminalToSessionAsync(pending);
        await MarkInitialTurnTerminalAsync(terminalStatus, message, output, failureReason, failureCategory, terminalExitCode ?? exitCode);
        if (State.PendingFailureEvent is not null)
            await EmitFailureEventAsync(State.PendingFailureEvent);
        if (State.PendingTerminalDeliveryEvent is not null)
            await EmitTerminalDeliveryEventAsync(State.PendingTerminalDeliveryEvent);
        if (State.PendingSubagentTerminalEvent is not null)
            await EmitSubagentTerminalEventAsync(State.PendingSubagentTerminalEvent);
    }

    private async Task MarkInitialTurnExecutingAsync()
    {
        var sessionId = State.Input?.AgentSessionId;
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(State.Input?.InitialTurnId))
            return;

        await _grains.GetGrain<IAgentSessionGrain>(sessionId).MarkTurnExecutingAsync(State.Input!.InitialTurnId!);
    }

    private async Task MarkInitialTurnTerminalAsync(
        AgentJobStatus status,
        string? message,
        string? output,
        string? failureReason,
        string? failureCategory,
        int? exitCode)
    {
        var sessionId = State.Input?.AgentSessionId;
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(State.Input?.InitialTurnId))
            return;

        var sessionGrain = _grains.GetGrain<IAgentSessionGrain>(sessionId);
        if (await sessionGrain.GetAsync() is null)
            return;

        await sessionGrain.MarkTurnTerminalAsync(
            State.Input!.InitialTurnId!,
            status switch
            {
                AgentJobStatus.Completed => AgentTurnStatus.Completed,
                AgentJobStatus.Cancelled => AgentTurnStatus.Cancelled,
                _ => AgentTurnStatus.Failed,
            },
            new AgentTurnResult(message, output, failureReason, failureCategory, exitCode));
    }

    private async Task EmitFailureEventAsync(PendingFailureEvent obligation)
    {
        var envelope = BuildFailureEnvelope(obligation);
        try
        {
            await _eventStore.AppendAsync(envelope, CancellationToken.None);
            EventDispatcherPoke.PokeAfterCommit(GrainFactory, _log, nameof(AgentJobGrain), _backgroundTasks);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "AgentJob {Id} failed to append {Type} event (eventId={EventId}); reminder will retry",
                Key, EventCatalog.ReverseDns.AgentJobFailed, obligation.EventId);
            await EnsureRecoveryReminderAsync();
            return;
        }
        State.PendingFailureEvent = null;
        await PersistAsync();
        _log.LogInformation(
            "AgentJob {Id} emitted {Type} event (eventId={EventId}, reason={Reason}, category={Category})",
            Key,
            EventCatalog.ReverseDns.AgentJobFailed,
            obligation.EventId,
            obligation.FailureReason ?? "-",
            obligation.FailureCategory ?? "-");
    }

    internal CloudEvent BuildFailureEnvelope(PendingFailureEvent obligation)
    {
        var extensions = AgentJobLineage.BuildExtensions(State.Input, State.RoutedPlan);
        var projectId = extensions.TryGetValue(EventCatalog.Lineage.ProjectId, out var pid) ? pid : null;
        var issue = extensions.TryGetValue(EventCatalog.Lineage.Issue, out var iss) ? iss : null;
        var epic = extensions.TryGetValue(EventCatalog.Lineage.Epic, out var epi) ? epi : null;
        var workflowRunId = extensions.TryGetValue(EventCatalog.Lineage.WorkflowRunId, out var wri) ? wri : null;
        var agentId = extensions.TryGetValue(EventCatalog.Lineage.AgentId, out var aid) ? aid : null;
        ProducerConformance.Assert(EventProducerFamily.AgentJob, extensions, new ProducerLineageContext(
            ProjectId: projectId,
            Issue: issue,
            Epic: epic,
            WorkflowRunId: workflowRunId,
            AgentId: agentId));
        return AgentJobLineage.BuildFailureEnvelope(
            Key,
            obligation.RecordedAt,
            new AgentJobLineage.FailurePayload(
                JobKey: Key,
                Status: State.Status,
                FailureReason: obligation.FailureReason,
                FailureCategory: obligation.FailureCategory,
                ProjectId: projectId,
                AgentId: agentId),
            extensions);
    }

    private async Task EnsureRecoveryReminderAsync()
    {
        await this.RegisterOrUpdateReminder(
            RecoveryReminderName,
            RecoveryReminderDue,
            RecoveryReminderPeriod);
    }

    private PendingSessionClose BuildPendingSessionClose(
        AgentJobStatus terminalStatus,
        int? exitCode,
        string? failureReason,
        string? failureCategory,
        string? pendingReason)
    {
        if (State.PendingSessionClose is { } existing)
            return existing;

        var statusText = terminalStatus switch
        {
            AgentJobStatus.Completed => "completed",
            AgentJobStatus.Cancelled => "cancelled",
            _ => "failed",
        };
        return new PendingSessionClose(
            DeliveryId: AgentJobSessionDeliveryIds.TerminalDeliveryId(Key),
            Status: statusText,
            ExitCode: exitCode,
            FailureReason: failureReason ?? pendingReason,
            FailureCategory: failureCategory,
            RecordedAt: _timeProvider.GetUtcNow());
    }

    private async Task DeliverTerminalToSessionAsync(PendingSessionClose pending)
    {
        var sessionId = State.Input?.AgentSessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            await ClearPendingSessionCloseAndMaybeReminderAsync();
            return;
        }

        try
        {
            var grain = GrainFactory.GetGrain<IAgentSessionGrain>(sessionId);
            var payloadJson = JSON.Serialize(new Dictionary<string, object?>
            {
                ["status"] = pending.Status,
                ["exitCode"] = pending.ExitCode,
                ["failureReason"] = pending.FailureReason,
                ["failureCategory"] = pending.FailureCategory,
                ["recordedAt"] = pending.RecordedAt.ToString("o"),
                ["agentJobId"] = Key,
                ["deliveryId"] = pending.DeliveryId,
            });
            await grain.AppendTerminalCloseAsync(new AppendTerminalCloseCommand(
                SessionId: sessionId,
                DeliveryId: pending.DeliveryId,
                Status: pending.Status,
                ExitCode: pending.ExitCode,
                FailureReason: pending.FailureReason,
                FailureCategory: pending.FailureCategory,
                RecordedAt: pending.RecordedAt,
                PayloadJson: payloadJson,
                RuntimeSessionId: State.RuntimeSessionId));

            await ClearPendingSessionCloseAndMaybeReminderAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "AgentJob {Id} terminal delivery to session {SessionId} failed; deliveryId={DeliveryId} retained for retry",
                Key, sessionId, pending.DeliveryId);
        }
    }

    private async Task ClearPendingSessionCloseAndMaybeReminderAsync()
    {
        State.PendingSessionClose = null;
        await PersistAsync();
        if (State.PendingFailureEvent is not null
            || State.PendingTerminalDeliveryEvent is not null
            || State.PendingSubagentTerminalEvent is not null)
            return;
        try
        {
            var reminder = await this.GetReminder(RecoveryReminderName);
            if (reminder is not null)
                await this.UnregisterReminder(reminder);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex,
                "AgentJob {Id} could not unregister recovery reminder; orphan tick will self-clean",
                Key);
        }
    }

    private void StageTerminalDeliveryEvent(
        AgentJobStatus status,
        string? message,
        string? output,
        string? failureReason,
        string? failureCategory,
        string[]? artifactUploadIds,
        int? exitCode)
    {
        var origin = State.ManualPlan?.ConnectionOrigin;
        if (origin is null || State.PendingTerminalDeliveryEvent is not null)
            return;

        State.PendingTerminalDeliveryEvent = new PendingTerminalDeliveryEvent(
            AgentJobSessionDeliveryIds.TerminalDeliveryEventId(Key),
            origin,
            status,
            message,
            failureReason,
            failureCategory,
            artifactUploadIds?.Length ?? 0,
            exitCode,
            _timeProvider.GetUtcNow(),
            output);
    }

    private async Task EmitTerminalDeliveryEventAsync(PendingTerminalDeliveryEvent pending)
    {
        try
        {
            var envelope = BuildTerminalDeliveryEnvelope(pending);
            await _eventStore.AppendAsync(envelope, CancellationToken.None);
            EventDispatcherPoke.PokeAfterCommit(GrainFactory, _log, nameof(AgentJobGrain), _backgroundTasks);
            State.PendingTerminalDeliveryEvent = null;
            await PersistAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "AgentJob {Id} terminal delivery event is retained for retry", Key);
        }
    }

    private void StageSubagentTerminalEvent(AgentJobStatus status)
    {
        if (status is not (AgentJobStatus.Completed or AgentJobStatus.Failed or AgentJobStatus.Cancelled)
            || State.Input?.SpawnOrigin is null
            || State.LaunchVisibility != AgentLaunchVisibility.Visible
            || State.PendingSubagentTerminalEvent is not null)
            return;

        // Only an accepted (visible) delegation owes a terminal callback;
        // a provisional or rejected launch was never attached to a parent
        // SessionParentLink, so a cancelled job here must stay silent.
        State.PendingSubagentTerminalEvent = new PendingSubagentTerminalEvent(
            AgentJobSessionDeliveryIds.SubagentTerminalEventId(Key),
            State.Input.SpawnOrigin,
            status,
            $"agent-job:{Key}",
            _timeProvider.GetUtcNow());
    }

    private async Task EmitSubagentTerminalEventAsync(PendingSubagentTerminalEvent pending)
    {
        try
        {
            await _eventStore.AppendAsync(BuildSubagentTerminalEnvelope(pending), CancellationToken.None);
            EventDispatcherPoke.PokeAfterCommit(GrainFactory, _log, nameof(AgentJobGrain), _backgroundTasks);
            State.PendingSubagentTerminalEvent = null;
            await PersistAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "AgentJob {Id} subagent terminal event is retained for retry", Key);
        }
    }

    internal CloudEvent BuildSubagentTerminalEnvelope(PendingSubagentTerminalEvent pending) =>
        AgentJobLineage.BuildSubagentTerminalEnvelope(Key, pending);

    internal CloudEvent BuildTerminalDeliveryEnvelope(PendingTerminalDeliveryEvent obligation)
    {
        var extensions = AgentJobLineage.BuildExtensions(State.Input, State.RoutedPlan);
        var sessionLaunchPrompt = State.Input?.Prompt
            ?? State.ManualPlan?.Prompt
            ?? State.RoutedPlan?.Prompt;
        return AgentJobLineage.BuildTerminalDeliveryEnvelope(
            Key,
            obligation,
            extensions,
            sessionLaunchPrompt);
    }

    private async Task UnregisterSelfAsync(string reminderName)
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
