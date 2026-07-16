using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.SystemInfo;

public sealed class SystemUpdateService : ISingletonService
{
    public const string HomeEnvironmentVariable = "HOME";

    private const int MaxLogEntries = 200;
    private readonly Func<CancellationToken, Task<SystemInfoResponse>> _getSystemInfo;
    private readonly ISystemUpdateStore _store;
    private readonly ISystemUpdateCommandRunner _commandRunner;
    private readonly ISystemReadinessProbe _readinessProbe;
    private readonly IConfiguration _configuration;
    private readonly IManagedAssetCatalog _managedAssets;
    private readonly ILogger<SystemUpdateService> _logger;
    private readonly TimeProvider _time;

    public SystemUpdateService(
        SystemInfoService systemInfoService,
        ISystemUpdateStore store,
        ISystemUpdateCommandRunner commandRunner,
        ISystemReadinessProbe readinessProbe,
        IConfiguration configuration,
        IManagedAssetCatalog managedAssets,
        ILogger<SystemUpdateService> logger)
        : this(_ => systemInfoService.GetSystemInfoAsync(), store, commandRunner, readinessProbe, configuration, managedAssets, logger, TimeProvider.System)
    {
    }

    internal SystemUpdateService(
        Func<CancellationToken, Task<SystemInfoResponse>> getSystemInfo,
        ISystemUpdateStore store,
        ISystemUpdateCommandRunner commandRunner,
        ISystemReadinessProbe readinessProbe,
        IConfiguration configuration,
        IManagedAssetCatalog managedAssets,
        ILogger<SystemUpdateService> logger,
        TimeProvider time)
    {
        _getSystemInfo = getSystemInfo;
        _store = store;
        _commandRunner = commandRunner;
        _readinessProbe = readinessProbe;
        _configuration = configuration;
        _managedAssets = managedAssets;
        _logger = logger;
        _time = time;
    }

    public async Task<(bool Started, string? Error, string? Code, SystemUpdateStatusResponse? Status)> StartAsync(SystemUpdateRequest request, CancellationToken cancellationToken = default)
    {
        var info = await _getSystemInfo(cancellationToken);
        var validation = ValidateStart(info);
        if (validation is not null)
            return (false, validation.Value.Error, validation.Value.Code, null);

        var jobId = Guid.NewGuid().ToString("N");
        if (!await _store.TryAcquireLockAsync(jobId, cancellationToken))
        {
            var latest = await _store.GetLatestAsync(cancellationToken);
            return (false, "A system update is already in progress", "update_in_progress", latest is null ? null : ToResponse(latest));
        }

        SystemUpdateJobState? startedState = null;
        try
        {
            var now = _time.GetUtcNow();
            startedState = new SystemUpdateJobState(
                jobId,
                "running",
                "Building",
                info.Update.Available,
                info.Running.GitHash,
                info.Source.Head,
                info.Source.Path,
                info.Install.ServerUnit,
                info.Install.RunnerUnit,
                null,
                [new SystemUpdateLogEntry(now, "Building", "Starting update")],
                now,
                now,
                null);
            await _store.SaveAsync(startedState, cancellationToken);

            _ = Task.Run(() => RunUpdateAsync(startedState, CancellationToken.None), CancellationToken.None);

            return (true, null, null, ToResponse(startedState));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "System update failed");
            var failedState = startedState ?? await _store.GetLatestAsync(cancellationToken);
            if (failedState is not null)
            {
                failedState = await FailAsync(failedState, ex.Message, cancellationToken);
                return (true, null, null, ToResponse(failedState));
            }

            throw;
        }
    }

    public async Task<SystemUpdateStatusResponse?> GetLatestStatusAsync(CancellationToken cancellationToken = default)
    {
        var latest = await _store.GetLatestAsync(cancellationToken);
        return latest is null ? null : ToResponse(latest);
    }

    public async Task AdvanceActiveJobAsync(CancellationToken cancellationToken = default)
    {
        var latest = await _store.GetLatestAsync(cancellationToken);
        if (latest is null)
            return;

        if (latest.Status is not ("running" or "waiting-for-reconnect"))
            return;

        var info = await _getSystemInfo(cancellationToken);
        var runningHash = info.Running.GitHash;

        if (latest.Status == "waiting-for-reconnect"
            && !string.IsNullOrWhiteSpace(runningHash)
            && !string.IsNullOrWhiteSpace(latest.SourceHead)
            && !string.Equals(runningHash, latest.SourceHead, StringComparison.Ordinal))
        {
            var supersededAt = _time.GetUtcNow();
            await PersistTransitionAsync(
                latest with
                {
                    Status = "superseded",
                    Stage = "Superseded",
                    RunningGitHash = runningHash,
                    Reason = "Server runtime has advanced past this job's source HEAD; this update is no longer relevant.",
                    CompletedAt = supersededAt
                },
                cancellationToken,
                new SystemUpdateLogEntry(supersededAt, "Superseded", $"Running git hash '{runningHash}' differs from job source HEAD '{latest.SourceHead}'; marking job as superseded."),
                releaseLock: true);
            return;
        }

        var readiness = await _readinessProbe.ProbeAsync(cancellationToken);
        if (!readiness.HealthReady || !readiness.RootReady || !readiness.AssetsReady)
        {
            var failureReason = readiness.FailureReason ?? "Waiting for reconnect";
            var shouldPersistWaiting = latest.Stage != "Waiting for reconnect" || latest.Reason != failureReason;
            if (shouldPersistWaiting)
            {
                var waitingAt = _time.GetUtcNow();
                await PersistTransitionAsync(
                    latest with
                    {
                        Status = "waiting-for-reconnect",
                        Stage = "Waiting for reconnect",
                        Reason = failureReason
                    },
                    cancellationToken,
                    new SystemUpdateLogEntry(waitingAt, "Waiting for reconnect", failureReason));
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(runningHash) || runningHash != latest.SourceHead)
            return;

        var now = _time.GetUtcNow();
        latest = await PersistTransitionAsync(
            latest with
            {
                RunningGitHash = runningHash,
                SourceHead = info.Source.Head,
                Reason = "Server runtime matches source HEAD and readiness checks passed"
            },
            cancellationToken,
            new SystemUpdateLogEntry(now, "Ready", $"Server runtime matches source HEAD and asset {readiness.RootAssetPath} is ready"));

        if (!string.IsNullOrWhiteSpace(latest.RunnerUnit))
        {
            latest = await RunCommandAsync(latest, latest.SourcePath!, "Restarting runner", "systemctl", ["--user", "restart", latest.RunnerUnit!], cancellationToken);
            if (latest.Status == "failed")
                return;
        }

        var completedAt = _time.GetUtcNow();
        await PersistTransitionAsync(
            latest with
            {
                Status = "succeeded",
                Outcome = "succeeded",
                Stage = "Ready",
                CompletedAt = completedAt
            },
            cancellationToken,
            releaseLock: true,
            timestamp: completedAt);
    }

    public async Task<SystemUpdateStatusEnvelope> GetStatusEnvelopeAsync(CancellationToken cancellationToken = default)
    {
        await AdvanceActiveJobAsync(cancellationToken);
        var latest = await GetLatestStatusAsync(cancellationToken);
        return new SystemUpdateStatusEnvelope(latest is not null, latest);
    }

    public async Task<SystemUpdateStatusResponse> RecordCliOutcomeAsync(SystemUpdateOutcomeRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));

        var info = await _getSystemInfo(cancellationToken);
        var now = _time.GetUtcNow();

        var latest = await _store.GetLatestAsync(cancellationToken);
        var jobId = string.IsNullOrWhiteSpace(request.JobId) ? Guid.NewGuid().ToString("N") : request.JobId!;
        var stage = string.IsNullOrWhiteSpace(request.Stage) ? "Ready" : request.Stage!;
        var status = NormalizeOutcomeStatus(request.Status);
        var outcome = NormalizeOutcomeLabel(request.Outcome) ?? status;
        var unavailableCapability = string.IsNullOrWhiteSpace(request.UnavailableCapability) ? null : request.UnavailableCapability;
        var sourceHead = request.SourceHead ?? info.Source.Head;
        var sourcePath = request.SourcePath ?? info.Source.Path;
        var serverUnit = request.ServerUnit ?? info.Install.ServerUnit;
        var runnerUnit = request.RunnerUnit ?? info.Install.RunnerUnit;

        // A CLI outcome either continues the same job (matching JobId, e.g. a retried
        // report) or records a brand-new update. The store only keeps the latest job,
        // so a prior terminal job is history and must not block the new outcome from
        // becoming the latest job state. Reuse the persisted state only when the JobId
        // matches; otherwise start from a fresh job.
        var baseState = latest is not null && string.Equals(latest.JobId, jobId, StringComparison.Ordinal)
            ? latest
            : new SystemUpdateJobState(
                jobId,
                status,
                stage,
                info.Update.Available,
                info.Running.GitHash,
                sourceHead,
                sourcePath,
                serverUnit,
                runnerUnit,
                null,
                [],
                now,
                now,
                now,
                outcome,
                unavailableCapability);

        IReadOnlyList<SystemUpdateLogEntry> logs = baseState.Logs;
        if (request.Logs is { Count: > 0 })
        {
            foreach (var entry in request.Logs)
            {
                logs = AppendLog(logs, entry);
            }
        }

        var completedAt = SystemUpdateJobState.TerminalStatuses.Contains(status) ? now : baseState.CompletedAt;

        var next = baseState with
        {
            JobId = jobId,
            Status = status,
            Stage = stage,
            RunningGitHash = info.Running.GitHash ?? baseState.RunningGitHash,
            SourceHead = sourceHead,
            SourcePath = sourcePath,
            ServerUnit = serverUnit,
            RunnerUnit = runnerUnit,
            Reason = unavailableCapability ?? baseState.Reason,
            Outcome = outcome,
            UnavailableCapability = unavailableCapability,
            CompletedAt = completedAt,
            Logs = logs
        };

        await SupersedeStaleWebJobsAsync(next, cancellationToken);

        var current = await _store.GetLatestAsync(cancellationToken);
        var expected = current is not null && string.Equals(current.JobId, baseState.JobId, StringComparison.Ordinal) ? current : null;
        var outcomeEntry = new SystemUpdateLogEntry(now, stage, $"CLI reported outcome '{outcome}' with status '{status}'.");
        next = await PersistTransitionAsync(next, cancellationToken, outcomeEntry, releaseLock: true, expected: expected);
        return ToResponse(next);
    }

    private async Task SupersedeStaleWebJobsAsync(SystemUpdateJobState cliState, CancellationToken cancellationToken)
    {
        var existing = await _store.GetLatestAsync(cancellationToken);
        if (existing is null
            || existing.JobId == cliState.JobId
            || existing.Status != "waiting-for-reconnect")
            return;

        if (string.IsNullOrWhiteSpace(existing.SourceHead)
            || string.IsNullOrWhiteSpace(cliState.SourceHead)
            || string.Equals(existing.SourceHead, cliState.SourceHead, StringComparison.Ordinal))
            return;

        var now = _time.GetUtcNow();
        await PersistTransitionAsync(
            existing with
            {
                Status = "superseded",
                Stage = "Superseded",
                Reason = "A newer CLI-triggered update has completed; this job is no longer relevant.",
                CompletedAt = now
            },
            cancellationToken,
            new SystemUpdateLogEntry(now, "Superseded", $"CLI update {cliState.JobId} completed with source HEAD '{cliState.SourceHead}'; this job is superseded."),
            releaseLock: true,
            expected: existing);
    }

    public async Task<RuntimeConsistencyResponse> GetConsistencyAsync(CancellationToken cancellationToken = default)
    {
        var info = await _getSystemInfo(cancellationToken);
        var components = new List<RuntimeConsistencyComponent>
        {
            BuildServerComponent(info),
            BuildRunnerComponent(info),
            BuildWebAssetsComponent(info),
            BuildManagedAssetsComponent(info),
            BuildCliComponent(info)
        };

        var mismatched = components.Count(c => c.Status is "mismatched" or "unavailable" or "unknown");
        var topStatus = mismatched == 0 ? "consistent" : "inconsistent";
        var reason = mismatched == 0
            ? "All components are coherent and usable."
            : $"{mismatched} component(s) reported a consistency issue.";

        return new RuntimeConsistencyResponse(topStatus, reason, components, info);
    }

    private static RuntimeConsistencyComponent BuildServerComponent(SystemInfoResponse info)
    {
        if (string.IsNullOrWhiteSpace(info.Running.GitHash) || string.IsNullOrWhiteSpace(info.Source.Head))
        {
            return new RuntimeConsistencyComponent("server", "unknown", "Server git hash or source HEAD is unavailable.");
        }

        if (!string.Equals(info.Running.GitHash, info.Source.Head, StringComparison.Ordinal))
        {
            return new RuntimeConsistencyComponent(
                "server",
                "mismatched",
                $"Running git hash '{info.Running.GitHash}' differs from source HEAD '{info.Source.Head}'.");
        }

        return new RuntimeConsistencyComponent("server", "consistent", null);
    }

    private static RuntimeConsistencyComponent BuildRunnerComponent(SystemInfoResponse info)
    {
        var status = info.Services.Runner;
        if (string.IsNullOrWhiteSpace(status) || !string.Equals(status, "active", StringComparison.OrdinalIgnoreCase))
        {
            return new RuntimeConsistencyComponent(
                "runner",
                "unavailable",
                string.IsNullOrWhiteSpace(status) ? "Runner service status is not reported." : $"Runner service status is '{status}'.");
        }

        return new RuntimeConsistencyComponent("runner", "consistent", null);
    }

    private static RuntimeConsistencyComponent BuildWebAssetsComponent(SystemInfoResponse info)
    {
        if (string.IsNullOrWhiteSpace(info.Install.ServerUnit))
        {
            return new RuntimeConsistencyComponent("web-assets", "unknown", "Server unit is not configured.");
        }

        var status = info.Services.Server;
        if (string.IsNullOrWhiteSpace(status) || !string.Equals(status, "active", StringComparison.OrdinalIgnoreCase))
        {
            return new RuntimeConsistencyComponent(
                "web-assets",
                "unavailable",
                string.IsNullOrWhiteSpace(status) ? "Server service status is not reported." : $"Server service status is '{status}'.");
        }

        return new RuntimeConsistencyComponent("web-assets", "consistent", null);
    }

    private RuntimeConsistencyComponent BuildManagedAssetsComponent(SystemInfoResponse info)
    {
        return _managedAssets.GetState() switch
        {
            ManagedAssetCatalogState.Available =>
                new RuntimeConsistencyComponent("managed-assets", "consistent", null),
            ManagedAssetCatalogState.Empty =>
                new RuntimeConsistencyComponent(
                    "managed-assets",
                    "mismatched",
                    "Managed skill assets contain no skill."),
            _ =>
                new RuntimeConsistencyComponent(
                    "managed-assets",
                    "mismatched",
                    "Managed skill asset directory is missing or unreadable."),
        };
    }

    private static RuntimeConsistencyComponent BuildCliComponent(SystemInfoResponse info)
    {
        if (string.IsNullOrWhiteSpace(info.Running.Version))
        {
            return new RuntimeConsistencyComponent("cli", "unknown", "Server version is not reported.");
        }

        return new RuntimeConsistencyComponent("cli", "consistent", null);
    }

    private static string NormalizeOutcomeStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            throw new ArgumentException("status is required", nameof(status));
        return status.Trim().ToLowerInvariant() switch
        {
            "succeeded" => "succeeded",
            "success" => "succeeded",
            "recovered" => "recovered",
            "failed" => "failed",
            "superseded" => "superseded",
            "cancelled" or "canceled" => "cancelled",
            _ => throw new ArgumentException($"Unknown outcome status '{status}'", nameof(status))
        };
    }

    private static string? NormalizeOutcomeLabel(string? outcome)
    {
        if (string.IsNullOrWhiteSpace(outcome)) return null;
        return outcome.Trim().ToLowerInvariant() switch
        {
            "succeeded" => "succeeded",
            "recovered" => "recovered",
            "failed" => "failed",
            _ => null
        };
    }

    private async Task RunUpdateAsync(SystemUpdateJobState state, CancellationToken cancellationToken)
    {
        var runnerWasPresent = !string.IsNullOrWhiteSpace(state.RunnerUnit);
        try
        {
            state = await RevalidateInstallAsync(state, cancellationToken);
            if (state.Status == "failed")
                return;

            state = await RunCommandAsync(state, state.SourcePath!, "Building", "dotnet", ["build", "Mohist.sln"], cancellationToken);
            if (state.Status == "failed")
            {
                state = await TryRestoreRunnerAsync(state, runnerWasPresent, cancellationToken);
                return;
            }

            state = await RunCommandAsync(state, state.SourcePath!, "Restarting server", "systemctl", ["--user", "restart", state.ServerUnit!], cancellationToken);
            if (state.Status == "failed")
            {
                state = await TryRestoreRunnerAsync(state, runnerWasPresent, cancellationToken);
                return;
            }

            var waitingAt = _time.GetUtcNow();
            state = await PersistTransitionAsync(
                state with
                {
                    Status = "waiting-for-reconnect",
                    Stage = "Waiting for reconnect",
                    Reason = "Server restart requested. Waiting for the new runtime to reconnect."
                },
                cancellationToken,
                new SystemUpdateLogEntry(waitingAt, "Waiting for reconnect", "Server restart requested. Waiting for the new runtime to reconnect."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "System update failed");
            var failure = CreateFailedTransition(state, ex.Message);
            state = ApplyTransitionLog(failure.State, failure.LogEntry);
            if (runnerWasPresent && !string.IsNullOrWhiteSpace(state.RunnerUnit))
            {
                state = await TryRestoreRunnerAsync(state, runnerWasPresent: true, cancellationToken);
            }
            else
            {
                state = await PersistTransitionAsync(failure.State, cancellationToken, failure.LogEntry);
            }
        }
        finally
        {
            if (state.Status is "failed" or "recovered")
                await _store.ReleaseLockAsync(state.JobId, cancellationToken);
        }
    }

    private async Task<SystemUpdateJobState> TryRestoreRunnerAsync(
        SystemUpdateJobState state,
        bool runnerWasPresent,
        CancellationToken cancellationToken)
    {
        if (!runnerWasPresent || string.IsNullOrWhiteSpace(state.RunnerUnit))
        {
            return await PersistTransitionAsync(state, cancellationToken);
        }

        var restoringState = await PersistTransitionAsync(
            state with
            {
                Stage = "Restoring runner",
                Status = "running",
                Reason = "Update failed before server restart completed; attempting to restore the trusted runner unit.",
                CompletedAt = null,
                Outcome = null,
                UnavailableCapability = null
            },
            cancellationToken);

        var restored = await RunCommandAsync(
            restoringState,
            state.SourcePath!,
            "Restoring runner",
            "systemctl",
            ["--user", "restart", state.RunnerUnit!],
            cancellationToken);

        if (restored.Status != "failed")
        {
            var recoveredAt = _time.GetUtcNow();
            var failureReason = state.Reason;
            return await PersistTransitionAsync(
                restored with
                {
                    Status = "recovered",
                    Stage = "Recovered",
                    Reason = failureReason,
                    Outcome = "recovered",
                    UnavailableCapability = null,
                    CompletedAt = recoveredAt
                },
                cancellationToken,
                new SystemUpdateLogEntry(recoveredAt, "Recovered", "Runner restore succeeded after update failure; Mohist is available with warnings."));
        }

        return await FailAsync(
            restored,
            state.Reason!,
            cancellationToken,
            stage: "Failed",
            outcome: "failed",
            unavailableCapability: "Runner",
            logStage: "Failed",
            logMessage: "Runner restore failed after update failure. Workflows are unavailable. Start the runner manually with: mo server start --runner");
    }

    private (string Error, string Code)? ValidateStart(SystemInfoResponse info)
    {
        if (info.Install.Mode != "local-source")
            return ("Web update is unsupported for the detected deployment", "unsupported_install");

        if (!IsUpdateEnabled())
            return ("System update is disabled by configuration", "update_disabled");

        if (string.IsNullOrWhiteSpace(info.Source.Path) || string.IsNullOrWhiteSpace(info.Install.ServerUnit))
            return ("System update is unavailable because the install is incomplete", "install_incomplete");

        if (info.Update.Status == "dirty-source")
            return ("Source tree has uncommitted changes", "dirty_source");

        if (!info.Update.Available)
            return (info.Update.Reason ?? "No update is available", "no_update_available");

        return null;
    }

    private bool IsUpdateEnabled()
    {
        var configured = _configuration["Mohist:SystemUpdate:Enabled"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return bool.TryParse(configured, out var enabled) && enabled;
        }

        // Install mode and install completeness are enforced independently by
        // ValidateStart (before and after this gate), so the unconfigured default
        // remains enabled.
        return true;
    }

    internal static bool IsActive(SystemUpdateJobState state)
        => state.Status is "running" or "waiting-for-reconnect";

    private async Task<SystemUpdateJobState> RevalidateInstallAsync(SystemUpdateJobState state, CancellationToken cancellationToken)
    {
        var info = await _getSystemInfo(cancellationToken);
        var validation = ValidateStart(info);
        if (validation is not null)
            return await FailAsync(state, validation.Value.Error, cancellationToken);

        if (!string.Equals(info.Source.Path, state.SourcePath, StringComparison.Ordinal)
            || !string.Equals(info.Install.ServerUnit, state.ServerUnit, StringComparison.Ordinal)
            || !string.Equals(info.Install.RunnerUnit, state.RunnerUnit, StringComparison.Ordinal))
        {
            return await FailAsync(state, "Trusted install facts changed before update execution", cancellationToken);
        }

        return state;
    }

    private async Task<SystemUpdateJobState> RunCommandAsync(
        SystemUpdateJobState state,
        string workingDirectory,
        string stage,
        string fileName,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var startedAt = _time.GetUtcNow();
        state = await PersistTransitionAsync(
            state with { Stage = stage },
            cancellationToken,
            new SystemUpdateLogEntry(startedAt, stage, $"Running {fileName} {string.Join(' ', args)}"));

        var result = await _commandRunner.RunAsync(new SystemCommandRequest(fileName, args, workingDirectory, stage), cancellationToken);
        var finishedAt = _time.GetUtcNow();
        var message = string.IsNullOrWhiteSpace(result.Output)
            ? $"{fileName} exited with code {result.ExitCode}"
            : result.Output;

        if (result.ExitCode != 0)
        {
            return await FailAsync(state, message, cancellationToken, stage: stage);
        }

        return await PersistTransitionAsync(
            state,
            cancellationToken,
            new SystemUpdateLogEntry(finishedAt, stage, message));
    }

    private async Task<SystemUpdateJobState> PersistTransitionAsync(
        SystemUpdateJobState next,
        CancellationToken cancellationToken,
        SystemUpdateLogEntry? logEntry = null,
        bool releaseLock = false,
        SystemUpdateJobState? expected = null,
        DateTimeOffset? timestamp = null)
    {
        next = ApplyTransitionLog(next, logEntry, timestamp);
        if (expected is not null)
            await _store.SaveIfCurrentAsync(expected, next, cancellationToken);
        else
            await _store.SaveAsync(next, cancellationToken);
        if (releaseLock)
            await _store.ReleaseLockAsync(next.JobId, cancellationToken);
        return next;
    }

    private async Task<SystemUpdateJobState> FailAsync(
        SystemUpdateJobState state,
        string reason,
        CancellationToken cancellationToken,
        string? stage = null,
        string? outcome = null,
        string? unavailableCapability = null,
        string? logStage = null,
        string? logMessage = null,
        bool releaseLock = false)
    {
        var failure = CreateFailedTransition(state, reason, stage, outcome, unavailableCapability, logStage, logMessage);
        return await PersistTransitionAsync(
            failure.State,
            cancellationToken,
            failure.LogEntry,
            releaseLock: releaseLock);
    }

    private (SystemUpdateJobState State, SystemUpdateLogEntry LogEntry) CreateFailedTransition(
        SystemUpdateJobState state,
        string reason,
        string? stage = null,
        string? outcome = null,
        string? unavailableCapability = null,
        string? logStage = null,
        string? logMessage = null)
    {
        var failedAt = _time.GetUtcNow();
        var next = state with
        {
            Status = "failed",
            Stage = stage ?? state.Stage,
            Reason = reason,
            Outcome = outcome,
            UnavailableCapability = unavailableCapability,
            CompletedAt = failedAt
        };
        return (next, new SystemUpdateLogEntry(failedAt, logStage ?? stage ?? state.Stage, logMessage ?? reason));
    }

    private SystemUpdateJobState ApplyTransitionLog(SystemUpdateJobState next, SystemUpdateLogEntry? logEntry, DateTimeOffset? timestamp = null)
    {
        var logs = logEntry is not null ? AppendLog(next.Logs, logEntry) : next.Logs;
        timestamp ??= logEntry?.At ?? _time.GetUtcNow();
        return next with
        {
            Logs = logs,
            UpdatedAt = timestamp.Value
        };
    }

    private static IReadOnlyList<SystemUpdateLogEntry> AppendLog(IReadOnlyList<SystemUpdateLogEntry> logs, SystemUpdateLogEntry entry)
    {
        var next = logs.ToList();
        next.Add(entry);
        if (next.Count > MaxLogEntries)
            next = next[^MaxLogEntries..];
        return next;
    }

    private static SystemUpdateStatusResponse ToResponse(SystemUpdateJobState state)
    {
        return new SystemUpdateStatusResponse(
            state.JobId,
            state.Status,
            state.Stage,
            state.UpdateAvailable,
            state.RunningGitHash,
            state.SourceHead,
            state.SourcePath,
            state.ServerUnit,
            state.RunnerUnit,
            state.Reason,
            state.Logs,
            state.CreatedAt,
            state.UpdatedAt,
            state.CompletedAt,
            state.Outcome,
            state.UnavailableCapability);
    }
}
