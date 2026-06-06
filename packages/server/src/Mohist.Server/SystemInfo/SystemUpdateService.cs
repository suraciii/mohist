using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace Mohist.Server.SystemInfo;

public interface ISystemUpdateStore
{
    Task<SystemUpdateJobState?> GetLatestAsync(CancellationToken cancellationToken = default);
    Task<bool> TryAcquireLockAsync(string jobId, CancellationToken cancellationToken = default);
    Task ReleaseLockAsync(string jobId, CancellationToken cancellationToken = default);
    Task SaveAsync(SystemUpdateJobState state, CancellationToken cancellationToken = default);
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
    DateTimeOffset? CompletedAt);

public sealed class FileSystemSystemUpdateStore : ISystemUpdateStore
{
    public const string HomeEnvironmentVariable = "HOME";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
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
            return await JsonSerializer.DeserializeAsync<SystemUpdateJobState>(stream, JsonOptions, cancellationToken);
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
                await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
            }

            File.Move(tempPath, _statePath, overwrite: true);
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
        return await JsonSerializer.DeserializeAsync<SystemUpdateJobState>(stream, JsonOptions, cancellationToken);
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

public sealed class SystemUpdateService
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
                var info = await _getSystemInfo(cancellationToken);
                if (!string.IsNullOrWhiteSpace(info.Running.GitHash) && info.Running.GitHash == latest.SourceHead)
                {
                    var now = DateTimeOffset.UtcNow;
                    latest = latest with
                    {
                        RunningGitHash = info.Running.GitHash,
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

    private async Task RunUpdateAsync(SystemUpdateJobState state, CancellationToken cancellationToken)
    {
        try
        {
            state = await RevalidateInstallAsync(state, cancellationToken);
            if (state.Status == "failed")
                return;

            state = await RunCommandAsync(state, state.SourcePath!, "Building", "dotnet", ["build", "Mohist.sln"], cancellationToken);
            if (state.Status == "failed")
                return;

            state = await RunCommandAsync(state, state.SourcePath!, "Restarting server", "systemctl", ["--user", "restart", state.ServerUnit!], cancellationToken);
            if (state.Status == "failed")
                return;

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
            await _store.SaveAsync(state, cancellationToken);
        }
        finally
        {
            if (state.Status == "failed")
                await _store.ReleaseLockAsync(state.JobId, cancellationToken);
        }
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
            state.CompletedAt);
    }
}
