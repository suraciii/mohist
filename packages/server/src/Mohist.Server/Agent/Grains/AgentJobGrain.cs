using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Serialization;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Orleans.Runtime;

namespace Mohist.Server.Agent.Grains;

/// <summary>
/// Authoritative owner of a standalone agent job's lifecycle and terminal result.
/// Persisted and non-reentrant so the backoff timer and
/// <see cref="ReportResultAsync"/> cannot race on the lifecycle state. Dispatches
/// its work directly to an idle Runner via <see cref="IRunnerRegistryGrain"/>, never
/// touching workflow assignment or workflow polling.
///
/// State machine:
/// <c>Pending</c> → <c>Running</c> (when a Runner accepts the dispatch) → <c>Completed</c>
/// or <c>Failed</c> (terminal). <c>Pending</c> → <c>Failed</c> when the dispatch retry
/// bound is reached without ever acquiring a slot.
///
/// Every terminal transition persists a <see cref="PendingSessionClose"/>
/// payload (with a stable delivery id and the recorded timestamp) and
/// registers a durable <c>agent-job-recovery</c> Orleans reminder. The
/// AgentSession grain's idempotent <c>AppendTerminalCloseAsync</c> command
/// flips that pending flag off and unregisters the reminder only after
/// the matching <c>session.closed</c> transcript fact is durable. The
/// reminder drives retries until acknowledgement so a process restart,
/// an activation loss, or a Session-persistence failure cannot lose the
/// terminal fact.
/// </summary>
public sealed class AgentJobGrain : Grain, IAgentJobGrain
{
    internal const string RecoveryReminderName = "agent-job-recovery";

    private static readonly TimeSpan RecoveryReminderDue = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RecoveryReminderPeriod = TimeSpan.FromSeconds(1);

    private readonly ILogger<AgentJobGrain> _log;
    private readonly AgentJobOptions _options;
    private readonly AgentJobBackoffSchedule _backoff;
    private readonly TimeProvider _timeProvider;
    private readonly IPersistentState<AgentJobState> _state;
    private readonly IAgentJobDispatchObserver _dispatchObserver;
    private readonly IAgentJobStore? _jobStore;
    private readonly IEventStore _eventStore;
    private IDisposable? _dispatchTimer;
    private IDisposable? _jobTimeoutTimer;

    public AgentJobGrain(
        ILogger<AgentJobGrain> log,
        IOptions<AgentJobOptions> options,
        TimeProvider timeProvider,
        [PersistentState("agent-job")] IPersistentState<AgentJobState> state,
        IEventStore eventStore,
        IAgentJobDispatchObserver? dispatchObserver = null,
        IAgentJobStore? jobStore = null)
    {
        _log = log;
        _options = options.Value;
        _timeProvider = timeProvider;
        _state = state;
        _eventStore = eventStore;
        _dispatchObserver = dispatchObserver ?? NoopAgentJobDispatchObserver.Instance;
        _jobStore = jobStore;
        // The backoff schedule is captured at activation time from the current
        // snapshot of AgentJobOptions. Hot-reload of the configuration section
        // is not applied to an already-active grain; it takes effect on the
        // next activation. (Switching to IOptionsMonitor would not change this
        // because Orleans instantiates grains per-activation, not per-call.)
        _backoff = _options.ResolveBackoffSchedule();
    }

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        if (!_state.RecordExists)
            await _state.ReadStateAsync();

        _log.LogInformation("AgentJob {Id} OnActivateAsync: status={Status}, input={Input}, routedPlan={RoutedPlan}",
            Key, State.Status, State.Input is not null, State.RoutedPlan is not null);

        if (IsTerminal)
        {
            // A terminal job carries a durable recovery obligation:
            // either the pending Session-close delivery or the pending
            // failure-event append is still outstanding.
            // Try to deliver here so a freshly reactivated grain
            // finishes the work without waiting for the next reminder
            // tick (the reminder is the safety net, not the only path).
            // If a reminder was lost across silos, re-register it so the
            // background loop keeps retrying until both obligations are
            // durable.
            if (State.PendingSessionClose is not null || State.PendingFailureEvent is not null)
            {
                await EnsureRecoveryReminderAsync();
                if (State.PendingSessionClose is not null)
                    await DeliverTerminalToSessionAsync(State.PendingSessionClose);
                if (State.PendingFailureEvent is not null)
                    await EmitFailureEventAsync(State.PendingFailureEvent);
            }
            return;
        }

        if (State.Input is null && State.RoutedPlan is null)
            return;

        // Routed-launch preparation recovery: the plan is durable but
        // Session open / LaunchReady / Pending dispatch have not yet
        // converged. Advance the persisted plan idempotently; this
        // branch is hit after a process loss before Session open,
        // before LaunchReady persisted, or before Runner acceptance.
        if (State.RoutedPlan is not null && !State.RunnerAccepted)
        {
            await EnsureRecoveryReminderAsync();
            await AdvancePreparedLaunchAsync();
            return;
        }

        if (State.Status == AgentJobStatus.Running)
        {
            ArmJobTimeout();
            return;
        }

        await TryDispatchAsync();
    }

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken ct)
    {
        _dispatchTimer?.Dispose();
        _dispatchTimer = null;
        _jobTimeoutTimer?.Dispose();
        _jobTimeoutTimer = null;
        return Task.CompletedTask;
    }

    private string Key => this.GetPrimaryKeyString();
    private AgentJobState State => _state.State;
    private bool IsTerminal => State.Status is AgentJobStatus.Completed or AgentJobStatus.Failed;

    public Task<AgentJobStatus> GetStatusAsync() => Task.FromResult(State.Status);

    public Task<string?> GetCurrentWorkIdAsync() => Task.FromResult(State.WorkId);

    public Task<AgentJobRuntimeSnapshot> GetRuntimeSnapshotAsync() =>
        Task.FromResult(new AgentJobRuntimeSnapshot(
            State.Status,
            State.RunnerId,
            State.WorkId,
            State.FailureReason,
            State.DispatchAttempts,
            State.RunnerAccepted,
            State.PendingSessionClose is not null,
            State.Input?.ProjectId ?? State.RoutedPlan?.ProjectId));

    /// <summary>
    /// Returns the job's terminal result. Before the job has reached a terminal
    /// state (<see cref="AgentJobStatus.Completed"/> or <see cref="AgentJobStatus.Failed"/>),
    /// this method returns a synthesised <see cref="AgentJobTerminalResult"/>
    /// whose <see cref="AgentJobTerminalResult.Status"/> mirrors the current
    /// grain state but whose <see cref="AgentJobTerminalResult.Message"/>,
    /// <see cref="AgentJobTerminalResult.Output"/>, and
    /// <see cref="AgentJobTerminalResult.ArtifactUploadIds"/> are all <c>null</c>.
    /// Callers that need to distinguish "not yet terminal" from "terminal" must
    /// check the <see cref="AgentJobTerminalResult.Status"/> field.
    /// </summary>
    public Task<AgentJobTerminalResult> GetTerminalResultAsync()
    {
        if (State.TerminalResult is not null)
            return Task.FromResult(State.TerminalResult);

        return Task.FromResult(new AgentJobTerminalResult(
            State.Status, null, null, null, State.FailureReason, null));
    }

    public async Task AssignRunnerAsync(string runnerId, string workId)
    {
        if (State.Status != AgentJobStatus.Pending)
            throw new InvalidOperationException(
                $"AgentJob '{Key}' cannot accept runner assignment in status {State.Status}");

        AssignRunnerInternal(runnerId, workId);
        State.RunnerAccepted = true;
        State.Status = AgentJobStatus.Running;
        State.RunningSince = _timeProvider.GetUtcNow();
        await SaveAsync();
        ArmJobTimeout();
    }

    private void AssignRunnerInternal(string runnerId, string workId)
    {
        State.RunnerId = runnerId;
        State.WorkId = workId;
        State.RunnerAccepted = false;
        State.RunningSince = null;
    }

    public Task<bool> IsWorkRunnableAsync(string runnerId, string workId)
    {
        return Task.FromResult(
            !IsTerminal
            && string.Equals(State.RunnerId, runnerId, StringComparison.Ordinal)
            && string.Equals(State.WorkId, workId, StringComparison.Ordinal));
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
        await SaveAsync();
        return true;
    }

    public async Task<AgentJobReportResult> ReportResultAsync(string runnerId, string workId, WorkResult result)
    {
        if (IsTerminal)
        {
            _log.LogDebug(
                "AgentJob {Id} rejecting report from {Runner} for {Work}: already in terminal {Status}",
                Key, runnerId, workId, State.Status);
            // Repair: a redelivered report on an already-terminal job
            // must not silently lose its pending Session close. Retry
            // the durable delivery here so report replay and reminder
            // ticks converge on the same single close fact.
            if (State.PendingSessionClose is not null)
                await DeliverTerminalToSessionAsync(State.PendingSessionClose);
            return new AgentJobReportResult(false, "already-terminal");
        }

        if (State.Status != AgentJobStatus.Running
            && (State.RunnerId is null || State.WorkId is null))
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

        var isSuccess = string.Equals(result.Status, "completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(result.Status, "pass", StringComparison.OrdinalIgnoreCase)
            || string.Equals(result.Status, "ok", StringComparison.OrdinalIgnoreCase)
            || string.Equals(result.Status, "success", StringComparison.OrdinalIgnoreCase);

        var failureReason = isSuccess
            ? null
            : (string.IsNullOrWhiteSpace(result.Message) ? result.Status : result.Message);

        // Runner category precedence: structured output `failureCategory`
        // → `WorkResult.Error.Code` → report status. The output JSON is
        // parsed here so the persisted terminal fact (and downstream
        // AgentSession close event) reflects the same verdict the runner
        // surfaced, even when `result.Error.Code` carries the pre-execution
        // `invalid-input` classification a generic status fallback would
        // collapse to `failed`.
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

    public Task FailAsync(string reason)
    {
        if (IsTerminal)
        {
            return Task.CompletedTask;
        }

        return EnterTerminalStateAsync(
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
        if (State.Input is not null)
        {
            var existingInput = InputWithAgentConfig()!;
            if (EquivalentInput(existingInput, input))
            {
                if (!IsTerminal && State.Status == AgentJobStatus.Pending)
                    await TryDispatchAsync();
                return;
            }
            throw new InvalidOperationException(
                $"AgentJob '{Key}' cannot accept a different submission after it has started " +
                $"({DescribeInputDifferences(existingInput, input)})");
        }

        if (State.Status != AgentJobStatus.Pending)
            throw new InvalidOperationException(
                $"AgentJob '{Key}' cannot be re-submitted; current status is {State.Status}");

        if (input is null || string.IsNullOrWhiteSpace(input.Prompt))
            throw new ArgumentException("AgentJobInput.Prompt is required", nameof(input));

        State.AgentConfigJson = SerializeAgentConfig(input.AgentConfig);
        State.Input = input with { AgentConfig = null };
        State.SubmittedAt = _timeProvider.GetUtcNow();
        State.NextDispatchDelay = TimeSpan.Zero;
        State.DispatchAttempts = 0;
        await SaveAsync();
        await TryDispatchAsync();
    }

    public async Task EnsureSubmittedAsync(AgentJobInput input)
    {
        if (State.Input is not null)
        {
            if (!IsTerminal && State.Status == AgentJobStatus.Pending)
                await TryDispatchAsync();
            return;
        }

        await SubmitAsync(input);
    }

    /// <summary>
    /// Idempotent routed-launch preparation. Registers the durable
    /// <c>agent-job-recovery</c> reminder
    /// BEFORE persisting the canonical plan so a crash between
    /// reminder registration and plan persistence still leaves an
    /// orphan reminder that self-cleans on its first tick. The reminder
    /// keeps serving <see cref="AdvancePreparedLaunchAsync"/> (and the
    /// terminal-delivery retry loop) until the job reaches Runner
    /// acceptance or a durable terminal close.
    ///
    /// <para>
    /// On replay the canonical plan (the one currently persisted on
    /// state) is returned even when the caller's resolved values
    /// differ — first-writer semantics. Redelivery cannot overwrite
    /// the workspace, lineage, or preflight outcome that the first
    /// delivery decided.
    /// </para>
    /// </summary>
    public async Task<RoutedAgentLaunchPlan> EnsurePreparedAsync(RoutedAgentLaunchPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (State.RoutedPlan is { } existing)
        {
            // First-writer: replay returns the persisted canonical
            // plan, not the caller's newly resolved values. Ensure the
            // recovery reminder is registered so OnActivate / the
            // reminder tick can advance the plan if the silo was
            // restarted between preparation and the caller calling
            // AdvancePreparedLaunchAsync.
            await EnsureRecoveryReminderAsync();
            return existing;
        }

        // Register reminder first so a crash between registration and
        // plan persistence still leaves a recoverable reminder. The
        // tick self-cleans when State.RoutedPlan is null and there is
        // no terminal pending close — covering the rare
        // reminder-before-failed-write edge case.
        await EnsureRecoveryReminderAsync();

        State.RoutedPlan = plan;
        await SaveAsync();
        return plan;
    }

    /// <summary>
    /// Advance the durable prepared launch plan. Idempotent across
    /// immediate, OnActivate, and
    /// reminder-recovery paths. Opens the AgentSession from the
    /// persisted plan only — never from caller's newly resolved
    /// values — and either persists LaunchReady and submits the
    /// AgentJobInput (executable disposition) or enters the durable
    /// preflight terminal-delivery protocol.
    /// </summary>
    public async Task AdvancePreparedLaunchAsync()
    {
        var plan = State.RoutedPlan;
        _log.LogInformation("AgentJob {Id} AdvancePreparedLaunchAsync: plan={Plan}, disposition={Disp}",
            Key, plan is not null, plan?.Disposition);
        if (plan is null)
            return;

        if (State.RunnerAccepted)
        {
            await UnregisterSelfAsync(RecoveryReminderName);
            return;
        }

        if (IsTerminal)
        {
            // Terminal state already covers either a successful runner
            // report or the preflight-failed terminal close; the
            // reminder self-cleans via ReceiveReminder when there is no
            // pending delivery.
            return;
        }

        // Ensure reminder is registered across all advance paths so a
        // crash between steps is recoverable without event redelivery.
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
        };
        if (plan.IssueNumber is > 0)
            labels[GenericAgentSessionMetadata.IssueNumber] = plan.IssueNumber.Value.ToString();
        if (plan.EpicNumber is > 0)
            labels[GenericAgentSessionMetadata.EpicNumber] = plan.EpicNumber.Value.ToString();
        if (!string.IsNullOrWhiteSpace(plan.WorkspacePath))
            labels[GenericAgentSessionMetadata.WorkspacePath] = plan.WorkspacePath!;
        var metadata = new AgentSessionMetadata(labels, null);

        await sessionGrain.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: string.Empty,
            AgentRuntime: plan.Runtime ?? AgentConfigSchema.OpenCodeRuntime,
            WorkDir: plan.WorkspacePath,
            Metadata: metadata));

        if (plan.Disposition == RoutedLaunchDisposition.PreflightFailed)
        {
            // Persist LaunchReady so subsequent state reads reflect the
            // Session has been opened from the canonical plan. Then
            // enter the durable terminal-delivery protocol; the
            // reminder stays active until Session-close acknowledgement.
            State.LaunchReady = true;
            // Surface the session id via the AgentJobInput.AgentSessionId
            // field even for the preflight branch so the durable
            // terminal-delivery helper can route the close to the
            // AgentSession grain we just opened above. The job will
            // never reach dispatch because the terminal transition
            // happens immediately afterwards.
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
                IssueNumber: plan.IssueNumber,
                EpicNumber: plan.EpicNumber,
                WorkflowRunId: plan.WorkflowRunId);
            await SaveAsync();

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
                IssueNumber: plan.IssueNumber,
                EpicNumber: plan.EpicNumber,
                WorkflowRunId: plan.WorkflowRunId);
            State.AgentConfigJson = plan.AgentConfigJson;
            State.Input = input with { AgentConfig = null };
            State.SubmittedAt = _timeProvider.GetUtcNow();
            State.NextDispatchDelay = TimeSpan.Zero;
            State.DispatchAttempts = 0;
        }

        if (!State.LaunchReady)
        {
            State.LaunchReady = true;
            await SaveAsync();
        }

        // Either the input was newly persisted or it was already
        // there from an earlier advance. TryDispatchAsync is the
        // existing dispatch loop; it is a no-op for terminal jobs and
        // idempotent for Pending ones.
        await TryDispatchAsync();
    }

    private static System.Text.Json.JsonElement? DeserializeAgentConfig(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? null
            : System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();

    private static bool EquivalentInput(AgentJobInput left, AgentJobInput right) =>
        string.Equals(left.Prompt, right.Prompt, StringComparison.Ordinal)
        && string.Equals(left.Model, right.Model, StringComparison.Ordinal)
        && string.Equals(left.WorkspacePath, right.WorkspacePath, StringComparison.Ordinal)
        && string.Equals(left.ProjectId, right.ProjectId, StringComparison.Ordinal)
        && string.Equals(left.Runtime, right.Runtime, StringComparison.Ordinal)
        && string.Equals(left.AgentId, right.AgentId, StringComparison.Ordinal)
        && string.Equals(left.AgentInstructions, right.AgentInstructions, StringComparison.Ordinal)
        && string.Equals(left.AgentSessionId, right.AgentSessionId, StringComparison.Ordinal)
        && string.Equals(left.Variant, right.Variant, StringComparison.Ordinal)
        && JsonEquals(left.AgentConfig, right.AgentConfig);

    private static string DescribeInputDifferences(AgentJobInput left, AgentJobInput right)
    {
        var fields = new List<string>();
        if (!string.Equals(left.Prompt, right.Prompt, StringComparison.Ordinal)) fields.Add(nameof(AgentJobInput.Prompt));
        if (!string.Equals(left.Model, right.Model, StringComparison.Ordinal)) fields.Add(nameof(AgentJobInput.Model));
        if (!string.Equals(left.WorkspacePath, right.WorkspacePath, StringComparison.Ordinal)) fields.Add(nameof(AgentJobInput.WorkspacePath));
        if (!string.Equals(left.ProjectId, right.ProjectId, StringComparison.Ordinal)) fields.Add(nameof(AgentJobInput.ProjectId));
        if (!string.Equals(left.Runtime, right.Runtime, StringComparison.Ordinal)) fields.Add(nameof(AgentJobInput.Runtime));
        if (!string.Equals(left.AgentId, right.AgentId, StringComparison.Ordinal)) fields.Add(nameof(AgentJobInput.AgentId));
        if (!string.Equals(left.AgentInstructions, right.AgentInstructions, StringComparison.Ordinal)) fields.Add(nameof(AgentJobInput.AgentInstructions));
        if (!string.Equals(left.AgentSessionId, right.AgentSessionId, StringComparison.Ordinal)) fields.Add(nameof(AgentJobInput.AgentSessionId));
        if (!string.Equals(left.Variant, right.Variant, StringComparison.Ordinal)) fields.Add(nameof(AgentJobInput.Variant));
        if (!JsonEquals(left.AgentConfig, right.AgentConfig)) fields.Add(nameof(AgentJobInput.AgentConfig));
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
        if (State.Status == AgentJobStatus.Pending
            && State.RunnerId is null
            && DispatchRetryBoundExceeded())
        {
            await EnterTerminalStateAsync(
                AgentJobStatus.Failed,
                null,
                AgentJobFailureReasons.RunnerUnavailable,
                AgentJobFailureReasons.RunnerUnavailable,
                AgentJobFailureReasons.RunnerUnavailable,
                "dispatch retry bound exceeded without acquiring a runner slot",
                null,
                null,
                null);
            return;
        }

        if (State.Status == AgentJobStatus.Pending)
        {
            if (State.RunnerId is not null && JobTimeoutExceeded())
            {
                await OnJobTimeoutAsync();
                return;
            }
            await TryDispatchAsync();
            return;
        }

        if (State.Status == AgentJobStatus.Running && JobTimeoutExceeded())
        {
            await OnJobTimeoutAsync();
        }
    }

    private async Task TryDispatchAsync()
    {
        try
        {
            await TryDispatchCoreAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "AgentJob {Id} dispatch attempt failed", Key);
            await ScheduleNextDispatchAsync();
        }
    }

    private async Task TryDispatchCoreAsync()
    {
        if (State.Status != AgentJobStatus.Pending || State.Input is null || State.SubmittedAt is null)
            return;

        if (State.RunnerId is null && DispatchRetryBoundExceeded())
        {
            await EnterTerminalStateAsync(
                AgentJobStatus.Failed,
                null,
                AgentJobFailureReasons.RunnerUnavailable,
                AgentJobFailureReasons.RunnerUnavailable,
                AgentJobFailureReasons.RunnerUnavailable,
                "dispatch retry bound exceeded without acquiring a runner slot",
                null,
                null,
                null);
            return;
        }

        State.DispatchAttempts++;
        await SaveAsync();

        if (State.RunnerId is not null)
        {
            if (await TryAssignToRunnerAsync(State.RunnerId))
                return;
            if (State.RunnerId is not null)
            {
                await ScheduleNextDispatchAsync();
                return;
            }
        }

        var projectId = State.Input.ProjectId ?? string.Empty;
        var registry = GrainFactory.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        var runners = await registry.ListEligibleRunnersAsync(projectId);
        if (runners.Count == 0)
        {
            await ScheduleNextDispatchAsync();
            return;
        }

        foreach (var runnerInfo in runners)
        {
            if (State.Status != AgentJobStatus.Pending)
                return;
            if (await TryAssignToRunnerAsync(runnerInfo.RunnerId))
                return;
        }

        await ScheduleNextDispatchAsync();
    }

    private async Task<bool> TryAssignToRunnerAsync(string runnerId)
    {
        var runner = GrainFactory.GetGrain<IRunnerGrain>(runnerId);
        var assignmentPrepared = string.Equals(State.RunnerId, runnerId, StringComparison.Ordinal)
            && State.WorkId is not null;
        if (!assignmentPrepared)
        {
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
        }

        if (State.Status != AgentJobStatus.Pending)
            return false;

        if (!assignmentPrepared)
        {
            AssignRunnerInternal(runnerId, StableWorkId(Key));
            await SaveAsync();
            await _dispatchObserver.AssignmentPreparedAsync(Key, runnerId, State.WorkId!);
        }

        var dispatch = BuildDispatch(State.WorkId!);
        var result = await runner.AssignAgentJobAsync(dispatch);

        if (result.Status != RunnerWorkAssignmentStatus.Assigned)
        {
            if (State.Status != AgentJobStatus.Pending)
                return false;
            if (string.Equals(result.Reason, "runner-reconciling", StringComparison.Ordinal))
                return false;

            State.RunnerId = null;
            State.WorkId = null;
            State.RunnerAccepted = false;
            State.RunningSince = null;
            await SaveAsync();
            return false;
        }

        await _dispatchObserver.RunnerAcceptedAsync(Key, runnerId, State.WorkId!);
        State.RunnerAccepted = true;
        State.Status = AgentJobStatus.Running;
        State.RunningSince = _timeProvider.GetUtcNow();
        await SaveAsync();
        DisposeDispatchTimer();
        ArmJobTimeout();
        // Durable Runner-acceptance fence: clear the prepared/pending-dispatch
        // obligation by unregistering the recovery reminder. The next
        // obligation is the terminal Session-close delivery, which
        // (re-)registers its own reminder via EnterTerminalStateAsync.
        await UnregisterSelfAsync(RecoveryReminderName);
        _log.LogInformation(
            "AgentJob {Id} assigned to runner {Runner} as work {Work}",
            Key, runnerId, State.WorkId);
        return true;
    }

    private WorkDispatch BuildDispatch(string workId)
    {
        var input = InputWithAgentConfig()!;
        var payload = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(input.WorkspacePath))
            payload["workspace"] = JSON.SerializeToElement(
                new { path = input.WorkspacePath });

        var variablesJson = payload.Count == 0
            ? null
            : JSON.Serialize(payload);

        var with = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        with["prompt"] = JSON.SerializeToElement(input.Prompt);
        if (!string.IsNullOrWhiteSpace(input.AgentInstructions))
            with["instructions"] = JSON.SerializeToElement(input.AgentInstructions);
        if (!string.IsNullOrWhiteSpace(input.Model))
            with["model"] = JSON.SerializeToElement(input.Model);
        if (!string.IsNullOrWhiteSpace(input.Variant))
            with["variant"] = JSON.SerializeToElement(input.Variant);
        // The dispatch envelope carries the
        // snapshot-fixed runtime so the runner AgentJob executor can
        // select the right runtime (PiRuntime / OpenCodeRuntime). The
        // raw-prompt-only validation path leaves Runtime unset on the
        // input; in that case the runner falls back to its existing
        // single-runtime readiness check.
        if (!string.IsNullOrWhiteSpace(input.Runtime))
            with["runtime"] = JSON.SerializeToElement(input.Runtime);
        var withJson = JSON.Serialize(with);

        return new WorkDispatch(
            WorkflowRunId: string.Empty,
            WorkId: workId,
            Uses: null,
            With: withJson,
            Variables: variablesJson,
            WorkType: "agent-job",
            Stage: "agent",
            Title: "Agent Job",
            OwnerKind: WorkDispatchOwnerKinds.AgentJob,
            AgentJobId: Key,
            ProjectId: string.IsNullOrWhiteSpace(input.ProjectId) ? null : input.ProjectId,
            AgentSessionId: string.IsNullOrWhiteSpace(input.AgentSessionId) ? null : input.AgentSessionId);
    }

    private async Task ScheduleNextDispatchAsync()
    {
        if (State.Status != AgentJobStatus.Pending || State.Input is null || State.SubmittedAt is null)
            return;

        if (State.RunnerId is null && DispatchRetryBoundExceeded())
        {
            await EnterTerminalStateAsync(
                AgentJobStatus.Failed,
                null,
                AgentJobFailureReasons.RunnerUnavailable,
                AgentJobFailureReasons.RunnerUnavailable,
                AgentJobFailureReasons.RunnerUnavailable,
                "dispatch retry bound exceeded without acquiring a runner slot",
                null,
                null,
                null);
            return;
        }
        if (State.RunnerId is not null && JobTimeoutExceeded())
        {
            await OnJobTimeoutAsync();
            return;
        }

        State.NextDispatchDelay = _backoff.NextDelay(State.NextDispatchDelay);
        await SaveAsync();
        DisposeDispatchTimer();
        _dispatchTimer = this.RegisterGrainTimer(
            _ => TryDispatchAsync(),
            State.NextDispatchDelay,
            TimeSpan.FromMilliseconds(-1));
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

        _log.LogWarning(
            "AgentJob {Id} report timeout after {Timeout}; transitioning to failed",
            Key, _options.JobTimeout);
        await EnterTerminalStateAsync(
            AgentJobStatus.Failed,
            null,
            AgentJobFailureReasons.ReportTimeout,
            AgentJobFailureReasons.ReportTimeout,
            AgentJobFailureReasons.ReportTimeout,
            $"report timeout after {_options.JobTimeout}",
            null,
            null,
            null);
    }

    private bool DispatchRetryBoundExceeded()
    {
        return State.Status == AgentJobStatus.Pending
            && State.SubmittedAt is not null
            && _timeProvider.GetUtcNow() >= State.SubmittedAt.Value + _backoff.TotalBound;
    }

    private bool JobTimeoutExceeded()
    {
        return State.RunnerId is not null
            && State.RunningSince is not null
            && _options.JobTimeout > TimeSpan.Zero
            && _timeProvider.GetUtcNow() >= State.RunningSince.Value + _options.JobTimeout;
    }

    /// <summary>
    /// Single canonical entry point for every AgentJob terminal transition.
    /// Persists a <see cref="PendingSessionClose"/> payload + records the
    /// stable delivery id before saving terminal state, then registers a
    /// durable <c>agent-job-recovery</c> reminder so the durable delivery
    /// to the AgentSession survives activation loss, process restart, and
    /// report replay. Idempotent for
    /// already-terminal jobs that still carry a pending delivery — the
    /// same delivery is retried so a redelivered report or a fresh
    /// activation converges on exactly one <c>session.closed</c> fact.
    /// </summary>
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
            // Already-terminal path: re-attach the same pending payload
            // (preserving the original delivery id + recorded timestamp)
            // so a redelivered report, an activation loss, or a reminder
            // tick all retry the original delivery and converge on a
            // single close fact. The failure-event obligation is also
            // re-attached on the same path so a freshly reactivated grain
            // finishes both durable writes before returning.
            State.PendingSessionClose ??= pending;
            if (State.PendingSessionClose is not null || State.PendingFailureEvent is not null)
                await EnsureRecoveryReminderAsync();
            await SaveAsync();
            if (State.PendingSessionClose is not null)
                await DeliverTerminalToSessionAsync(State.PendingSessionClose);
            if (State.PendingFailureEvent is not null)
                await EmitFailureEventAsync(State.PendingFailureEvent);
            return;
        }

        State.Status = terminalStatus;
        State.FailureReason = failureReason;
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

        if (terminalStatus == AgentJobStatus.Failed)
        {
            // Issue-491 D1: stage the durable failure-event obligation on
            // every failed terminal transition. A redelivery from
            // OnActivate / ReportResult retry / recovery-reminder tick
            // reuses the original EventId and collapses at the store
            // layer (source, eventId) uniqueness.
            State.PendingFailureEvent = new PendingFailureEvent(
                EventId: AgentJobSessionDeliveryIds.FailureEventId(Key),
                FailureReason: failureReason ?? pendingReason,
                FailureCategory: failureCategory,
                RecordedAt: _timeProvider.GetUtcNow());
        }

        DisposeDispatchTimer();
        DisposeJobTimeoutTimer();

        // Register before persisting terminal state. This makes the
        // reminder an orphan if the state write fails, which its next tick
        // cleans up, but avoids persisting a terminal close obligation that
        // has no durable wake-up after an activation loss.
        await EnsureRecoveryReminderAsync();
        await SaveAsync();

        _log.LogInformation(
            "AgentJob {Id} terminal: {Status} ({Reason}, category={Category}, deliveryId={DeliveryId})",
            Key, State.Status, State.FailureReason ?? "ok",
            State.PendingSessionClose?.FailureCategory ?? "-",
            State.PendingSessionClose?.DeliveryId ?? "-");

        await DeliverTerminalToSessionAsync(pending);
        if (State.PendingFailureEvent is not null)
            await EmitFailureEventAsync(State.PendingFailureEvent);
    }

    private async Task EmitFailureEventAsync(PendingFailureEvent obligation)
    {
        var envelope = BuildFailureEnvelope(obligation);
        try
        {
            await _eventStore.AppendAsync(envelope, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Issue-491 D1: leave the obligation in place; the recovery
            // reminder re-attempts on its next tick. Never surface a
            // terminal-delivery failure as an unobserved exception — the
            // Session-close delivery is the only consumer that must
            // succeed synchronously.
            _log.LogWarning(ex,
                "AgentJob {Id} failed to append {Type} event (eventId={EventId}); reminder will retry",
                Key, EventCatalog.ReverseDns.AgentJobFailed, obligation.EventId);
            await EnsureRecoveryReminderAsync();
            return;
        }
        State.PendingFailureEvent = null;
        await SaveAsync();
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
        var producerFamily = string.IsNullOrWhiteSpace(agentId)
            ? EventProducerFamily.RawAgentJob
            : EventProducerFamily.AgentJob;
        ProducerConformance.Assert(producerFamily, extensions, new ProducerLineageContext(
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

        var statusText = terminalStatus == AgentJobStatus.Completed ? "completed" : "failed";
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
            // No AgentSession to close. Clear that obligation while retaining
            // the reminder for a pending failure event, if one exists.
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
            // Leave PendingSessionClose and the reminder in place so
            // OnActivate, the reminder tick, or the next ReportResult
            // replay retries the same delivery until it succeeds.
        }
    }

    private async Task ClearPendingSessionCloseAndMaybeReminderAsync()
    {
        State.PendingSessionClose = null;
        await SaveAsync();
        if (State.PendingFailureEvent is not null)
            return;
        try
        {
            var reminder = await this.GetReminder(RecoveryReminderName);
            if (reminder is not null)
                await this.UnregisterReminder(reminder);
        }
        catch (Exception ex)
        {
            // A future reminder tick observes no pending payload and
            // unregisters itself; a transient reminder-service failure
            // here is recoverable.
            _log.LogDebug(ex,
                "AgentJob {Id} could not unregister recovery reminder; orphan tick will self-clean",
                Key);
        }
    }

    /// Durable reminder tick driving three recovery loops:
    /// terminal-delivery retry (issue-449 design decision 2),
    /// prepared-launch advancement (design decisions 1-3), and the
    /// failure-event emission retry (issue-491 design D1). A single
    /// reminder name covers all three so the grain keeps a durable
    /// wake-up until either Runner acceptance is persisted (preparation)
    /// or the Session-close acknowledgement clears the pending payload
    /// (terminal). The tick self-cleans only when no recoverable
    /// obligation is left — for failed agentless jobs that means waiting
    /// for the failure-event append to succeed even when there is no
    /// </summary>
    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (!string.Equals(reminderName, RecoveryReminderName, StringComparison.Ordinal))
            return;

        if (IsTerminal)
        {
            if (State.PendingSessionClose is not null)
                await DeliverTerminalToSessionAsync(State.PendingSessionClose);
            if (State.PendingFailureEvent is not null)
                await EmitFailureEventAsync(State.PendingFailureEvent);
            if (State.PendingSessionClose is null && State.PendingFailureEvent is null)
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

        if (State.RoutedPlan is not null && !State.RunnerAccepted)
        {
            await AdvancePreparedLaunchAsync();
            return;
        }

        // Non-terminal, non-prepared state: no recoverable obligation.
        // Reminder was registered before terminal save committed but
        // the save then failed; the orphan tick self-cleans.
        await UnregisterSelfAsync(reminderName);
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

    /// <summary>
    /// Runner failure category precedence:
    /// structured output <c>failureCategory</c> → <c>WorkResult.Error.Code</c>
    /// → report status. The output JSON is the most specific runner signal
    /// (e.g., <c>prompt_timeout</c>); the error code carries pre-execution
    /// classifications like <c>invalid-input</c>; the status is the
    /// coarsest fallback. All three are intentionally nullable — a
    /// successful close persists no category.
    /// </summary>
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

    private async Task SaveAsync()
    {
        await _state.WriteStateAsync();
        await MirrorToJobStoreAsync();
    }

    private async Task MirrorToJobStoreAsync()
    {
        if (_jobStore is null)
            return;

        try
        {
            var json = JsonSerializer.Serialize(State, Infrastructure.JSON.Options);
            await _jobStore.SaveAsync(Key, json);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "AgentJob {Id} mirror write to AgentJobs relational read model failed; grain state remains authoritative",
                Key);
        }
    }

    private static string StableWorkId(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return $"agent-work-{Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant()}";
    }

    private void DisposeDispatchTimer()
    {
        _dispatchTimer?.Dispose();
        _dispatchTimer = null;
    }

    private void DisposeJobTimeoutTimer()
    {
        _jobTimeoutTimer?.Dispose();
        _jobTimeoutTimer = null;
    }
}
