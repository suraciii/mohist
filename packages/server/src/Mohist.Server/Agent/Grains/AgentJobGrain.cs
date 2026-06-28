using System.Text.Json;
using Microsoft.Extensions.Options;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Serialization;
using Mohist.Server.Runner.Grains;

namespace Mohist.Server.Agent.Grains;

/// <summary>
/// Authoritative owner of a standalone agent job's lifecycle and terminal result.
/// In-memory only (no <c>[PersistentState]</c>); non-reentrant so the backoff timer
/// and <see cref="ReportResultAsync"/> cannot race on the lifecycle fields. Dispatches
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

    private AgentJobStatus _status = AgentJobStatus.Pending;
    private string? _runnerId;
    private string? _workId;
    private string? _failureReason;
    private AgentJobTerminalResult? _terminalResult;

    private AgentJobInput? _input;
    private DateTimeOffset? _submittedAt;
    private DateTimeOffset? _runningSince;
    private IDisposable? _dispatchTimer;
    private IDisposable? _jobTimeoutTimer;
    private TimeSpan _nextDispatchDelay;
    private int _dispatchAttempts;

    public AgentJobGrain(ILogger<AgentJobGrain> log, IOptions<AgentJobOptions> options, TimeProvider timeProvider)
    {
        _log = log;
        _options = options.Value;
        _timeProvider = timeProvider;
        // The backoff schedule is captured at activation time from the current
        // snapshot of AgentJobOptions. Hot-reload of the configuration section
        // is not applied to an already-active grain; it takes effect on the
        // next activation. (Switching to IOptionsMonitor would not change this
        // because Orleans instantiates grains per-activation, not per-call.)
        _backoff = _options.ResolveBackoffSchedule();
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

    public Task<AgentJobStatus> GetStatusAsync() => Task.FromResult(_status);

    public Task<string?> GetCurrentWorkIdAsync() => Task.FromResult(_workId);

    public Task<AgentJobRuntimeSnapshot> GetRuntimeSnapshotAsync() =>
        Task.FromResult(new AgentJobRuntimeSnapshot(_status, _runnerId, _workId, _failureReason, _dispatchAttempts));

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
        if (_terminalResult is not null)
            return Task.FromResult(_terminalResult);

        return Task.FromResult(new AgentJobTerminalResult(
            _status, null, null, null, _failureReason, null));
    }

    public Task AssignRunnerAsync(string runnerId, string workId)
    {
        if (_status != AgentJobStatus.Pending)
            throw new InvalidOperationException(
                $"AgentJob '{Key}' cannot accept runner assignment in status {_status}");

        AssignRunnerInternal(runnerId, workId);
        return Task.CompletedTask;
    }

    private void AssignRunnerInternal(string runnerId, string workId)
    {
        _status = AgentJobStatus.Running;
        _runnerId = runnerId;
        _workId = workId;
        _runningSince = _timeProvider.GetUtcNow();
        ArmJobTimeout();
    }

    public Task<bool> IsWorkRunnableAsync(string runnerId, string workId)
    {
        return Task.FromResult(
            _status == AgentJobStatus.Running
            && string.Equals(_runnerId, runnerId, StringComparison.Ordinal)
            && string.Equals(_workId, workId, StringComparison.Ordinal));
    }

    public Task<AgentJobReportResult> ReportResultAsync(string runnerId, string workId, WorkResult result)
    {
        if (_status == AgentJobStatus.Completed || _status == AgentJobStatus.Failed)
        {
            _log.LogDebug(
                "AgentJob {Id} rejecting report from {Runner} for {Work}: already in terminal {Status}",
                Key, runnerId, workId, _status);
            return Task.FromResult(new AgentJobReportResult(false, "already-terminal"));
        }

        if (_status != AgentJobStatus.Running)
        {
            _log.LogWarning(
                "AgentJob {Id} rejecting report from {Runner} for {Work}: unexpected status {Status}",
                Key, runnerId, workId, _status);
            return Task.FromResult(new AgentJobReportResult(false, "not-running"));
        }

        if (!string.Equals(_runnerId, runnerId, StringComparison.Ordinal)
            || !string.Equals(_workId, workId, StringComparison.Ordinal))
        {
            _log.LogWarning(
                "AgentJob {Id} rejecting report from {Runner} for {Work}: expected {ExpectedRunner}/{ExpectedWork}",
                Key, runnerId, workId, _runnerId, _workId);
            return Task.FromResult(new AgentJobReportResult(false, "runner-or-work-mismatch"));
        }

        var isSuccess = string.Equals(result.Status, "completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(result.Status, "pass", StringComparison.OrdinalIgnoreCase)
            || string.Equals(result.Status, "ok", StringComparison.OrdinalIgnoreCase)
            || string.Equals(result.Status, "success", StringComparison.OrdinalIgnoreCase);

        _status = isSuccess ? AgentJobStatus.Completed : AgentJobStatus.Failed;
        if (!isSuccess)
            _failureReason = string.IsNullOrWhiteSpace(result.Message)
                ? result.Status
                : result.Message;

        _terminalResult = new AgentJobTerminalResult(
            _status,
            result.Message,
            result.Output,
            result.ArtifactUploadIds,
            _failureReason,
            result.ExitCode);

        DisposeDispatchTimer();
        DisposeJobTimeoutTimer();

        _log.LogInformation(
            "AgentJob {Id} terminal: {Status} ({Reason})",
            Key, _status, _failureReason ?? "ok");

        return Task.FromResult(new AgentJobReportResult(true));
    }

    public Task FailAsync(string reason)
    {
        if (_status == AgentJobStatus.Completed || _status == AgentJobStatus.Failed)
            return Task.CompletedTask;

        _status = AgentJobStatus.Failed;
        _failureReason = reason;
        _runningSince = null;
        _terminalResult ??= new AgentJobTerminalResult(
            _status, reason, null, null, reason, null);
        DisposeDispatchTimer();
        DisposeJobTimeoutTimer();

        _log.LogInformation(
            "AgentJob {Id} forced to failed: {Reason}",
            Key, reason);

        return Task.CompletedTask;
    }

    public Task SubmitAsync(AgentJobInput input)
    {
        if (_status != AgentJobStatus.Pending)
            throw new InvalidOperationException(
                $"AgentJob '{Key}' cannot be re-submitted; current status is {_status}");

        if (input is null || string.IsNullOrWhiteSpace(input.Prompt))
            throw new ArgumentException("AgentJobInput.Prompt is required", nameof(input));

        _input = input;
        _submittedAt = _timeProvider.GetUtcNow();
        _nextDispatchDelay = TimeSpan.Zero;
        _dispatchAttempts = 0;
        _ = TryDispatchAsync();
        return Task.CompletedTask;
    }

    public async Task CheckTimeoutsAsync()
    {
        if (_status == AgentJobStatus.Pending && DispatchRetryBoundExceeded())
        {
            await FailWithReasonAsync(AgentJobFailureReasons.RunnerUnavailable);
            return;
        }

        if (_status == AgentJobStatus.Pending)
        {
            await TryDispatchAsync();
            return;
        }

        if (_status == AgentJobStatus.Running && JobTimeoutExceeded())
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
        if (_status != AgentJobStatus.Pending || _input is null || _submittedAt is null)
            return;

        if (DispatchRetryBoundExceeded())
        {
            await FailWithReasonAsync(AgentJobFailureReasons.RunnerUnavailable);
            return;
        }

        _dispatchAttempts++;
        var projectId = _input.ProjectId ?? string.Empty;
        var registry = GrainFactory.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        var runners = await registry.ListEligibleRunnersAsync(projectId);
        if (runners.Count == 0)
        {
            await ScheduleNextDispatchAsync();
            return;
        }

        foreach (var runnerInfo in runners)
        {
            if (_status != AgentJobStatus.Pending)
                return;
            if (await TryAssignToRunnerAsync(runnerInfo))
                return;
        }

        await ScheduleNextDispatchAsync();
    }

    private async Task<bool> TryAssignToRunnerAsync(RunnerInfo runnerInfo)
    {
        var runner = GrainFactory.GetGrain<IRunnerGrain>(runnerInfo.RunnerId);
        var state = await runner.GetRuntimeStateAsync();
        if (state.Status != RunnerStatus.Online)
            return false;

        // Slots are sourced from the runner grain (which reads from the
        // persisted definition state). The runner-reported MaxWorkflowSlots
        // on RunnerInfo is non-authoritative and intentionally ignored here.
        var maxSlots = await runner.GetSlotsAsync();
        var activeWorkCount = state.ActiveWorks
            .Select(w => w.OwnerId)
            .Distinct(StringComparer.Ordinal)
            .Count();
        if (activeWorkCount >= maxSlots)
            return false;

        var workId = $"agent-work-{Guid.NewGuid():N}";

        if (_status != AgentJobStatus.Pending)
            return false;
        AssignRunnerInternal(runnerInfo.RunnerId, workId);

        RunnerWorkAssignmentResult result;
        try
        {
            var dispatch = BuildDispatch(workId);
            result = await runner.AssignAgentJobAsync(dispatch);
        }
        catch
        {
            await FailWithReasonAsync(AgentJobFailureReasons.RunnerUnavailable);
            throw;
        }

        if (result.Status != RunnerWorkAssignmentStatus.Assigned)
        {
            await FailWithReasonAsync(AgentJobFailureReasons.RunnerUnavailable);
            return false;
        }

        DisposeDispatchTimer();
        _log.LogInformation(
            "AgentJob {Id} assigned to runner {Runner} as work {Work}",
            Key, runnerInfo.RunnerId, workId);
        return true;
    }

    private WorkDispatch BuildDispatch(string workId)
    {
        var input = _input!;
        var payload = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(input.WorkspacePath))
            payload["workspace"] = JSON.SerializeToElement(
                new { path = input.WorkspacePath });

        var variablesJson = payload.Count == 0
            ? null
            : JSON.Serialize(payload);

        var with = new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
        {
            ["prompt"] = JSON.SerializeToElement(input.Prompt),
        };
        if (!string.IsNullOrWhiteSpace(input.Model))
            with["model"] = JSON.SerializeToElement(input.Model);
        var withJson = JSON.Serialize(with);

        return new WorkDispatch(
            WorkflowRunId: string.Empty,
            WorkId: workId,
            Uses: string.IsNullOrWhiteSpace(input.Uses) ? "mohist/acp-agent" : input.Uses,
            With: withJson,
            Variables: variablesJson,
            WorkType: "agent-job",
            Stage: "agent",
            Title: "Agent Job",
            OwnerKind: WorkDispatchOwnerKinds.AgentJob,
            AgentJobId: Key);
    }

    private async Task ScheduleNextDispatchAsync()
    {
        if (_status != AgentJobStatus.Pending || _input is null || _submittedAt is null)
            return;

        if (DispatchRetryBoundExceeded())
        {
            await FailWithReasonAsync(AgentJobFailureReasons.RunnerUnavailable);
            return;
        }

        _nextDispatchDelay = _backoff.NextDelay(_nextDispatchDelay);
        DisposeDispatchTimer();
        _dispatchTimer = this.RegisterGrainTimer(
            _ => TryDispatchAsync(),
            _nextDispatchDelay,
            TimeSpan.FromMilliseconds(-1));
    }

    private void ArmJobTimeout()
    {
        if (_options.JobTimeout <= TimeSpan.Zero)
            return;

        DisposeJobTimeoutTimer();
        _jobTimeoutTimer = this.RegisterGrainTimer(
            _ => OnJobTimeoutAsync(),
            _options.JobTimeout,
            TimeSpan.FromMilliseconds(-1));
    }

    private async Task OnJobTimeoutAsync()
    {
        if (_status != AgentJobStatus.Running || !JobTimeoutExceeded())
            return;

        _log.LogWarning(
            "AgentJob {Id} report timeout after {Timeout}; transitioning to failed",
            Key, _options.JobTimeout);
        await FailWithReasonAsync(AgentJobFailureReasons.ReportTimeout);
    }

    private bool DispatchRetryBoundExceeded()
    {
        return _status == AgentJobStatus.Pending
            && _submittedAt is not null
            && _timeProvider.GetUtcNow() >= _submittedAt.Value + _backoff.TotalBound;
    }

    private bool JobTimeoutExceeded()
    {
        return _status == AgentJobStatus.Running
            && _runningSince is not null
            && _options.JobTimeout > TimeSpan.Zero
            && _timeProvider.GetUtcNow() >= _runningSince.Value + _options.JobTimeout;
    }

    private Task FailWithReasonAsync(string reason)
    {
        if (_status == AgentJobStatus.Completed || _status == AgentJobStatus.Failed)
            return Task.CompletedTask;

        _status = AgentJobStatus.Failed;
        _failureReason = reason;
        _runningSince = null;
        _terminalResult ??= new AgentJobTerminalResult(
            _status, reason, null, null, reason, null);
        DisposeDispatchTimer();
        DisposeJobTimeoutTimer();
        return Task.CompletedTask;
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
