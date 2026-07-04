using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.SystemInfo;

public interface ISystemUpdateStore
{
    Task<SystemUpdateJobState?> GetLatestAsync(CancellationToken cancellationToken = default);
    Task<bool> TryAcquireLockAsync(string jobId, CancellationToken cancellationToken = default);
    Task ReleaseLockAsync(string jobId, CancellationToken cancellationToken = default);
    Task ReleaseStaleLockAsync(string jobId, CancellationToken cancellationToken = default);
    Task SaveAsync(SystemUpdateJobState state, CancellationToken cancellationToken = default);
    Task<bool> SaveIfCurrentAsync(SystemUpdateJobState expected, SystemUpdateJobState next, CancellationToken cancellationToken = default);
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

    public Task ReleaseStaleLockAsync(string jobId, CancellationToken cancellationToken = default)
    {
        _gate.Wait(cancellationToken);
        try
        {
            ReleaseLockFile(jobId);
        }
        finally
        {
            _gate.Release();
        }

        return Task.CompletedTask;
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