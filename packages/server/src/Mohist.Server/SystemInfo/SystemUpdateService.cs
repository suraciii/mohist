using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.SystemInfo;

public interface ISystemUpdateStore
{
    Task<SystemUpdateJobState?> GetLatestAsync(CancellationToken cancellationToken = default);
    Task<bool> TryAcquireLockAsync(string jobId, CancellationToken cancellationToken = default);
    Task ReleaseLockAsync(string jobId, CancellationToken cancellationToken = default);
    Task SaveAsync(SystemUpdateJobState state, CancellationToken cancellationToken = default);
    Task<bool> SaveIfCurrentAsync(SystemUpdateJobState expected, SystemUpdateJobState next, CancellationToken cancellationToken = default);
}

public interface ISystemUpdateCommandRunner
{
    Task<SystemCommandResult> RunAsync(SystemCommandRequest command, CancellationToken cancellationToken = default);
}

public interface ISystemReadinessProbe
{
    Task<SystemReadinessResult> ProbeAsync(CancellationToken cancellationToken = default);
}

public sealed record SystemCommandRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    string Stage,
    int MaxOutputBytes = 8192);

public sealed record SystemCommandResult(int ExitCode, string Output);

public sealed record SystemReadinessResult(
    bool HealthReady,
    bool RootReady,
    bool AssetsReady,
    string? RootAssetPath,
    string? FailureReason);

public sealed record SystemUpdateJobState(
    string JobId,
    string Status,
    string Stage,
    bool UpdateAvailable,
    string? RunningGitHash,
    string? SourceHead,
    string? SourcePath,
    string? ServerUnit,
    string? RunnerUnit,
    string? Reason,
    IReadOnlyList<SystemUpdateLogEntry> Logs,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    string? Outcome = null,
    string? UnavailableCapability = null)
{
    public static readonly IReadOnlyList<string> ActiveStatuses = ["running", "waiting-for-reconnect"];
    public static readonly IReadOnlyList<string> TerminalStatuses = ["succeeded", "failed", "recovered", "superseded", "cancelled"];
}

public sealed class FileSystemSystemUpdateStore : ISystemUpdateStore
{
    public const string HomeEnvironmentVariable = "HOME";

    private readonly string _statePath;
    private readonly string _lockPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IEnvironmentVariableProvider _environment;
    private bool _locked;
    private string? _lockOwnerJobId;

    public FileSystemSystemUpdateStore(IConfiguration configuration)
        : this(configuration, SystemEnvironmentVariableProvider.Instance)
    {
    }

    public FileSystemSystemUpdateStore(IConfiguration configuration, IEnvironmentVariableProvider environment)
    {
        _environment = environment;
        _statePath = ResolveStatePath(configuration);
        _lockPath = _statePath + ".lock";
        var dir = Path.GetDirectoryName(_statePath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);
    }

    public async Task<SystemUpdateJobState?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_statePath))
                return null;

            await using var stream = File.OpenRead(_statePath);
            return await JsonSerializer.DeserializeAsync<SystemUpdateJobState>(stream, JSON.Options, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> TryAcquireLockAsync(string jobId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_locked)
                return false;

            var latest = await ReadLatestUnlockedAsync(cancellationToken);
            if (latest is not null && SystemUpdateService.IsActive(latest))
                return false;

            if (!TryCreateLockFile(jobId))
                return false;

            _locked = true;
            _lockOwnerJobId = jobId;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReleaseLockAsync(string jobId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_lockOwnerJobId == jobId)
            {
                _locked = false;
                _lockOwnerJobId = null;
                ReleaseLockFile(jobId);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(SystemUpdateJobState state, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var tempPath = _statePath + ".tmp";
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, state, JSON.Options, cancellationToken);
            }

            File.Move(tempPath, _statePath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> SaveIfCurrentAsync(SystemUpdateJobState expected, SystemUpdateJobState next, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = await ReadLatestUnlockedAsync(cancellationToken);
            if (current is null)
                return false;
            if (!string.Equals(current.JobId, expected.JobId, StringComparison.Ordinal)
                || !string.Equals(current.Status, expected.Status, StringComparison.Ordinal))
            {
                return false;
            }

            var tempPath = _statePath + ".tmp";
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, next, JSON.Options, cancellationToken);
            }

            File.Move(tempPath, _statePath, overwrite: true);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SystemUpdateJobState?> ReadLatestUnlockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_statePath))
            return null;

        await using var stream = File.OpenRead(_statePath);
        return await JsonSerializer.DeserializeAsync<SystemUpdateJobState>(stream, JSON.Options, cancellationToken);
    }

    private bool TryCreateLockFile(string jobId)
    {
        try
        {
            using var stream = new FileStream(_lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream);
            writer.Write(jobId);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private void ReleaseLockFile(string jobId)
    {
        if (!File.Exists(_lockPath))
            return;

        try
        {
            var owner = File.ReadAllText(_lockPath);
            if (owner == jobId)
                File.Delete(_lockPath);
        }
        catch (IOException)
        {
            // A concurrently starting process may be reading the lock. The active state still protects correctness.
        }
    }

    private string ResolveStatePath(IConfiguration configuration)
    {
        var configured = configuration["Mohist:SystemUpdate:StatePath"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        var home = _environment.GetEnvironmentVariable(HomeEnvironmentVariable)
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".mohist", "system-update.json");
    }
}

public sealed class ProcessSystemUpdateCommandRunner : ISystemUpdateCommandRunner
{
    public async Task<SystemCommandResult> RunAsync(SystemCommandRequest command, CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo(command.FileName, command.Arguments)
        {
            WorkingDirectory = command.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(psi);
        if (process == null)
            return new SystemCommandResult(-1, $"Failed to start {command.FileName}");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = (await stdoutTask) + (await stderrTask);

        if (output.Length > command.MaxOutputBytes)
            output = output[..command.MaxOutputBytes];

        return new SystemCommandResult(process.ExitCode, output.Trim());
    }
}

public sealed class HttpSystemReadinessProbe : ISystemReadinessProbe
{
    private static readonly Regex AssetRegex = new("(?:src|href)=\"(?<path>/assets/[^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly HttpClient _httpClient;

    public HttpSystemReadinessProbe(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<SystemReadinessResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var health = await GetAsync("/api/health", cancellationToken);
        if (!health.IsSuccessStatusCode)
            return new SystemReadinessResult(false, false, false, null, "API health endpoint is not ready");

        var root = await GetAsync("/", cancellationToken);
        if (!root.IsSuccessStatusCode)
            return new SystemReadinessResult(true, false, false, null, "Web root is not ready");

        var html = await root.Content.ReadAsStringAsync(cancellationToken);
        var assetPath = AssetRegex.Match(html).Groups["path"].Value;
        if (string.IsNullOrWhiteSpace(assetPath))
            return new SystemReadinessResult(true, true, false, null, "Web root did not reference a bundled asset");

        var asset = await GetAsync(assetPath, cancellationToken);
        if (!asset.IsSuccessStatusCode)
            return new SystemReadinessResult(true, true, false, assetPath, "Bundled asset is not ready");

        return new SystemReadinessResult(true, true, true, assetPath, null);
    }

    private async Task<HttpResponseMessage> GetAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.GetAsync(path, cancellationToken);
        }
        catch
        {
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
    }
}

public sealed class SystemUpdateService : ISingletonService
{
    public const string HomeEnvironmentVariable = "HOME";

    private const int MaxLogEntries = 200;
    private readonly Func<CancellationToken, Task<SystemInfoResponse>> _getSystemInfo;
    private readonly ISystemUpdateStore _store;
    private readonly ISystemUpdateCommandRunner _commandRunner;
    private readonly ISystemReadinessProbe _readinessProbe;
    private readonly IConfiguration _configuration;
    private readonly IEnvironmentVariableProvider _environment;
    private readonly ILogger<SystemUpdateService> _logger;

    public SystemUpdateService(
        SystemInfoService systemInfoService,
        ISystemUpdateStore store,
        ISystemUpdateCommandRunner commandRunner,
        ISystemReadinessProbe readinessProbe,
        IConfiguration configuration,
        IEnvironmentVariableProvider environment,
        ILogger<SystemUpdateService> logger)
        : this(_ => systemInfoService.GetSystemInfoAsync(), store, commandRunner, readinessProbe, configuration, environment, logger)
    {
    }

    internal SystemUpdateService(
        Func<CancellationToken, Task<SystemInfoResponse>> getSystemInfo,
        ISystemUpdateStore store,
        ISystemUpdateCommandRunner commandRunner,
        ISystemReadinessProbe readinessProbe,
        IConfiguration configuration,
        IEnvironmentVariableProvider environment,
        ILogger<SystemUpdateService> logger)
    {
        _getSystemInfo = getSystemInfo;
        _store = store;
        _commandRunner = commandRunner;
        _readinessProbe = readinessProbe;
        _configuration = configuration;
        _environment = environment;
        _logger = logger;
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
            var now = DateTimeOffset.UtcNow;
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
                var failedAt = DateTimeOffset.UtcNow;
                failedState = failedState with
                {
                    Status = "failed",
                    Stage = failedState.Stage,
                    Reason = ex.Message,
                    UpdatedAt = failedAt,
                    CompletedAt = failedAt,
                    Logs = AppendLog(failedState.Logs, new SystemUpdateLogEntry(failedAt, failedState.Stage, ex.Message))
                };
                await _store.SaveAsync(failedState, cancellationToken);
                return (true, null, null, ToResponse(failedState));
            }

            throw;
        }
        finally
        {
        }
    }

    public async Task<SystemUpdateStatusResponse?> GetLatestStatusAsync(CancellationToken cancellationToken = default)
    {
        var latest = await _store.GetLatestAsync(cancellationToken);
        if (latest is null)
            return null;

        if (latest.Status is "running" or "waiting-for-reconnect")
        {
            var info = await _getSystemInfo(cancellationToken);
            var runningHash = info.Running.GitHash;

            if (latest.Status == "waiting-for-reconnect"
                && !string.IsNullOrWhiteSpace(runningHash)
                && !string.IsNullOrWhiteSpace(latest.SourceHead)
                && !string.Equals(runningHash, latest.SourceHead, StringComparison.Ordinal))
            {
                var supersededAt = DateTimeOffset.UtcNow;
                latest = latest with
                {
                    Status = "superseded",
                    Stage = "Superseded",
                    RunningGitHash = runningHash,
                    Reason = "Server runtime has advanced past this job's source HEAD; this update is no longer relevant.",
                    UpdatedAt = supersededAt,
                    CompletedAt = supersededAt,
                    Logs = AppendLog(latest.Logs, new SystemUpdateLogEntry(supersededAt, "Superseded", $"Running git hash '{runningHash}' differs from job source HEAD '{latest.SourceHead}'; marking job as superseded."))
                };
                await _store.SaveAsync(latest, cancellationToken);
                await _store.ReleaseLockAsync(latest.JobId, cancellationToken);
                return ToResponse(latest);
            }

            var readiness = await _readinessProbe.ProbeAsync(cancellationToken);
            if (!readiness.HealthReady || !readiness.RootReady || !readiness.AssetsReady)
            {
                var failureReason = readiness.FailureReason ?? "Waiting for reconnect";
                var shouldPersistWaiting = latest.Stage != "Waiting for reconnect" || latest.Reason != failureReason;
                if (shouldPersistWaiting)
                {
                    var waitingAt = DateTimeOffset.UtcNow;
                    latest = latest with
                    {
                        Status = "waiting-for-reconnect",
                        Stage = "Waiting for reconnect",
                        Reason = failureReason,
                        UpdatedAt = waitingAt,
                        Logs = AppendLog(latest.Logs, new SystemUpdateLogEntry(waitingAt, "Waiting for reconnect", failureReason))
                    };
                    await _store.SaveAsync(latest, cancellationToken);
                }
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(runningHash) && runningHash == latest.SourceHead)
                {
                    var now = DateTimeOffset.UtcNow;
                    latest = latest with
                    {
                        RunningGitHash = runningHash,
                        SourceHead = info.Source.Head,
                        Reason = "Server runtime matches source HEAD and readiness checks passed",
                        UpdatedAt = now,
                        Logs = AppendLog(latest.Logs, new SystemUpdateLogEntry(now, "Ready", $"Server runtime matches source HEAD and asset {readiness.RootAssetPath} is ready"))
                    };
                    await _store.SaveAsync(latest, cancellationToken);

                    if (!string.IsNullOrWhiteSpace(latest.RunnerUnit))
                    {
                        latest = await RunCommandAsync(latest, latest.SourcePath!, "Restarting runner", "systemctl", ["--user", "restart", latest.RunnerUnit!], cancellationToken);
                        if (latest.Status == "failed")
                            return ToResponse(latest);
                    }

                    var completedAt = DateTimeOffset.UtcNow;
                    latest = latest with
                    {
                        Status = "succeeded",
                        Outcome = "succeeded",
                        Stage = "Ready",
                        UpdatedAt = completedAt,
                        CompletedAt = completedAt,
                    };
                    await _store.SaveAsync(latest, cancellationToken);
                    await _store.ReleaseLockAsync(latest.JobId, cancellationToken);
                }
            }
        }

        return ToResponse(latest);
    }

    public async Task<SystemUpdateStatusEnvelope> GetStatusEnvelopeAsync(CancellationToken cancellationToken = default)
    {
        var latest = await GetLatestStatusAsync(cancellationToken);
        return new SystemUpdateStatusEnvelope(latest is not null, latest);
    }

    public async Task<SystemUpdateStatusResponse> RecordCliOutcomeAsync(SystemUpdateOutcomeRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));

        var info = await _getSystemInfo(cancellationToken);
        var now = DateTimeOffset.UtcNow;

        var persistedLatest = await _store.GetLatestAsync(cancellationToken);
        if (persistedLatest is not null
            && !string.IsNullOrWhiteSpace(request.JobId)
            && !string.Equals(persistedLatest.JobId, request.JobId, StringComparison.Ordinal)
            && SystemUpdateJobState.TerminalStatuses.Contains(persistedLatest.Status))
        {
            throw new InvalidOperationException(
                $"The latest persisted update job ('{persistedLatest.JobId}') is terminal and does not match the supplied JobId ('{request.JobId}'). Refusing to overwrite an unrelated job state.");
        }

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

        var baseState = latest ?? new SystemUpdateJobState(
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
        logs = AppendLog(logs, new SystemUpdateLogEntry(now, stage, $"CLI reported outcome '{outcome}' with status '{status}'."));

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
            UpdatedAt = now,
            CompletedAt = completedAt,
            Logs = logs
        };

        await SupersedeStaleWebJobsAsync(next, cancellationToken);

        var current = await _store.GetLatestAsync(cancellationToken);
        if (current is null || !string.Equals(current.JobId, baseState.JobId, StringComparison.Ordinal))
        {
            await _store.SaveAsync(next, cancellationToken);
        }
        else
        {
            await _store.SaveIfCurrentAsync(current, next, cancellationToken);
        }
        await _store.ReleaseLockAsync(next.JobId, cancellationToken);
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

        var now = DateTimeOffset.UtcNow;
        var superseded = existing with
        {
            Status = "superseded",
            Stage = "Superseded",
            Reason = "A newer CLI-triggered update has completed; this job is no longer relevant.",
            UpdatedAt = now,
            CompletedAt = now,
            Logs = AppendLog(existing.Logs, new SystemUpdateLogEntry(now, "Superseded", $"CLI update {cliState.JobId} completed with source HEAD '{cliState.SourceHead}'; this job is superseded."))
        };
        await _store.SaveIfCurrentAsync(existing, superseded, cancellationToken);
        await _store.ReleaseLockAsync(superseded.JobId, cancellationToken);
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
        var assetRoot = ResolveManagedAssetRoot();
        if (string.IsNullOrWhiteSpace(assetRoot) || !Directory.Exists(assetRoot))
        {
            return new RuntimeConsistencyComponent(
                "managed-assets",
                "mismatched",
                "Managed skill asset directory is missing or unreadable.");
        }

        try
        {
            var hasSkill = Directory.EnumerateFiles(assetRoot, "SKILL.md", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
            }).Any();

            if (!hasSkill)
            {
                return new RuntimeConsistencyComponent(
                    "managed-assets",
                    "mismatched",
                    $"Managed skill assets at '{assetRoot}' contain no skill.");
            }
        }
        catch
        {
            return new RuntimeConsistencyComponent(
                "managed-assets",
                "mismatched",
                "Managed skill asset directory is missing or unreadable.");
        }

        return new RuntimeConsistencyComponent("managed-assets", "consistent", null);
    }

    private static RuntimeConsistencyComponent BuildCliComponent(SystemInfoResponse info)
    {
        if (string.IsNullOrWhiteSpace(info.Running.Version))
        {
            return new RuntimeConsistencyComponent("cli", "unknown", "Server version is not reported.");
        }

        return new RuntimeConsistencyComponent("cli", "consistent", null);
    }

    private string ResolveManagedAssetRoot()
    {
        var configured = _configuration["Mohist:CliSkillDataPath"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        var home = _environment.GetEnvironmentVariable(HomeEnvironmentVariable)
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".mohist", "cli", "skill-data");
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

            var waitingAt = DateTimeOffset.UtcNow;
            state = state with
            {
                Status = "waiting-for-reconnect",
                Stage = "Waiting for reconnect",
                Reason = "Server restart requested. Waiting for the new runtime to reconnect.",
                UpdatedAt = waitingAt,
                Logs = AppendLog(state.Logs, new SystemUpdateLogEntry(waitingAt, "Waiting for reconnect", "Server restart requested. Waiting for the new runtime to reconnect."))
            };
            await _store.SaveAsync(state, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "System update failed");
            var failedAt = DateTimeOffset.UtcNow;
            state = state with
            {
                Status = "failed",
                Reason = ex.Message,
                UpdatedAt = failedAt,
                CompletedAt = failedAt,
                Logs = AppendLog(state.Logs, new SystemUpdateLogEntry(failedAt, state.Stage, ex.Message))
            };
            if (runnerWasPresent && !string.IsNullOrWhiteSpace(state.RunnerUnit))
            {
                state = await TryRestoreRunnerAsync(state, runnerWasPresent: true, cancellationToken);
            }
            else
            {
                await _store.SaveAsync(state, cancellationToken);
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
            await _store.SaveAsync(state, cancellationToken);
            return state;
        }

        var restoringState = state with
        {
            Stage = "Restoring runner",
            Status = "running",
            Reason = "Update failed before server restart completed; attempting to restore the trusted runner unit.",
            UpdatedAt = DateTimeOffset.UtcNow,
            CompletedAt = null,
            Outcome = null,
            UnavailableCapability = null
        };
        await _store.SaveAsync(restoringState, cancellationToken);

        var restored = await RunCommandAsync(
            restoringState,
            state.SourcePath!,
            "Restoring runner",
            "systemctl",
            ["--user", "restart", state.RunnerUnit!],
            cancellationToken);

        if (restored.Status != "failed")
        {
            var recoveredAt = DateTimeOffset.UtcNow;
            var failureReason = state.Reason;
            var recovered = restored with
            {
                Status = "recovered",
                Stage = "Recovered",
                Reason = failureReason,
                Outcome = "recovered",
                UnavailableCapability = null,
                UpdatedAt = recoveredAt,
                CompletedAt = recoveredAt,
                Logs = AppendLog(restored.Logs, new SystemUpdateLogEntry(recoveredAt, "Recovered", "Runner restore succeeded after update failure; Mohist is available with warnings."))
            };
            await _store.SaveAsync(recovered, cancellationToken);
            return recovered;
        }

        var failedAt = DateTimeOffset.UtcNow;
        var restoreFailureReason = state.Reason;
        var failed = restored with
        {
            Status = "failed",
            Stage = "Failed",
            Reason = restoreFailureReason,
            Outcome = "failed",
            UnavailableCapability = "Runner",
            UpdatedAt = failedAt,
            CompletedAt = failedAt,
            Logs = AppendLog(restored.Logs, new SystemUpdateLogEntry(failedAt, "Failed", "Runner restore failed after update failure. Workflows are unavailable. Start the runner manually with: mo server start --runner"))
        };
        await _store.SaveAsync(failed, cancellationToken);
        return failed;
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
        return string.IsNullOrWhiteSpace(configured) || bool.TryParse(configured, out var enabled) && enabled;
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
        var startedAt = DateTimeOffset.UtcNow;
        state = state with
        {
            Stage = stage,
            UpdatedAt = startedAt,
            Logs = AppendLog(state.Logs, new SystemUpdateLogEntry(startedAt, stage, $"Running {fileName} {string.Join(' ', args)}"))
        };
        await _store.SaveAsync(state, cancellationToken);

        var result = await _commandRunner.RunAsync(new SystemCommandRequest(fileName, args, workingDirectory, stage), cancellationToken);
        var finishedAt = DateTimeOffset.UtcNow;
        var message = string.IsNullOrWhiteSpace(result.Output)
            ? $"{fileName} exited with code {result.ExitCode}"
            : result.Output;

        if (result.ExitCode != 0)
        {
            state = state with
            {
                Status = "failed",
                Stage = stage,
                Reason = message,
                UpdatedAt = finishedAt,
                CompletedAt = finishedAt,
                Logs = AppendLog(state.Logs, new SystemUpdateLogEntry(finishedAt, stage, message))
            };
            await _store.SaveAsync(state, cancellationToken);
            return state;
        }

        state = state with
        {
            UpdatedAt = finishedAt,
            Logs = AppendLog(state.Logs, new SystemUpdateLogEntry(finishedAt, stage, message))
        };
        await _store.SaveAsync(state, cancellationToken);
        return state;
    }

    private static IReadOnlyList<SystemUpdateLogEntry> AppendLog(IReadOnlyList<SystemUpdateLogEntry> logs, SystemUpdateLogEntry entry)
    {
        var next = logs.ToList();
        next.Add(entry);
        if (next.Count > MaxLogEntries)
            next = next[^MaxLogEntries..];
        return next;
    }

    private async Task<SystemUpdateJobState> FailAsync(SystemUpdateJobState state, string reason, CancellationToken cancellationToken)
    {
        var failedAt = DateTimeOffset.UtcNow;
        state = state with
        {
            Status = "failed",
            Reason = reason,
            UpdatedAt = failedAt,
            CompletedAt = failedAt,
            Logs = AppendLog(state.Logs, new SystemUpdateLogEntry(failedAt, state.Stage, reason))
        };
        await _store.SaveAsync(state, cancellationToken);
        return state;
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
