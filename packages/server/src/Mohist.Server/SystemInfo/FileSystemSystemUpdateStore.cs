using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.SystemInfo;

public interface ISystemUpdateStore
{
    Task<SystemUpdateJobState?> GetLatestAsync(CancellationToken cancellationToken = default);
    Task<bool> TryAcquireLockAsync(string jobId, CancellationToken cancellationToken = default);
    Task ReleaseLockAsync(string jobId, CancellationToken cancellationToken = default);
    Task<bool> ReleaseStaleLockAsync(string jobId, CancellationToken cancellationToken = default);
    Task SaveAsync(SystemUpdateJobState state, CancellationToken cancellationToken = default);
    Task<bool> SaveIfCurrentAsync(SystemUpdateJobState expected, SystemUpdateJobState next, CancellationToken cancellationToken = default);
}

internal interface ISystemUpdateStateFiles
{
    void EnsureParentDirectory(string path);
    bool Exists(string path);
    Stream OpenRead(string path);
    Stream Create(string path);
    bool TryCreate(string path, string contents);
    string ReadAllText(string path);
    void Move(string source, string destination, bool overwrite);
    void Delete(string path);
}

internal sealed class PhysicalSystemUpdateStateFiles : ISystemUpdateStateFiles
{
    public static readonly PhysicalSystemUpdateStateFiles Instance = new();

    private PhysicalSystemUpdateStateFiles()
    {
    }

    public void EnsureParentDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }

    public bool Exists(string path) => File.Exists(path);

    public Stream OpenRead(string path) => File.OpenRead(path);

    public Stream Create(string path) => File.Create(path);

    public bool TryCreate(string path, string contents)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream);
            writer.Write(contents);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public string ReadAllText(string path) => File.ReadAllText(path);

    public void Move(string source, string destination, bool overwrite) => File.Move(source, destination, overwrite);

    public void Delete(string path) => File.Delete(path);
}

public sealed class FileSystemSystemUpdateStore : ISystemUpdateStore
{
    public const string HomeEnvironmentVariable = "HOME";

    private readonly string _statePath;
    private readonly string _lockPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IEnvironmentVariableProvider _environment;
    private readonly ISystemUpdateStateFiles _files;
    private bool _locked;
    private string? _lockOwnerJobId;

    public FileSystemSystemUpdateStore(IConfiguration configuration)
        : this(configuration, SystemEnvironmentVariableProvider.Instance, PhysicalSystemUpdateStateFiles.Instance)
    {
    }

    public FileSystemSystemUpdateStore(IConfiguration configuration, IEnvironmentVariableProvider environment)
        : this(configuration, environment, PhysicalSystemUpdateStateFiles.Instance)
    {
    }

    internal FileSystemSystemUpdateStore(
        IConfiguration configuration,
        IEnvironmentVariableProvider environment,
        ISystemUpdateStateFiles files)
    {
        _environment = environment;
        _files = files;
        _statePath = ResolveStatePath(configuration);
        _lockPath = _statePath + ".lock";
        _files.EnsureParentDirectory(_statePath);
    }

    public async Task<SystemUpdateJobState?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_files.Exists(_statePath))
                return null;

            await using var stream = _files.OpenRead(_statePath);
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
                ReleaseLockFileOwnedByCurrentProcess(jobId);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> ReleaseStaleLockAsync(string jobId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return ReleaseLockFile(jobId);
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
            await using (var stream = _files.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, state, JSON.Options, cancellationToken);
            }

            _files.Move(tempPath, _statePath, overwrite: true);
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
            await using (var stream = _files.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, next, JSON.Options, cancellationToken);
            }

            _files.Move(tempPath, _statePath, overwrite: true);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SystemUpdateJobState?> ReadLatestUnlockedAsync(CancellationToken cancellationToken)
    {
        if (!_files.Exists(_statePath))
            return null;

        await using var stream = _files.OpenRead(_statePath);
        return await JsonSerializer.DeserializeAsync<SystemUpdateJobState>(stream, JSON.Options, cancellationToken);
    }

    private bool TryCreateLockFile(string jobId)
    {
        return _files.TryCreate(_lockPath, jobId);
    }

    private bool ReleaseLockFile(string jobId)
    {
        if (!_files.Exists(_lockPath))
            return true;

        try
        {
            var owner = _files.ReadAllText(_lockPath);
            if (owner != jobId)
                return false;

            _files.Delete(_lockPath);
            return !_files.Exists(_lockPath);
        }
        catch (IOException)
        {
            // A concurrently starting process may be reading the lock. The active state still protects correctness.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void ReleaseLockFileOwnedByCurrentProcess(string jobId)
    {
        if (!_files.Exists(_lockPath))
            return;

        try
        {
            var owner = _files.ReadAllText(_lockPath);
            if (owner == jobId)
                _files.Delete(_lockPath);
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
