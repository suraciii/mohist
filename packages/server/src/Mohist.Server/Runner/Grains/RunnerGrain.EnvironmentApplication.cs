namespace Mohist.Server.Runner.Grains;

public partial class RunnerGrain
{
    public async Task<RunnerEnvironmentApplicationBeginResult?> BeginEnvironmentApplicationAsync(
        string updateId,
        string targetVersion,
        string processGeneration,
        string connectionGeneration)
    {
        var requestedId = NormalizeUpdateInterruptId(updateId)
            ?? throw new ArgumentException("environment update id must be a UUID", nameof(updateId));
        var requestedVersion = NormalizeEnvironmentVersion(targetVersion)
            ?? throw new ArgumentException("environment version is required", nameof(targetVersion));
        var requestedProcessGeneration = NormalizeRequiredIdentity(processGeneration, nameof(processGeneration));
        var requestedConnectionGeneration = NormalizeRequiredIdentity(connectionGeneration, nameof(connectionGeneration));

        await _lifecycleGate.WaitAsync();
        try
        {
            if (_status != RunnerStatus.Online
                || _info is null
                || !string.Equals(_state.State?.CurrentProcessGeneration, requestedProcessGeneration, StringComparison.Ordinal)
                || !string.Equals(_info.ConnectionGeneration, requestedConnectionGeneration, StringComparison.Ordinal))
                return null;

            var state = _state.State ??= new RunnerState();
            var existing = state.EnvironmentApplication;
            if (existing is not null && string.Equals(existing.UpdateId, requestedId, StringComparison.Ordinal))
            {
                var sameIdStatus = existing.Phase is RunnerEnvironmentApplicationPhase.Waiting
                    or RunnerEnvironmentApplicationPhase.Applying
                    or RunnerEnvironmentApplicationPhase.Unconfirmed
                    ? RunnerEnvironmentApplicationBeginStatus.AlreadyPending
                    : RunnerEnvironmentApplicationBeginStatus.AlreadyCompleted;
                return new RunnerEnvironmentApplicationBeginResult(
                    requestedId,
                    sameIdStatus,
                    await BuildEnvironmentApplicationSnapshotAsync(existing));
            }

            if (existing is not null
                && existing.Phase is (RunnerEnvironmentApplicationPhase.Waiting
                    or RunnerEnvironmentApplicationPhase.Applying
                    or RunnerEnvironmentApplicationPhase.Unconfirmed))
            {
                return new RunnerEnvironmentApplicationBeginResult(
                    requestedId,
                    RunnerEnvironmentApplicationBeginStatus.Conflict,
                    await BuildEnvironmentApplicationSnapshotAsync(existing));
            }

            var previousFence = state.UpdateInterruptFence;
            var previousFencePendingId = previousFence?.PendingId;
            var previousFenceLastCancelledId = previousFence?.LastCancelledId;
            var previousFenceKind = previousFence?.Kind;
            var previousDraining = _draining;
            var fence = UpdateInterruptFence();
            if (!string.IsNullOrWhiteSpace(fence.PendingId))
            {
                return new RunnerEnvironmentApplicationBeginResult(
                    requestedId,
                    RunnerEnvironmentApplicationBeginStatus.Conflict,
                    existing is null ? null : await BuildEnvironmentApplicationSnapshotAsync(existing));
            }

            var now = _timeProvider.GetUtcNow();
            state.EnvironmentApplication = new RunnerEnvironmentApplication
            {
                UpdateId = requestedId,
                TargetVersion = requestedVersion,
                PreviousVersion = _info.EnvironmentVersion,
                BaseProcessGeneration = requestedProcessGeneration,
                BaseConnectionGeneration = requestedConnectionGeneration,
                Phase = RunnerEnvironmentApplicationPhase.Waiting,
                RequestedAt = now,
            };
            fence.PendingId = requestedId;
            fence.LastCancelledId = null;
            fence.Kind = RunnerUpdateInterruptKinds.EnvironmentApplication;
            _draining = true;
            try
            {
                await PersistAsync();
            }
            catch
            {
                state.EnvironmentApplication = existing;
                if (previousFence is null)
                {
                    state.UpdateInterruptFence = null;
                }
                else
                {
                    previousFence.PendingId = previousFencePendingId;
                    previousFence.LastCancelledId = previousFenceLastCancelledId;
                    previousFence.Kind = previousFenceKind;
                }
                _draining = previousDraining;
                throw;
            }

            PublishStatusObservation();
            return new RunnerEnvironmentApplicationBeginResult(
                requestedId,
                RunnerEnvironmentApplicationBeginStatus.Waiting,
                await BuildEnvironmentApplicationSnapshotAsync(state.EnvironmentApplication));
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<RunnerEnvironmentApplicationCommandResult> GetEnvironmentApplicationAsync(string updateId)
    {
        var requestedId = NormalizeUpdateInterruptId(updateId)
            ?? throw new ArgumentException("environment update id must be a UUID", nameof(updateId));
        await _lifecycleGate.WaitAsync();
        try
        {
            var application = _state.State?.EnvironmentApplication;
            if (application is null || !string.Equals(application.UpdateId, requestedId, StringComparison.Ordinal))
                return new(requestedId, RunnerEnvironmentApplicationCommandStatus.NotFound, null);
            return new(
                requestedId,
                RunnerEnvironmentApplicationCommandStatus.Accepted,
                await BuildEnvironmentApplicationSnapshotAsync(application));
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<RunnerEnvironmentApplicationCommandResult> BeginEnvironmentApplyAsync(
        string updateId,
        string processGeneration)
    {
        var requestedId = NormalizeUpdateInterruptId(updateId)
            ?? throw new ArgumentException("environment update id must be a UUID", nameof(updateId));
        var requestedProcessGeneration = NormalizeRequiredIdentity(processGeneration, nameof(processGeneration));

        await _lifecycleGate.WaitAsync();
        try
        {
            var application = CurrentEnvironmentApplication(requestedId);
            if (application is null)
                return new(requestedId, RunnerEnvironmentApplicationCommandStatus.NotFound, null);
            if (application.Phase != RunnerEnvironmentApplicationPhase.Waiting)
            {
                if (application.Phase != RunnerEnvironmentApplicationPhase.Applying)
                    return await EnvironmentCommandResultAsync(requestedId, RunnerEnvironmentApplicationCommandStatus.Conflict, application);
                if (!OwnsEnvironmentFence(requestedId))
                    return await EnvironmentCommandResultAsync(requestedId, RunnerEnvironmentApplicationCommandStatus.Conflict, application);
                if (_status != RunnerStatus.Online
                    || !string.Equals(_state.State?.CurrentProcessGeneration, requestedProcessGeneration, StringComparison.Ordinal))
                    return await EnvironmentCommandResultAsync(requestedId, RunnerEnvironmentApplicationCommandStatus.Stale, application);
                return await EnvironmentCommandResultAsync(requestedId, RunnerEnvironmentApplicationCommandStatus.Accepted, application);
            }
            if (!OwnsEnvironmentFence(requestedId))
                return await EnvironmentCommandResultAsync(requestedId, RunnerEnvironmentApplicationCommandStatus.Conflict, application);
            if (_status != RunnerStatus.Online
                || !string.Equals(_state.State?.CurrentProcessGeneration, requestedProcessGeneration, StringComparison.Ordinal))
                return await EnvironmentCommandResultAsync(requestedId, RunnerEnvironmentApplicationCommandStatus.Stale, application);

            var settlement = await ReadEnvironmentSettlementAsync(requestedProcessGeneration);
            if (!settlement.Settled)
                return await EnvironmentCommandResultAsync(requestedId, RunnerEnvironmentApplicationCommandStatus.NotSettled, application);

            await PersistEnvironmentApplicationMutationAsync(
                application,
                () => application.Phase = RunnerEnvironmentApplicationPhase.Applying);
            return await EnvironmentCommandResultAsync(requestedId, RunnerEnvironmentApplicationCommandStatus.Accepted, application);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<RunnerEnvironmentApplicationCommandResult> ConfirmEnvironmentApplicationAsync(
        string updateId,
        string processGeneration,
        string environmentVersion)
    {
        var requestedId = NormalizeUpdateInterruptId(updateId)
            ?? throw new ArgumentException("environment update id must be a UUID", nameof(updateId));
        var requestedProcessGeneration = NormalizeRequiredIdentity(processGeneration, nameof(processGeneration));
        var requestedVersion = NormalizeEnvironmentVersion(environmentVersion)
            ?? throw new ArgumentException("environment version is required", nameof(environmentVersion));

        await _lifecycleGate.WaitAsync();
        try
        {
            var application = CurrentEnvironmentApplication(requestedId);
            if (application is null)
                return new(requestedId, RunnerEnvironmentApplicationCommandStatus.NotFound, null);
            if (application.Phase == RunnerEnvironmentApplicationPhase.Active)
                return await EnvironmentCommandResultAsync(requestedId, RunnerEnvironmentApplicationCommandStatus.AlreadyApplied, application);
            if (application.Phase != RunnerEnvironmentApplicationPhase.Applying)
                return await EnvironmentCommandResultAsync(requestedId, RunnerEnvironmentApplicationCommandStatus.Conflict, application);
            if (_status != RunnerStatus.Online
                || !string.Equals(_state.State?.CurrentProcessGeneration, requestedProcessGeneration, StringComparison.Ordinal))
                return await EnvironmentCommandResultAsync(requestedId, RunnerEnvironmentApplicationCommandStatus.Stale, application);
            if (!string.Equals(_info?.EnvironmentVersion, requestedVersion, StringComparison.Ordinal)
                || !string.Equals(application.TargetVersion, requestedVersion, StringComparison.Ordinal))
                return await EnvironmentCommandResultAsync(requestedId, RunnerEnvironmentApplicationCommandStatus.VersionMismatch, application);

            if (!OwnsEnvironmentFence(requestedId))
                return await EnvironmentCommandResultAsync(requestedId, RunnerEnvironmentApplicationCommandStatus.Conflict, application);

            await PersistEnvironmentApplicationMutationAsync(
                application,
                () =>
                {
                    application.Phase = RunnerEnvironmentApplicationPhase.Active;
                    application.CompletedAt = _timeProvider.GetUtcNow();
                    ClearEnvironmentFence(requestedId, cancelled: false);
                },
                refreshDrain: true);
            return await EnvironmentCommandResultAsync(requestedId, RunnerEnvironmentApplicationCommandStatus.Accepted, application);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<RunnerEnvironmentApplicationCommandResult> FailEnvironmentApplicationAsync(
        string updateId,
        string failureCode)
    {
        var requestedId = NormalizeUpdateInterruptId(updateId)
            ?? throw new ArgumentException("environment update id must be a UUID", nameof(updateId));
        var normalizedFailure = NormalizeFailureCode(failureCode)
            ?? throw new ArgumentException("failure code is required", nameof(failureCode));

        await _lifecycleGate.WaitAsync();
        try
        {
            var application = CurrentEnvironmentApplication(requestedId);
            if (application is null)
                return new(requestedId, RunnerEnvironmentApplicationCommandStatus.NotFound, null);
            if (application.Phase == RunnerEnvironmentApplicationPhase.Failed)
                return await EnvironmentCommandResultAsync(requestedId, RunnerEnvironmentApplicationCommandStatus.Failed, application);
            if (application.Phase != RunnerEnvironmentApplicationPhase.Applying)
                return await EnvironmentCommandResultAsync(requestedId, RunnerEnvironmentApplicationCommandStatus.Conflict, application);
            if (!OwnsEnvironmentFence(requestedId))
                return await EnvironmentCommandResultAsync(requestedId, RunnerEnvironmentApplicationCommandStatus.Conflict, application);

            await PersistEnvironmentApplicationMutationAsync(
                application,
                () =>
                {
                    application.FailureCode = normalizedFailure;
                    application.Phase = RunnerEnvironmentApplicationPhase.Failed;
                });
            return await EnvironmentCommandResultAsync(requestedId, RunnerEnvironmentApplicationCommandStatus.Failed, application);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<RunnerEnvironmentApplicationCommandResult> ConfirmEnvironmentRollbackAsync(
        string updateId,
        string processGeneration,
        string environmentVersion)
    {
        var requestedId = NormalizeUpdateInterruptId(updateId)
            ?? throw new ArgumentException("environment update id must be a UUID", nameof(updateId));
        var requestedProcessGeneration = NormalizeRequiredIdentity(processGeneration, nameof(processGeneration));
        var requestedVersion = NormalizeEnvironmentVersion(environmentVersion)
            ?? throw new ArgumentException("environment version is required", nameof(environmentVersion));

        await _lifecycleGate.WaitAsync();
        try
        {
            var application = CurrentEnvironmentApplication(requestedId);
            if (application is null)
                return new(requestedId, RunnerEnvironmentApplicationCommandStatus.NotFound, null);
            if (application.Phase == RunnerEnvironmentApplicationPhase.Failed
                && application.CompletedAt is not null
                && !OwnsEnvironmentFence(requestedId))
            {
                return await EnvironmentCommandResultAsync(requestedId, RunnerEnvironmentApplicationCommandStatus.AlreadyRolledBack, application);
            }
            if (application.Phase is not (RunnerEnvironmentApplicationPhase.Failed or RunnerEnvironmentApplicationPhase.Unconfirmed))
                return await EnvironmentCommandResultAsync(requestedId, RunnerEnvironmentApplicationCommandStatus.Conflict, application);
            if (_status != RunnerStatus.Online
                || !string.Equals(_state.State?.CurrentProcessGeneration, requestedProcessGeneration, StringComparison.Ordinal))
                return await EnvironmentCommandResultAsync(requestedId, RunnerEnvironmentApplicationCommandStatus.Stale, application);
            if (!string.Equals(application.PreviousVersion, requestedVersion, StringComparison.Ordinal)
                || !string.Equals(_info?.EnvironmentVersion, requestedVersion, StringComparison.Ordinal))
                return await EnvironmentCommandResultAsync(requestedId, RunnerEnvironmentApplicationCommandStatus.VersionMismatch, application);
            if (!OwnsEnvironmentFence(requestedId))
                return await EnvironmentCommandResultAsync(requestedId, RunnerEnvironmentApplicationCommandStatus.Conflict, application);

            await PersistEnvironmentApplicationMutationAsync(
                application,
                () =>
                {
                    application.CompletedAt = _timeProvider.GetUtcNow();
                    application.Phase = RunnerEnvironmentApplicationPhase.Failed;
                    ClearEnvironmentFence(requestedId, cancelled: false);
                },
                refreshDrain: true);
            return await EnvironmentCommandResultAsync(requestedId, RunnerEnvironmentApplicationCommandStatus.Accepted, application);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<RunnerEnvironmentApplicationCommandResult> MarkEnvironmentApplicationUnconfirmedAsync(
        string updateId,
        string failureCode)
    {
        var requestedId = NormalizeUpdateInterruptId(updateId)
            ?? throw new ArgumentException("environment update id must be a UUID", nameof(updateId));
        var normalizedFailure = NormalizeFailureCode(failureCode)
            ?? throw new ArgumentException("failure code is required", nameof(failureCode));

        await _lifecycleGate.WaitAsync();
        try
        {
            var application = CurrentEnvironmentApplication(requestedId);
            if (application is null)
                return new(requestedId, RunnerEnvironmentApplicationCommandStatus.NotFound, null);
            if (application.Phase == RunnerEnvironmentApplicationPhase.Unconfirmed)
                return await EnvironmentCommandResultAsync(requestedId, RunnerEnvironmentApplicationCommandStatus.Unconfirmed, application);
            if (application.Phase is not (RunnerEnvironmentApplicationPhase.Applying or RunnerEnvironmentApplicationPhase.Failed))
                return await EnvironmentCommandResultAsync(requestedId, RunnerEnvironmentApplicationCommandStatus.Conflict, application);
            if (!OwnsEnvironmentFence(requestedId))
                return await EnvironmentCommandResultAsync(requestedId, RunnerEnvironmentApplicationCommandStatus.Conflict, application);

            await PersistEnvironmentApplicationMutationAsync(
                application,
                () =>
                {
                    application.FailureCode = normalizedFailure;
                    application.Phase = RunnerEnvironmentApplicationPhase.Unconfirmed;
                });
            return await EnvironmentCommandResultAsync(requestedId, RunnerEnvironmentApplicationCommandStatus.Unconfirmed, application);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<RunnerEnvironmentApplicationCommandResult> CancelEnvironmentApplicationAsync(string updateId)
    {
        var requestedId = NormalizeUpdateInterruptId(updateId)
            ?? throw new ArgumentException("environment update id must be a UUID", nameof(updateId));

        await _lifecycleGate.WaitAsync();
        try
        {
            var application = CurrentEnvironmentApplication(requestedId);
            if (application is null)
                return new(requestedId, RunnerEnvironmentApplicationCommandStatus.NotFound, null);
            if (application.Phase == RunnerEnvironmentApplicationPhase.Cancelled)
                return await EnvironmentCommandResultAsync(requestedId, RunnerEnvironmentApplicationCommandStatus.AlreadyCancelled, application);
            if (application.Phase != RunnerEnvironmentApplicationPhase.Waiting)
                return await EnvironmentCommandResultAsync(requestedId, RunnerEnvironmentApplicationCommandStatus.Conflict, application);
            if (!OwnsEnvironmentFence(requestedId))
                return await EnvironmentCommandResultAsync(requestedId, RunnerEnvironmentApplicationCommandStatus.Conflict, application);

            await PersistEnvironmentApplicationMutationAsync(
                application,
                () =>
                {
                    application.Phase = RunnerEnvironmentApplicationPhase.Cancelled;
                    application.CompletedAt = _timeProvider.GetUtcNow();
                    ClearEnvironmentFence(requestedId, cancelled: true);
                },
                refreshDrain: true);
            return await EnvironmentCommandResultAsync(requestedId, RunnerEnvironmentApplicationCommandStatus.Cancelled, application);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private RunnerEnvironmentApplication? CurrentEnvironmentApplication(string updateId)
    {
        var application = _state.State?.EnvironmentApplication;
        return application is not null
            && string.Equals(application.UpdateId, updateId, StringComparison.Ordinal)
            ? application
            : null;
    }

    private async Task PersistEnvironmentApplicationMutationAsync(
        RunnerEnvironmentApplication application,
        Action mutation,
        bool refreshDrain = false)
    {
        var state = _state.State ??= new RunnerState();
        var previousApplication = CloneEnvironmentApplication(application);
        var previousFence = state.UpdateInterruptFence;
        var previousFencePendingId = previousFence?.PendingId;
        var previousFenceLastCancelledId = previousFence?.LastCancelledId;
        var previousFenceKind = previousFence?.Kind;
        var previousDraining = _draining;

        mutation();
        try
        {
            await PersistAsync();
        }
        catch
        {
            RestoreEnvironmentApplication(application, previousApplication);
            if (previousFence is null)
            {
                state.UpdateInterruptFence = null;
            }
            else
            {
                previousFence.PendingId = previousFencePendingId;
                previousFence.LastCancelledId = previousFenceLastCancelledId;
                previousFence.Kind = previousFenceKind;
            }
            _draining = previousDraining;
            throw;
        }

        if (refreshDrain)
            RefreshDurableDrainFlag();
        PublishStatusObservation();
    }

    private static RunnerEnvironmentApplication CloneEnvironmentApplication(
        RunnerEnvironmentApplication application) =>
        new()
        {
            UpdateId = application.UpdateId,
            TargetVersion = application.TargetVersion,
            PreviousVersion = application.PreviousVersion,
            BaseProcessGeneration = application.BaseProcessGeneration,
            BaseConnectionGeneration = application.BaseConnectionGeneration,
            Phase = application.Phase,
            FailureCode = application.FailureCode,
            RequestedAt = application.RequestedAt,
            CompletedAt = application.CompletedAt,
        };

    private static void RestoreEnvironmentApplication(
        RunnerEnvironmentApplication target,
        RunnerEnvironmentApplication source)
    {
        target.UpdateId = source.UpdateId;
        target.TargetVersion = source.TargetVersion;
        target.PreviousVersion = source.PreviousVersion;
        target.BaseProcessGeneration = source.BaseProcessGeneration;
        target.BaseConnectionGeneration = source.BaseConnectionGeneration;
        target.Phase = source.Phase;
        target.FailureCode = source.FailureCode;
        target.RequestedAt = source.RequestedAt;
        target.CompletedAt = source.CompletedAt;
    }

    private async Task<RunnerEnvironmentApplicationCommandResult> EnvironmentCommandResultAsync(
        string updateId,
        RunnerEnvironmentApplicationCommandStatus status,
        RunnerEnvironmentApplication application)
    {
        return new(updateId, status, await BuildEnvironmentApplicationSnapshotAsync(application));
    }

    private async Task<RunnerEnvironmentApplicationSnapshot> BuildEnvironmentApplicationSnapshotAsync(
        RunnerEnvironmentApplication application)
    {
        return new(
            application.UpdateId,
            application.TargetVersion,
            application.PreviousVersion,
            application.Phase,
            application.FailureCode,
            application.BaseProcessGeneration,
            application.BaseConnectionGeneration,
            application.RequestedAt,
            application.CompletedAt,
            await ReadEnvironmentSettlementAsync(_state.State?.CurrentProcessGeneration));
    }

    private async Task<RunnerEnvironmentSettlement> ReadEnvironmentSettlementAsync(string? processGeneration)
    {
        var runtime = await BuildRuntimeStateAsync();
        var observation = runtime.DispatchObservation;
        var currentGeneration = !string.IsNullOrWhiteSpace(processGeneration)
            && string.Equals(_state.State?.CurrentProcessGeneration, processGeneration, StringComparison.Ordinal);
        var currentConnection = observation is not null
            && string.Equals(observation.ConnectionGeneration, _info?.ConnectionGeneration, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(observation.ConnectionGeneration);
        var reportedSettled = currentGeneration
            && currentConnection
            && observation is not null
            && observation.IsSettledFor(processGeneration!);
        var activeWorkCount = runtime.ActiveWorks.Count;
        var inFlightCount = observation?.InFlightCount ?? 0;
        var awaitingAckCount = observation?.AwaitingAckCount ?? 0;
        return new(
            activeWorkCount == 0,
            reportedSettled,
            activeWorkCount == 0 && reportedSettled,
            activeWorkCount,
            inFlightCount,
            awaitingAckCount,
            observation?.ProcessGeneration,
            observation?.ConnectionGeneration);
    }

    private bool OwnsEnvironmentFence(string updateId)
    {
        var fence = _state.State?.UpdateInterruptFence;
        return RunnerUpdateInterruptKinds.IsEnvironmentApplication(fence)
            && string.Equals(fence?.PendingId, updateId, StringComparison.Ordinal);
    }

    private void ClearEnvironmentFence(string updateId, bool cancelled)
    {
        var fence = UpdateInterruptFence();
        if (!RunnerUpdateInterruptKinds.IsEnvironmentApplication(fence)
            || !string.Equals(fence.PendingId, updateId, StringComparison.Ordinal))
            return;
        fence.PendingId = null;
        fence.Kind = null;
        if (cancelled)
            fence.LastCancelledId = updateId;
    }

    private void RefreshDurableDrainFlag()
    {
        var state = _state.State;
        _draining = !string.IsNullOrWhiteSpace(state?.PendingProcessGeneration)
            || !string.IsNullOrWhiteSpace(state?.ClosingProcessGeneration)
            || !string.IsNullOrWhiteSpace(state?.UpdateInterruptFence?.PendingId);
    }

    private static string? NormalizeEnvironmentVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = value.Trim();
        return normalized.Length <= 128 && !normalized.Any(char.IsControl) ? normalized : null;
    }

    private static string NormalizeRequiredIdentity(string value, string name)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Any(char.IsControl))
            throw new ArgumentException($"{name} is required", name);
        return normalized;
    }

    private static string? NormalizeFailureCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = value.Trim();
        return normalized.Length <= 128 && !normalized.Any(char.IsControl) ? normalized : null;
    }
}
