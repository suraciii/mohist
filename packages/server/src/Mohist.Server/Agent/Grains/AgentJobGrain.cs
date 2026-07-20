using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Serialization;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Grains;
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
/// </summary>
public sealed class AgentJobGrain : Grain, IAgentJobGrain
{
    private readonly ILogger<AgentJobGrain> _log;
    private readonly AgentJobOptions _options;
    private readonly AgentJobBackoffSchedule _backoff;
    private readonly TimeProvider _timeProvider;
    private readonly IPersistentState<AgentJobState> _state;
    private readonly IAgentJobDispatchObserver _dispatchObserver;
    private IDisposable? _dispatchTimer;
    private IDisposable? _jobTimeoutTimer;

    public AgentJobGrain(
        ILogger<AgentJobGrain> log,
        IOptions<AgentJobOptions> options,
        TimeProvider timeProvider,
        [PersistentState("agent-job")] IPersistentState<AgentJobState> state,
        IAgentJobDispatchObserver? dispatchObserver = null)
    {
        _log = log;
        _options = options.Value;
        _timeProvider = timeProvider;
        _state = state;
        _dispatchObserver = dispatchObserver ?? NoopAgentJobDispatchObserver.Instance;
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

        if (State.Input is null || IsTerminal)
            return;

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
            State.RunnerAccepted));

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

        State.Status = isSuccess ? AgentJobStatus.Completed : AgentJobStatus.Failed;
        if (!isSuccess)
            State.FailureReason = string.IsNullOrWhiteSpace(result.Message)
                ? result.Status
                : result.Message;

        State.TerminalResult = new AgentJobTerminalResult(
            State.Status,
            result.Message,
            result.Output,
            result.ArtifactUploadIds,
            State.FailureReason,
            result.ExitCode);
        State.RunningSince = null;

        DisposeDispatchTimer();
        DisposeJobTimeoutTimer();
        await SaveAsync();

        _log.LogInformation(
            "AgentJob {Id} terminal: {Status} ({Reason})",
            Key, State.Status, State.FailureReason ?? "ok");

        if (isSuccess)
            await CloseGenericSessionAsync("completed", result.ExitCode ?? 0, null, null, null);
        else
            await CloseGenericSessionAsync(
                "failed",
                result.ExitCode ?? 1,
                State.FailureReason,
                FailureCategoryFrom(result.Output) ?? result.Status,
                State.FailureReason);

        return new AgentJobReportResult(true);
    }

    public async Task FailAsync(string reason)
    {
        if (IsTerminal)
            return;

        State.Status = AgentJobStatus.Failed;
        State.FailureReason = reason;
        State.RunningSince = null;
        State.TerminalResult ??= new AgentJobTerminalResult(
            State.Status, reason, null, null, reason, null);
        DisposeDispatchTimer();
        DisposeJobTimeoutTimer();
        await SaveAsync();

        _log.LogInformation(
            "AgentJob {Id} forced to failed: {Reason}",
            Key, reason);
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

    private static bool EquivalentInput(AgentJobInput left, AgentJobInput right) =>
        string.Equals(left.Prompt, right.Prompt, StringComparison.Ordinal)
        && string.Equals(left.Model, right.Model, StringComparison.Ordinal)
        && string.Equals(left.WorkspacePath, right.WorkspacePath, StringComparison.Ordinal)
        && string.Equals(left.ProjectId, right.ProjectId, StringComparison.Ordinal)
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
            await FailWithReasonAsync(AgentJobFailureReasons.RunnerUnavailable);
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
            await FailWithReasonAsync(AgentJobFailureReasons.RunnerUnavailable);
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
            await FailWithReasonAsync(AgentJobFailureReasons.RunnerUnavailable);
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
        await FailWithReasonAsync(AgentJobFailureReasons.ReportTimeout);
    }

    private async Task CloseGenericSessionOnFailureAsync(string reason)
        => await CloseGenericSessionAsync(
            "failed",
            null,
            $"agent-job-{reason} ({Key})",
            reason,
            reason);

    private async Task CloseGenericSessionAsync(
        string status,
        int? exitCode,
        string? failureReason,
        string? failureCategory,
        string? reason)
    {
        var sessionId = State.Input?.AgentSessionId;
        var runtimeSessionId = State.RuntimeSessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        try
        {
            var grain = GrainFactory.GetGrain<IAgentSessionGrain>(sessionId);
            var payload = JSON.Serialize(new Dictionary<string, object?>
            {
                ["status"] = status,
                ["exitCode"] = exitCode,
                ["failureReason"] = failureReason,
                ["failureCategory"] = failureCategory,
                ["reason"] = reason,
                ["recordedAt"] = _timeProvider.GetUtcNow().ToString("o"),
            });
            var close = new[] { new AgentSessionRuntimeEventInput("session.closed", payload) };
            if (string.IsNullOrWhiteSpace(runtimeSessionId))
            {
                var session = await grain.GetAsync();
                if (string.IsNullOrWhiteSpace(session?.AgentSessionId))
                    await grain.AppendSystemEventsAsync(new AppendAgentSessionSystemEventsCommand(close));
                return;
            }
            await grain.AppendRuntimeEventsAsync(
                new AppendAgentSessionRuntimeEventsCommand(close, runtimeSessionId));
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "AgentJob {Id} failed to close generic session {SessionId} with status {Status}",
                Key, sessionId, status);
        }
    }

    private static string? FailureCategoryFrom(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;

        try
        {
            using var document = JsonDocument.Parse(output);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("failureCategory", out var category)
                && category.ValueKind == JsonValueKind.String
                ? category.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
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

    private async Task FailWithReasonAsync(string reason)
    {
        if (IsTerminal)
            return;

        State.Status = AgentJobStatus.Failed;
        State.FailureReason = reason;
        State.RunningSince = null;
        State.TerminalResult ??= new AgentJobTerminalResult(
            State.Status, reason, null, null, reason, null);
        DisposeDispatchTimer();
        DisposeJobTimeoutTimer();
        await SaveAsync();
        await CloseGenericSessionOnFailureAsync(reason);
    }

    private Task SaveAsync() => _state.WriteStateAsync();

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
