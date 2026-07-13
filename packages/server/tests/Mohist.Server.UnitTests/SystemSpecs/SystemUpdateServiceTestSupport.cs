using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.SystemInfo;
using Mohist.Server.UnitTests.Support;
using Xunit;
using EnvironmentAbstractions.TestHelpers;

namespace Mohist.Server.UnitTests.SystemSpecs;

internal static class SystemUpdateServiceTestSupport
{
    private static readonly TimeSpan AsyncWaitTimeout = TimeSpan.FromSeconds(5);

    internal static SystemUpdateService CreateService(
        SystemInfoResponse systemInfo,
        ISystemUpdateStore store,
        ISystemUpdateCommandRunner commandRunner,
        ISystemReadinessProbe readinessProbe)
    {
        return CreateService(new SequencedSystemInfo(systemInfo), store, commandRunner, readinessProbe);
    }

    internal static SystemUpdateService CreateService(
        SystemInfoResponse systemInfo,
        ISystemUpdateStore store,
        ISystemUpdateCommandRunner commandRunner,
        ISystemReadinessProbe readinessProbe,
        string? enabled,
        bool includeEnabled = true)
    {
        return CreateService(new SequencedSystemInfo(systemInfo), store, commandRunner, readinessProbe, enabled, includeEnabled);
    }

    internal static FileSystemSystemUpdateStore CreateFileSystemStore(
        InMemorySystemUpdateStateFiles files,
        string statePath = "/test/system-update.json")
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mohist:SystemUpdate:StatePath"] = statePath
        }).Build();
        return new FileSystemSystemUpdateStore(
            configuration,
            new MockEnvironmentVariableProvider(),
            files);
    }

    internal static SystemUpdateService CreateService(
        SequencedSystemInfo systemInfo,
        ISystemUpdateStore store,
        ISystemUpdateCommandRunner commandRunner,
        ISystemReadinessProbe readinessProbe)
    {
        return CreateService(systemInfo, store, commandRunner, readinessProbe, enabled: "true");
    }

    internal static SystemUpdateService CreateService(
        SequencedSystemInfo systemInfo,
        ISystemUpdateStore store,
        ISystemUpdateCommandRunner commandRunner,
        ISystemReadinessProbe readinessProbe,
        string? enabled,
        bool includeEnabled = true)
    {
        return CreateService(
            systemInfo,
            store,
            commandRunner,
            readinessProbe,
            new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero)),
            enabled,
            includeEnabled).Service;
    }

    internal static (SystemUpdateService Service, FakeTimeProvider Time) CreateService(
        SequencedSystemInfo systemInfo,
        ISystemUpdateStore store,
        ISystemUpdateCommandRunner commandRunner,
        ISystemReadinessProbe readinessProbe,
        FakeTimeProvider time,
        string? enabled = "true",
        bool includeEnabled = true)
    {
        var settings = new Dictionary<string, string?>();
        if (includeEnabled)
            settings["Mohist:SystemUpdate:Enabled"] = enabled;

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var service = new SystemUpdateService(
            systemInfo.GetSystemInfoAsync,
            store,
            commandRunner,
            readinessProbe,
            configuration,
            new MockEnvironmentVariableProvider(),
            NullLogger<SystemUpdateService>.Instance,
            time);
        return (service, time);
    }

    internal static SystemInfoResponse CreateInfo(
        string updateStatus = "update-available",
        bool available = true,
        bool sourceDirty = false,
        string? runningGitHash = "oldhash",
        string? sourceHead = "newhash",
        string installMode = "local-source",
        string? sourcePath = "/repo",
        string? serverServiceStatus = "active",
        string? runnerServiceStatus = "active")
    {
        return new SystemInfoResponse(
            new RunningInfo("1.2.3", runningGitHash, DateTimeOffset.UnixEpoch),
            new SourceInfo(sourcePath, "main", sourceHead, sourceDirty),
            new InstallInfo(installMode, "systemd-user", "mohist.service", "mohist-runner.service", installMode),
            new UpdateInfo(updateStatus, available, updateStatus),
            new ServiceInfo(serverServiceStatus, runnerServiceStatus),
            new SystemPaths("/db", "/config", "/logs", "/opencode"));
    }

    internal static SystemUpdateService CreateConsistencyService(
        SequencedSystemInfo systemInfo,
        ISystemUpdateStore store,
        ISystemUpdateCommandRunner commandRunner,
        ISystemReadinessProbe readinessProbe,
        bool hasManagedAssets = true)
    {
        return CreateConsistencyService(
            systemInfo,
            store,
            commandRunner,
            readinessProbe,
            hasManagedAssets,
            new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero))).Service;
    }

    internal static (SystemUpdateService Service, FakeTimeProvider Time) CreateConsistencyService(
        SequencedSystemInfo systemInfo,
        ISystemUpdateStore store,
        ISystemUpdateCommandRunner commandRunner,
        ISystemReadinessProbe readinessProbe,
        bool hasManagedAssets,
        FakeTimeProvider time)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mohist:SystemUpdate:Enabled"] = "true",
            ["Mohist:CliSkillDataPath"] = "/test/skill-data"
        }).Build();

        var service = new SystemUpdateService(
            systemInfo.GetSystemInfoAsync,
            store,
            commandRunner,
            readinessProbe,
            configuration,
            new MockEnvironmentVariableProvider(),
            NullLogger<SystemUpdateService>.Instance,
            time,
            new ManagedAssetInspector(hasManagedAssets));
        return (service, time);
    }

    internal sealed class OrderTrackingStore : ISystemUpdateStore
    {
        public List<string> Events { get; } = [];

        public Task<SystemUpdateJobState?> GetLatestAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<SystemUpdateJobState?>(null);

        public Task<bool> TryAcquireLockAsync(string jobId, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task ReleaseLockAsync(string jobId, CancellationToken cancellationToken = default)
        {
            Events.Add("ReleaseLock");
            return Task.CompletedTask;
        }

        public Task<bool> ReleaseStaleLockAsync(string jobId, CancellationToken cancellationToken = default)
        {
            Events.Add("ReleaseStaleLock");
            return Task.FromResult(true);
        }

        public Task SaveAsync(SystemUpdateJobState state, CancellationToken cancellationToken = default)
        {
            Events.Add("Save");
            return Task.CompletedTask;
        }

        public Task<bool> SaveIfCurrentAsync(SystemUpdateJobState expected, SystemUpdateJobState next, CancellationToken cancellationToken = default)
        {
            Events.Add("Save");
            return Task.FromResult(true);
        }
    }

    internal sealed class InMemoryUpdateStore : ISystemUpdateStore
    {
        private readonly object _gate = new();
        private readonly List<StatusWaiter> _statusWaiters = [];
        // Specs that assert the lock is free after a terminal status must wait
        // for unlock explicitly: the production RunUpdateAsync saves the
        // terminal status first, then releases the lock in a `finally`, so
        // WaitForStatusAsync(terminal) can complete while the lock is still
        // held. Mirror the StatusWaiter/CountWaiter TCS pattern.
        private readonly List<TaskCompletionSource> _unlockWaiters = [];
        private readonly bool _acquireLock;
        private SystemUpdateJobState? _latest;
        private bool _locked;
        private string? _lockOwnerJobId;

        public InMemoryUpdateStore(bool acquireLock = true)
        {
            _acquireLock = acquireLock;
        }

        public List<SystemUpdateJobState> SavedStates { get; } = [];

        public int AcquireAttempts { get; private set; }

        public Task<SystemUpdateJobState?> GetLatestAsync(CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return Task.FromResult(_latest);
            }
        }

        public Task<bool> TryAcquireLockAsync(string jobId, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                AcquireAttempts++;
                if (!_acquireLock || _locked || _latest?.Status is "running" or "waiting-for-reconnect")
                    return Task.FromResult(false);

                _locked = true;
                _lockOwnerJobId = jobId;
                return Task.FromResult(true);
            }
        }

        public Task ReleaseLockAsync(string jobId, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (_lockOwnerJobId == jobId)
                {
                    _locked = false;
                    _lockOwnerJobId = null;
                    CompleteUnlockWaiters();
                }
            }

            return Task.CompletedTask;
        }

        public Task<bool> ReleaseStaleLockAsync(string jobId, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (_locked && _lockOwnerJobId != jobId)
                    return Task.FromResult(false);

                if (_lockOwnerJobId == jobId)
                {
                    _locked = false;
                    _lockOwnerJobId = null;
                    CompleteUnlockWaiters();
                }
            }

            return Task.FromResult(true);
        }

        public Task WaitForUnlockAsync()
        {
            lock (_gate)
            {
                if (!_locked)
                    return Task.CompletedTask;

                var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _unlockWaiters.Add(waiter);
                return waiter.Task.WaitAsync(AsyncWaitTimeout);
            }
        }

        private void CompleteUnlockWaiters()
        {
            for (var i = _unlockWaiters.Count - 1; i >= 0; i--)
            {
                var waiter = _unlockWaiters[i];
                _unlockWaiters.RemoveAt(i);
                waiter.TrySetResult();
            }
        }

        public Task SaveAsync(SystemUpdateJobState state, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _latest = state;
                SavedStates.Add(state);
                CompleteStatusWaiters();
            }

            return Task.CompletedTask;
        }

        public Task<bool> SaveIfCurrentAsync(SystemUpdateJobState expected, SystemUpdateJobState next, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (_latest is null
                    || !string.Equals(_latest.JobId, expected.JobId, StringComparison.Ordinal)
                    || !string.Equals(_latest.Status, expected.Status, StringComparison.Ordinal))
                {
                    return Task.FromResult(false);
                }
                _latest = next;
                SavedStates.Add(next);
                CompleteStatusWaiters();
                return Task.FromResult(true);
            }
        }

        public Task WaitForStatusAsync(string status)
        {
            lock (_gate)
            {
                if (string.Equals(_latest?.Status, status, StringComparison.Ordinal))
                    return Task.CompletedTask;

                var waiter = new StatusWaiter(status);
                _statusWaiters.Add(waiter);
                return waiter.Task.WaitAsync(AsyncWaitTimeout);
            }
        }

        public Task WaitForStatusAndStageAsync(string status, string stage)
        {
            if (string.Equals(_latest?.Status, status, StringComparison.Ordinal)
                && string.Equals(_latest?.Stage, stage, StringComparison.Ordinal))
            {
                return Task.CompletedTask;
            }

            var waiter = new StatusWaiter(status, stage);
            _statusWaiters.Add(waiter);
            return waiter.Task;
        }

        private void CompleteStatusWaiters()
        {
            for (var i = _statusWaiters.Count - 1; i >= 0; i--)
            {
                var waiter = _statusWaiters[i];
                if (!waiter.Matches(_latest))
                    continue;

                _statusWaiters.RemoveAt(i);
                waiter.Complete();
            }
        }
    }

    internal sealed class InMemorySystemUpdateStateFiles : ISystemUpdateStateFiles
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

        public void EnsureParentDirectory(string path)
        {
        }

        public bool Exists(string path)
        {
            lock (_gate)
                return _files.ContainsKey(path);
        }

        public Stream OpenRead(string path)
        {
            lock (_gate)
            {
                if (!_files.TryGetValue(path, out var contents))
                    throw new FileNotFoundException($"No state file at '{path}'.");
                return new MemoryStream(contents, writable: false);
            }
        }

        public Stream Create(string path) => new CommitStream(this, path);

        public bool TryCreate(string path, string contents)
        {
            lock (_gate)
            {
                if (_files.ContainsKey(path))
                    return false;
                _files[path] = Encoding.UTF8.GetBytes(contents);
                return true;
            }
        }

        public string ReadAllText(string path) => Encoding.UTF8.GetString(ReadAllBytes(path));

        public byte[] ReadAllBytes(string path)
        {
            lock (_gate)
            {
                if (!_files.TryGetValue(path, out var contents))
                    throw new FileNotFoundException($"No state file at '{path}'.");
                return contents.ToArray();
            }
        }

        public void Move(string source, string destination, bool overwrite)
        {
            lock (_gate)
            {
                if (!_files.TryGetValue(source, out var contents))
                    throw new FileNotFoundException($"No state file at '{source}'.");
                if (!overwrite && _files.ContainsKey(destination))
                    throw new IOException($"State file already exists at '{destination}'.");
                _files.Remove(source);
                _files[destination] = contents;
            }
        }

        public void Delete(string path)
        {
            lock (_gate)
                _files.Remove(path);
        }

        private void Save(string path, byte[] contents)
        {
            lock (_gate)
                _files[path] = contents;
        }

        private sealed class CommitStream(InMemorySystemUpdateStateFiles owner, string path) : MemoryStream
        {
            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    owner.Save(path, ToArray());
                base.Dispose(disposing);
            }
        }
    }

    internal sealed class ManagedAssetInspector(bool hasSkill) : IManagedAssetInspector
    {
        public bool HasSkill(string assetRoot) => hasSkill;
    }

    internal sealed class RecordingCommandRunner : ISystemUpdateCommandRunner
    {
        private readonly object _gate = new();
        private readonly List<CountWaiter> _waiters = [];
        public List<SystemCommandRequest> Requests { get; } = [];

        public Task<SystemCommandResult> RunAsync(SystemCommandRequest command, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                Requests.Add(command);
                CompleteSatisfiedWaiters();
            }

            return Task.FromResult(new SystemCommandResult(0, $"ok:{command.Stage}"));
        }

        public Task WaitForCountAsync(int count)
        {
            lock (_gate)
            {
                if (Requests.Count >= count)
                    return Task.CompletedTask;

                var waiter = new CountWaiter(count);
                _waiters.Add(waiter);
                return waiter.Task.WaitAsync(AsyncWaitTimeout);
            }
        }

        private void CompleteSatisfiedWaiters()
        {
            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                var waiter = _waiters[i];
                if (Requests.Count < waiter.Count)
                    continue;

                _waiters.RemoveAt(i);
                waiter.Complete();
            }
        }
    }

    internal sealed class ScriptedCommandRunner : ISystemUpdateCommandRunner
    {
        private readonly object _gate = new();
        private readonly (int Index, string FileName, SystemCommandResult Result)[] _script;
        private readonly List<CountWaiter> _waiters = [];
        public List<SystemCommandRequest> Requests { get; } = [];

        public ScriptedCommandRunner(params (int Index, string FileName, SystemCommandResult Result)[] script)
        {
            _script = script;
        }

        public Task<SystemCommandResult> RunAsync(SystemCommandRequest command, CancellationToken cancellationToken = default)
        {
            int index;
            lock (_gate)
            {
                index = Requests.Count;
                Requests.Add(command);
                CompleteSatisfiedWaiters();
            }

            if (index >= _script.Length)
                return Task.FromResult(new SystemCommandResult(0, "ok"));

            var entry = _script[index];
            if (!string.Equals(entry.FileName, command.FileName, StringComparison.Ordinal))
            {
                return Task.FromResult(new SystemCommandResult(-1, $"unexpected command at index {index}: expected {entry.FileName} but got {command.FileName}"));
            }

            return Task.FromResult(entry.Result);
        }

        public Task WaitForCountAsync(int count)
        {
            lock (_gate)
            {
                if (Requests.Count >= count)
                    return Task.CompletedTask;

                var waiter = new CountWaiter(count);
                _waiters.Add(waiter);
                return waiter.Task.WaitAsync(AsyncWaitTimeout);
            }
        }

        private void CompleteSatisfiedWaiters()
        {
            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                var waiter = _waiters[i];
                if (Requests.Count < waiter.Count)
                    continue;

                _waiters.RemoveAt(i);
                waiter.Complete();
            }
        }
    }

    internal sealed class ThrowingCommandRunner : ISystemUpdateCommandRunner
    {
        private readonly object _gate = new();
        private readonly (string FileName, Func<SystemCommandResult> Run)[] _script;
        private readonly List<CountWaiter> _waiters = [];
        public List<SystemCommandRequest> Requests { get; } = [];

        public ThrowingCommandRunner(params (string FileName, Func<SystemCommandResult> Run)[] script)
        {
            _script = script;
        }

        public Task<SystemCommandResult> RunAsync(SystemCommandRequest command, CancellationToken cancellationToken = default)
        {
            int index;
            lock (_gate)
            {
                index = Requests.Count;
                Requests.Add(command);
                CompleteSatisfiedWaiters();
            }

            if (index >= _script.Length)
                return Task.FromResult(new SystemCommandResult(0, "ok"));

            var entry = _script[index];
            if (!string.Equals(entry.FileName, command.FileName, StringComparison.Ordinal))
            {
                return Task.FromResult(new SystemCommandResult(-1, $"unexpected command at index {index}: expected {entry.FileName} but got {command.FileName}"));
            }

            return Task.FromResult(entry.Run());
        }

        public Task WaitForCountAsync(int count)
        {
            lock (_gate)
            {
                if (Requests.Count >= count)
                    return Task.CompletedTask;

                var waiter = new CountWaiter(count);
                _waiters.Add(waiter);
                return waiter.Task.WaitAsync(AsyncWaitTimeout);
            }
        }

        private void CompleteSatisfiedWaiters()
        {
            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                var waiter = _waiters[i];
                if (Requests.Count < waiter.Count)
                    continue;

                _waiters.RemoveAt(i);
                waiter.Complete();
            }
        }
    }

    internal sealed class CountWaiter
    {
        private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CountWaiter(int count) => Count = count;

        public int Count { get; }

        public Task Task => _tcs.Task;

        public void Complete() => _tcs.TrySetResult();
    }

    internal sealed class StatusWaiter
    {
        private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public StatusWaiter(string status, string? stage = null)
        {
            Status = status;
            Stage = stage;
        }

        public string Status { get; }

        public string? Stage { get; }

        public Task Task => _tcs.Task;

        public bool Matches(SystemUpdateJobState? state)
        {
            return state is not null
                && string.Equals(state.Status, Status, StringComparison.Ordinal)
                && (Stage is null || string.Equals(state.Stage, Stage, StringComparison.Ordinal));
        }

        public void Complete() => _tcs.TrySetResult();
    }

    internal sealed class StubReadinessProbe : ISystemReadinessProbe
    {
        private readonly SystemReadinessResult _result;

        public StubReadinessProbe(SystemReadinessResult result)
        {
            _result = result;
        }

        public Task<SystemReadinessResult> ProbeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_result);
    }

    internal sealed class SequenceReadinessProbe : ISystemReadinessProbe
    {
        private readonly Queue<SystemReadinessResult> _results;

        public SequenceReadinessProbe(params SystemReadinessResult[] results)
        {
            _results = new Queue<SystemReadinessResult>(results);
        }

        public Task<SystemReadinessResult> ProbeAsync(CancellationToken cancellationToken = default)
        {
            var result = _results.Count > 1 ? _results.Dequeue() : _results.Peek();
            return Task.FromResult(result);
        }
    }

    internal sealed class SequencedSystemInfo
    {
        private readonly Queue<SystemInfoResponse> _responses;

        public SequencedSystemInfo(params SystemInfoResponse[] responses)
        {
            _responses = new Queue<SystemInfoResponse>(responses);
        }

        public Task<SystemInfoResponse> GetSystemInfoAsync(CancellationToken cancellationToken = default)
        {
            var response = _responses.Count > 1 ? _responses.Dequeue() : _responses.Peek();
            return Task.FromResult(response);
        }
    }

    internal static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        await TestWait.ForAsync(
            condition,
            value => value,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(25),
            "system update condition");
    }
}
