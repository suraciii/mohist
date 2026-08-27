using Mohist.Server.Contracts;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Agent.Grains;

public sealed partial class AgentJobGrain
{
    private const string ManagerRecoveryPrompt =
        "The previous Manager execution ended before its outcome was confirmed. Inspect the current resource state before taking any action; do not repeat the interrupted operation automatically.";

    private static bool IsManagerCredentialExpired(WorkResult result) =>
        string.Equals(result.ErrorCode, "manager-credential-expired", StringComparison.Ordinal);

    private async Task<AgentJobReportResult> ReportManagerCredentialExpiredAsync(WorkResult result)
    {
        if (!IsManagerInput())
            return new AgentJobReportResult(WorkReportVerdict.Refused, "invalid-manager-expiry-report");

        await EnterUnknownStateAsync("manager-credential-expired");
        if (State.PendingInitialTurnTerminalDelivery is { } pending)
            await DeliverInitialTurnTerminalAsync(pending);
        await EnsureManagerRecoveryAsync("manager-credential-expired");
        return new AgentJobReportResult(WorkReportVerdict.Accepted, "manager_credential_expired");
    }

    private async Task EnsureManagerRecoveryAsync(string reason)
    {
        if (State.ManagerExpiryRecovery is not null || State.ManagerRecovery is not null)
            return;

        var input = State.Input;
        var anchor = input?.SlackExecutionContext?.ReplyAnchor;
        if (input is null
            || anchor is null
            || !string.Equals(anchor.ProjectId, SlackDeliveryOwnerIds.ManagerProjectId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(input.AgentSessionId))
            return;

        // The interrupted execution's leases must die with the transition,
        // before the recovery turn can mint fresh credentials: a retained
        // bearer from the lost turn stays unusable during recovery. The
        // prefix revoke is idempotent and covers every attempt of this work.
        if (!string.IsNullOrWhiteSpace(State.WorkId))
            _managerCredentials.RevokeWork(Key, State.WorkId);

        var inputId = $"manager-recovery-input:{Key}";
        var turnId = $"manager-recovery-turn:{Key}";
        var session = GrainFactory.GetGrain<IAgentSessionGrain>(input.AgentSessionId);
        await session.RecordManagerRecoveryTurnAsync(new RecordFollowupTurnCommand(
            inputId,
            turnId,
            ManagerRecoveryPrompt,
            $"manager-recovery:{reason}",
            Provenance: new AgentSessionInputProvenance(
                "slack",
                anchor.WorkspaceId,
                anchor.ConversationId,
                anchor.ThreadRootMessageId,
                anchor.InitiatingMemberId,
                anchor.TriggeringMessageId,
                anchor.ConnectionId,
                anchor.ThreadRootMessageId)));

        State.ManagerRecovery = new ManagerRecoveryTransition(
            inputId,
            turnId,
            reason,
            _timeProvider.GetUtcNow());
        if (string.Equals(reason, "manager-credential-expired", StringComparison.Ordinal))
        {
            State.ManagerExpiryRecovery = new ManagerExpiryRecoveryTransition(
                inputId,
                turnId,
                _timeProvider.GetUtcNow());
        }
        await PersistAsync();
        await session.MarkInitialTurnTerminalAsync(Key, AgentTurnStatus.Unknown, null);
    }

    private bool IsManagerInput() =>
        State.Input?.SlackExecutionContext?.ReplyAnchor is { } anchor
        && string.Equals(anchor.ProjectId, SlackDeliveryOwnerIds.ManagerProjectId, StringComparison.Ordinal)
        && string.Equals(anchor.OwnerKind, SlackDeliveryOwnerKinds.Manager, StringComparison.Ordinal);

    private async Task<AgentJobReportResult> ReportUnknownResultAsync(WorkResult result)
    {
        // A restarted Runner proves only that this physical dispatch was
        // fenced locally without a durable result. Preserve that fact as
        // Unknown; never route it through terminal failure.
        var reason = string.IsNullOrWhiteSpace(result.Message)
            ? AgentJobFailureReasons.RunnerUnavailable
            : result.Message;
        await EnterUnknownStateAsync(reason);
        if (IsManagerInput())
        {
            if (State.PendingInitialTurnTerminalDelivery is { } pending)
                await DeliverInitialTurnTerminalAsync(pending);
            await EnsureManagerRecoveryAsync("manager-execution-unknown");
        }
        return new AgentJobReportResult(WorkReportVerdict.Accepted, "unknown");
    }

    private readonly TimeSpan _runnerLossRecoveryTimeout;

    private async Task OnJobTimeoutAsync()
    {
        if (IsTerminal || State.RunnerId is null)
            return;

        var runnerLost = await IsRunnerAwayAsync();
        var reason = runnerLost
            ? AgentJobFailureReasons.RunnerLost
            : $"{AgentJobFailureReasons.ReportTimeout}: report timeout after {_options.JobTimeout}";
        // A report timeout is initially Unknown because the Runner may still
        // produce authoritative evidence. If it never does, the same bounded
        // reconciliation window used for Runner loss turns the durable
        // timeout fact into a retryable Failed Turn.
        DateTimeOffset? recoveryDeadlineAt = _timeProvider.GetUtcNow() + _runnerLossRecoveryTimeout;
        var failureCategory = runnerLost
            ? AgentJobFailureReasons.RunnerLost
            : AgentJobFailureReasons.ReportTimeout;

        _log.LogWarning(
            "AgentJob {Id} report timeout after {Timeout}; transitioning to unknown with reason {Reason}",
            Key, _options.JobTimeout, reason);
        await EnterUnknownStateAsync(reason, recoveryDeadlineAt, failureCategory);
        if (IsManagerInput())
        {
            if (State.PendingInitialTurnTerminalDelivery is { } pending)
                await DeliverInitialTurnTerminalAsync(pending);
            await EnsureManagerRecoveryAsync(reason);
        }
    }

    private bool IsRecovering => State.Status == AgentJobStatus.Unknown
        && State.RecoveryDeadlineAt is { } deadline
        && deadline > _timeProvider.GetUtcNow();

    public Task MarkUnknownAsync(string reason) => EnterUnknownStateAsync(reason);

    public async Task MarkUnknownAsync(string reason, DateTimeOffset recoveryDeadlineAt)
    {
        await EnterUnknownStateAsync(reason, recoveryDeadlineAt);
        // Server-side Runner loss is the one unknown transition that no
        // Runner report will follow up on, so the Manager recovery turn must
        // be created here rather than waiting for a report that cannot come.
        if (IsManagerInput())
        {
            if (State.PendingInitialTurnTerminalDelivery is { } pending)
                await DeliverInitialTurnTerminalAsync(pending);
            await EnsureManagerRecoveryAsync(reason);
        }
    }

    internal async Task EnterUnknownStateAsync(
        string reason,
        DateTimeOffset? recoveryDeadlineAt = null,
        string? failureCategory = null)
    {
        if (IsTerminal)
            return;

        if (State.Status == AgentJobStatus.Unknown)
        {
            var changed = false;
            if (recoveryDeadlineAt is { } deadline
                && State.RecoveryDeadlineAt is null)
            {
                State.RecoveryDeadlineAt = deadline;
                State.FailureReason = reason;
                State.RecoveryFailureCategory = failureCategory ?? CanonicalRecoveryFailureCategory(reason);
                changed = true;
            }
            else if (!string.IsNullOrWhiteSpace(failureCategory)
                && string.IsNullOrWhiteSpace(State.RecoveryFailureCategory))
            {
                State.RecoveryFailureCategory = failureCategory;
                changed = true;
            }

            if (EnsureUnknownInitialTurnDelivery(State.FailureReason ?? reason))
                changed = true;
            await EnsureRecoveryReminderAsync();
            if (changed)
                await PersistAsync();
            return;
        }

        var previousStatus = State.Status;
        State.Status = AgentJobStatus.Unknown;
        State.FailureReason = reason;
        State.RecoveryDeadlineAt = recoveryDeadlineAt;
        State.RecoveryFailureCategory = failureCategory ?? CanonicalRecoveryFailureCategory(reason);
        State.RunningSince = null;
        State.TerminalResult = null;
        State.TerminalAt = null;

        DisposeJobTimeoutTimer();

        EnsureUnknownInitialTurnDelivery(reason);
        StageTerminalDeliveryEvent(AgentJobStatus.Unknown, reason, null, reason, "unknown", null, null);
        await EnsureRecoveryReminderAsync();
        await PersistAsync();
        if (State.PendingTerminalDeliveryEvent is not null)
            await EmitTerminalDeliveryEventAsync(State.PendingTerminalDeliveryEvent);

        _log.LogInformation(
            "AgentJob {Id} unknown: previous={Previous}, reason={Reason}, recoveryDeadlineAt={RecoveryDeadlineAt}",
            Key, previousStatus, reason, recoveryDeadlineAt);
    }

    private async Task<bool> FailRecoveringJobIfDueAsync()
    {
        if (State.Status != AgentJobStatus.Unknown
            || State.RecoveryDeadlineAt is not { } deadline
            || _timeProvider.GetUtcNow() < deadline)
        {
            return false;
        }

        var reason = State.FailureReason ?? AgentJobFailureReasons.RunnerLost;
        var failureCategory = State.RecoveryFailureCategory
            ?? CanonicalRecoveryFailureCategory(reason)
            ?? AgentJobFailureReasons.RunnerLost;
        await EnterTerminalStateAsync(
            AgentJobStatus.Failed,
            exitCode: 1,
            failureReason: reason,
            failureCategory: failureCategory,
            pendingReason: reason,
            message: reason,
            output: null,
            artifactUploadIds: null,
            terminalExitCode: 1);
        return true;
    }

    private async Task<bool> IsRunnerAwayAsync()
    {
        if (string.IsNullOrWhiteSpace(State.RunnerId))
            return false;

        try
        {
            var runtime = await GrainFactory
                .GetGrain<IRunnerGrain>(State.RunnerId)
                .GetRuntimeStateAsync();
            return runtime.Status != RunnerStatus.Online;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex,
                "AgentJob {Id} could not read runner {Runner} during report-timeout reconciliation; treating it as lost",
                Key,
                State.RunnerId);
            return true;
        }
    }

    private static string? CanonicalRecoveryFailureCategory(string? reason) =>
        string.Equals(reason, AgentJobFailureReasons.RunnerLost, StringComparison.Ordinal)
            ? AgentJobFailureReasons.RunnerLost
            : reason?.StartsWith($"{AgentJobFailureReasons.ReportTimeout}:", StringComparison.Ordinal) == true
                ? AgentJobFailureReasons.ReportTimeout
                : null;

    private static TimeSpan ValidateRunnerLossRecoveryTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.FromMinutes(2))
            throw new InvalidOperationException(
                "AgentJob RunnerLossRecoveryTimeout must be longer than the two-minute runner presence timeout.");
        return timeout;
    }


    private async Task EnsureRecoveryReminderAsync()
    {
        await this.RegisterOrUpdateReminder(
            RecoveryReminderName,
            RecoveryReminderDue,
            RecoveryReminderPeriod);
    }
}
