using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.SystemInfo;
using Mohist.Server.Tests.Support;
using Xunit;
using EnvironmentAbstractions.TestHelpers;

namespace Mohist.Server.Tests.Specs;

public class SystemUpdateServiceSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task StartAsync_DirtySourceRejectsRequestEvenWhenForceIsSent()
    {
        var service = CreateService(
            systemInfo: CreateInfo(updateStatus: "dirty-source", available: true, sourceDirty: true),
            store: new InMemoryUpdateStore(),
            commandRunner: new RecordingCommandRunner(),
            readinessProbe: new StubReadinessProbe(new(true, true, true, "/assets/app.js", null)));

        var result = await service.StartAsync(new SystemUpdateRequest(), CancellationToken.None);

        Assert.False(result.Started);
        Assert.Equal("dirty_source", result.Code);
        Assert.Equal("Source tree has uncommitted changes", result.Error);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task StartAsync_RunsOnlyFixedCommandsAndPersistsWaitingState()
    {
        var store = new InMemoryUpdateStore();
        var commands = new RecordingCommandRunner();
        var service = CreateService(
            systemInfo: CreateInfo(),
            store: store,
            commandRunner: commands,
            readinessProbe: new StubReadinessProbe(new(false, false, false, null, "API health endpoint is not ready")));

        var result = await service.StartAsync(new SystemUpdateRequest(), CancellationToken.None);
        await commands.WaitForCountAsync(2);

        Assert.True(result.Started);
        Assert.Collection(commands.Requests,
            command =>
            {
                Assert.Equal("dotnet", command.FileName);
                Assert.Equal(["build", "Mohist.sln"], command.Arguments);
                Assert.Equal("/repo", command.WorkingDirectory);
            },
            command =>
            {
                Assert.Equal("systemctl", command.FileName);
                Assert.Equal(["--user", "restart", "mohist.service"], command.Arguments);
                Assert.Equal("/repo", command.WorkingDirectory);
            });

        var latest = await store.GetLatestAsync();
        Assert.NotNull(latest);
        Assert.Equal("waiting-for-reconnect", latest!.Status);
        Assert.Equal("Waiting for reconnect", latest.Stage);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task StartAsync_ReturnsFullPersistedStatusPayload()
    {
        var store = new InMemoryUpdateStore();
        var service = CreateService(
            systemInfo: CreateInfo(),
            store: store,
            commandRunner: new RecordingCommandRunner(),
            readinessProbe: new StubReadinessProbe(new(false, false, false, null, "API health endpoint is not ready")));

        var result = await service.StartAsync(new SystemUpdateRequest(), CancellationToken.None);

        Assert.True(result.Started);
        Assert.NotNull(result.Status);
        Assert.True(result.Status!.UpdateAvailable);
        Assert.Equal("oldhash", result.Status.RunningGitHash);
        Assert.Equal("newhash", result.Status.SourceHead);
        Assert.Equal("/repo", result.Status.SourcePath);
        Assert.Equal("mohist.service", result.Status.ServerUnit);
        Assert.Equal("mohist-runner.service", result.Status.RunnerUnit);
        Assert.NotEmpty(result.Status.Logs);
        Assert.NotEqual(default, result.Status.CreatedAt);
        Assert.NotEqual(default, result.Status.UpdatedAt);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task GetLatestStatusAsync_WhenReady_RestartsRunnerBeforeReadyCompletion()
    {
        var store = new InMemoryUpdateStore();
        var commands = new RecordingCommandRunner();
        var now = DateTimeOffset.UtcNow;
        await store.SaveAsync(new SystemUpdateJobState(
            "job-1",
            "waiting-for-reconnect",
            "Waiting for reconnect",
            true,
            "oldhash",
            "newhash",
            "/repo",
            "mohist.service",
            "mohist-runner.service",
            "Waiting for restart",
            [new SystemUpdateLogEntry(now, "Waiting for reconnect", "Waiting for restart")],
            now,
            now,
            null));

        var service = CreateService(
            new SequencedSystemInfo(CreateInfo(runningGitHash: "newhash", sourceHead: "newhash")),
            store,
            commands,
            new StubReadinessProbe(new(true, true, true, "/assets/app.js", null)));

        var status = await service.GetLatestStatusAsync();

        Assert.Equal("succeeded", status!.Status);
        Assert.Equal("Ready", status.Stage);
        Assert.Collection(commands.Requests, command =>
        {
            Assert.Equal("systemctl", command.FileName);
            Assert.Equal(["--user", "restart", "mohist-runner.service"], command.Arguments);
            Assert.Equal("/repo", command.WorkingDirectory);
        });
        Assert.True(await store.TryAcquireLockAsync("job-2"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task StartAsync_UnsupportedInstall_IsRejectedWithoutRunningCommands()
    {
        var commands = new RecordingCommandRunner();
        var service = CreateService(
            systemInfo: CreateInfo(installMode: "binary", updateStatus: "unsupported", available: false),
            store: new InMemoryUpdateStore(),
            commandRunner: commands,
            readinessProbe: new StubReadinessProbe(new(true, true, true, "/assets/app.js", null)));

        var result = await service.StartAsync(new SystemUpdateRequest(), CancellationToken.None);

        Assert.False(result.Started);
        Assert.Equal("unsupported_install", result.Code);
        Assert.Empty(commands.Requests);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task StartAsync_WhenNoUpdateAvailable_IsRejectedWithoutRunningCommands()
    {
        var commands = new RecordingCommandRunner();
        var service = CreateService(
            systemInfo: CreateInfo(updateStatus: "up-to-date", available: false),
            store: new InMemoryUpdateStore(),
            commandRunner: commands,
            readinessProbe: new StubReadinessProbe(new(true, true, true, "/assets/app.js", null)));

        var result = await service.StartAsync(new SystemUpdateRequest(), CancellationToken.None);

        Assert.False(result.Started);
        Assert.Equal("no_update_available", result.Code);
        Assert.Empty(commands.Requests);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task StartAsync_WhenUpdateAlreadyRunning_ReturnsConflict()
    {
        var store = new InMemoryUpdateStore(acquireLock: false);
        var service = CreateService(
            systemInfo: CreateInfo(),
            store: store,
            commandRunner: new RecordingCommandRunner(),
            readinessProbe: new StubReadinessProbe(new(true, true, true, "/assets/app.js", null)));

        var result = await service.StartAsync(new SystemUpdateRequest(), CancellationToken.None);

        Assert.False(result.Started);
        Assert.Equal("update_in_progress", result.Code);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task StartAsync_WhenPersistedActiveJobExistsAfterRestart_ReturnsConflict()
    {
        var store = new InMemoryUpdateStore();
        var now = DateTimeOffset.UtcNow;
        await store.SaveAsync(new SystemUpdateJobState(
            "job-1",
            "waiting-for-reconnect",
            "Waiting for reconnect",
            true,
            "oldhash",
            "newhash",
            "/repo",
            "mohist.service",
            "mohist-runner.service",
            "Waiting for restart",
            [new SystemUpdateLogEntry(now, "Waiting for reconnect", "Waiting for restart")],
            now,
            now,
            null));

        var service = CreateService(
            systemInfo: CreateInfo(),
            store: store,
            commandRunner: new RecordingCommandRunner(),
            readinessProbe: new StubReadinessProbe(new(true, true, true, "/assets/app.js", null)));

        var result = await service.StartAsync(new SystemUpdateRequest(), CancellationToken.None);

        Assert.False(result.Started);
        Assert.Equal("update_in_progress", result.Code);
        Assert.NotNull(result.Status);
        Assert.Equal("job-1", result.Status!.JobId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task FileSystemStore_TryAcquireLockAsync_IsDurableAcrossStoreInstances()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"mohist-system-update-{Guid.NewGuid():N}.json");
        try
        {
            var first = CreateFileSystemStore(statePath);
            var second = CreateFileSystemStore(statePath);

            Assert.True(await first.TryAcquireLockAsync("job-1"));
            Assert.False(await second.TryAcquireLockAsync("job-2"));

            await first.ReleaseLockAsync("job-1");
            Assert.True(await second.TryAcquireLockAsync("job-2"));
        }
        finally
        {
            if (File.Exists(statePath))
                File.Delete(statePath);
            if (File.Exists(statePath + ".lock"))
                File.Delete(statePath + ".lock");
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task FileSystemStore_TryAcquireLockAsync_RejectsPersistedActiveJobAfterRestart()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"mohist-system-update-{Guid.NewGuid():N}.json");
        try
        {
            var now = DateTimeOffset.UtcNow;
            var first = CreateFileSystemStore(statePath);
            await first.SaveAsync(new SystemUpdateJobState(
                "job-1",
                "waiting-for-reconnect",
                "Waiting for reconnect",
                true,
                "oldhash",
                "newhash",
                "/repo",
                "mohist.service",
                "mohist-runner.service",
                "Waiting",
                [new SystemUpdateLogEntry(now, "Waiting for reconnect", "Waiting")],
                now,
                now,
                null));

            var restarted = CreateFileSystemStore(statePath);

            Assert.False(await restarted.TryAcquireLockAsync("job-2"));
        }
        finally
        {
            if (File.Exists(statePath))
                File.Delete(statePath);
            if (File.Exists(statePath + ".lock"))
                File.Delete(statePath + ".lock");
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task StartAsync_WhenInstallFactsChangeBeforeExecution_FailsWithoutCommands()
    {
        var store = new InMemoryUpdateStore();
        var commands = new RecordingCommandRunner();
        var service = CreateService(
            new SequencedSystemInfo(
                CreateInfo(sourceHead: "newhash"),
                CreateInfo(sourceHead: "newhash", sourcePath: "/other-repo")),
            store,
            commands,
            new StubReadinessProbe(new(true, true, true, "/assets/app.js", null)));

        var result = await service.StartAsync(new SystemUpdateRequest(), CancellationToken.None);
        await WaitUntilAsync(async () => (await store.GetLatestAsync())?.Status == "failed");

        var latest = await store.GetLatestAsync();
        Assert.True(result.Started);
        Assert.Empty(commands.Requests);
        Assert.Equal("failed", latest!.Status);
        Assert.Equal("Trusted install facts changed before update execution", latest.Reason);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task StartAsync_KeepsLockWhileWaitingForReconnect()
    {
        var store = new InMemoryUpdateStore();
        var commands = new RecordingCommandRunner();
        var service = CreateService(
            systemInfo: CreateInfo(),
            store: store,
            commandRunner: commands,
            readinessProbe: new StubReadinessProbe(new(false, false, false, null, "API health endpoint is not ready")));

        var result = await service.StartAsync(new SystemUpdateRequest(), CancellationToken.None);
        await commands.WaitForCountAsync(2);
        await WaitUntilAsync(async () => (await store.GetLatestAsync())?.Status == "waiting-for-reconnect");

        Assert.True(result.Started);
        Assert.False(await store.TryAcquireLockAsync("job-2"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task GetLatestStatusAsync_DoesNotSucceedUntilReadinessAndHashMatch()
    {
        var store = new InMemoryUpdateStore();
        var now = DateTimeOffset.UtcNow;
        await store.SaveAsync(new SystemUpdateJobState(
            "job-1",
            "waiting-for-reconnect",
            "Waiting for reconnect",
            true,
            "oldhash",
            "newhash",
            "/repo",
            "mohist.service",
            "mohist-runner.service",
            "Waiting for restart",
            [new SystemUpdateLogEntry(now, "Waiting for reconnect", "Waiting for restart")],
            now,
            now,
            null));

        var readiness = new SequenceReadinessProbe(
            new(false, false, false, null, "API health endpoint is not ready"),
            new(true, true, false, "/assets/app.js", "Bundled asset is not ready"),
            new(true, true, true, "/assets/app.js", null));
        var systemInfo = new SequencedSystemInfo(
            CreateInfo(runningGitHash: "oldhash", sourceHead: "newhash"),
            CreateInfo(runningGitHash: "newhash", sourceHead: "newhash"));
        var service = CreateService(systemInfo, store, new RecordingCommandRunner(), readiness);

        var first = await service.GetLatestStatusAsync();
        var second = await service.GetLatestStatusAsync();
        var third = await service.GetLatestStatusAsync();
        var fourth = await service.GetLatestStatusAsync();

        Assert.Equal("waiting-for-reconnect", first!.Status);
        Assert.Equal("waiting-for-reconnect", second!.Status);
        Assert.Equal("waiting-for-reconnect", third!.Status);
        Assert.Equal("succeeded", fourth!.Status);
        Assert.Equal("Ready", fourth.Stage);
        Assert.Contains(fourth.Logs, log => log.Stage == "Ready" && log.Message.Contains("asset /assets/app.js is ready"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task GetLatestStatusAsync_PersistsReadinessFailuresAcrossReconnectBoundary()
    {
        var store = new InMemoryUpdateStore();
        var now = DateTimeOffset.UtcNow;
        await store.SaveAsync(new SystemUpdateJobState(
            "job-1",
            "waiting-for-reconnect",
            "Waiting for reconnect",
            true,
            "oldhash",
            "newhash",
            "/repo",
            "mohist.service",
            "mohist-runner.service",
            "Waiting for restart",
            [new SystemUpdateLogEntry(now, "Waiting for reconnect", "Waiting for restart")],
            now,
            now,
            null));

        var readiness = new SequenceReadinessProbe(
            new(false, false, false, null, "API health endpoint is not ready"),
            new(true, true, false, "/assets/app.js", "Bundled asset is not ready"));
        var service = CreateService(new SequencedSystemInfo(CreateInfo(runningGitHash: "oldhash", sourceHead: "newhash")), store, new RecordingCommandRunner(), readiness);

        var first = await service.GetLatestStatusAsync();
        var second = await service.GetLatestStatusAsync();

        Assert.Equal("API health endpoint is not ready", first!.Reason);
        Assert.Equal("Bundled asset is not ready", second!.Reason);
        Assert.Contains(second.Logs, log => log.Stage == "Waiting for reconnect" && log.Message.Contains("Bundled asset is not ready"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task GetLatestStatusAsync_BoundsPersistedLogEntries()
    {
        var store = new InMemoryUpdateStore();
        var now = DateTimeOffset.UtcNow;
        var logs = Enumerable.Range(0, 220)
            .Select(i => new SystemUpdateLogEntry(now.AddSeconds(i), "Waiting for reconnect", $"entry-{i}"))
            .ToArray();
        await store.SaveAsync(new SystemUpdateJobState(
            "job-1",
            "waiting-for-reconnect",
            "Waiting for reconnect",
            true,
            "oldhash",
            "newhash",
            "/repo",
            "mohist.service",
            "mohist-runner.service",
            "Waiting for restart",
            logs,
            now,
            now,
            null));

        var service = CreateService(
            new SequencedSystemInfo(CreateInfo(runningGitHash: "oldhash", sourceHead: "newhash")),
            store,
            new RecordingCommandRunner(),
            new StubReadinessProbe(new(false, false, false, null, "Still waiting")));

        await service.GetLatestStatusAsync();

        var latest = await store.GetLatestAsync();
        Assert.Equal(200, latest!.Logs.Count);
        Assert.DoesNotContain(latest.Logs, log => log.Message == "entry-0");
        Assert.Contains(latest.Logs, log => log.Message == "Still waiting");
    }

    private static SystemUpdateService CreateService(
        SystemInfoResponse systemInfo,
        ISystemUpdateStore store,
        ISystemUpdateCommandRunner commandRunner,
        ISystemReadinessProbe readinessProbe)
    {
        return CreateService(new SequencedSystemInfo(systemInfo), store, commandRunner, readinessProbe);
    }

    private static FileSystemSystemUpdateStore CreateFileSystemStore(string statePath)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mohist:SystemUpdate:StatePath"] = statePath
        }).Build();
        return new FileSystemSystemUpdateStore(configuration);
    }

    private static SystemUpdateService CreateService(
        SequencedSystemInfo systemInfo,
        ISystemUpdateStore store,
        ISystemUpdateCommandRunner commandRunner,
        ISystemReadinessProbe readinessProbe)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mohist:SystemUpdate:Enabled"] = "true"
        }).Build();

        return new SystemUpdateService(
            systemInfo.GetSystemInfoAsync,
            store,
            commandRunner,
            readinessProbe,
            configuration,
            new MockEnvironmentVariableProvider(),
            NullLogger<SystemUpdateService>.Instance);
    }

    private static SystemInfoResponse CreateInfo(
        string updateStatus = "update-available",
        bool available = true,
        bool sourceDirty = false,
        string? runningGitHash = "oldhash",
        string? sourceHead = "newhash",
        string installMode = "local-source",
        string? sourcePath = "/repo")
    {
        return new SystemInfoResponse(
            new RunningInfo("1.2.3", runningGitHash, DateTimeOffset.UtcNow),
            new SourceInfo(sourcePath, "main", sourceHead, sourceDirty),
            new InstallInfo(installMode, "systemd-user", "mohist.service", "mohist-runner.service", installMode),
            new UpdateInfo(updateStatus, available, updateStatus),
            new ServiceInfo("active", "active"),
            new SystemPaths("/db", "/config", "/logs", "/opencode"));
    }

    private sealed class InMemoryUpdateStore : ISystemUpdateStore
    {
        private readonly bool _acquireLock;
        private SystemUpdateJobState? _latest;
        private bool _locked;
        private string? _lockOwnerJobId;

        public InMemoryUpdateStore(bool acquireLock = true)
        {
            _acquireLock = acquireLock;
        }

        public Task<SystemUpdateJobState?> GetLatestAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_latest);

        public Task<bool> TryAcquireLockAsync(string jobId, CancellationToken cancellationToken = default)
        {
            if (!_acquireLock || _locked || _latest?.Status is "running" or "waiting-for-reconnect")
                return Task.FromResult(false);

            _locked = true;
            _lockOwnerJobId = jobId;
            return Task.FromResult(true);
        }

        public Task ReleaseLockAsync(string jobId, CancellationToken cancellationToken = default)
        {
            if (_lockOwnerJobId == jobId)
            {
                _locked = false;
                _lockOwnerJobId = null;
            }

            return Task.CompletedTask;
        }

        public Task SaveAsync(SystemUpdateJobState state, CancellationToken cancellationToken = default)
        {
            _latest = state;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingCommandRunner : ISystemUpdateCommandRunner
    {
        private readonly TaskCompletionSource _commandsSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<SystemCommandRequest> Requests { get; } = [];

        public Task<SystemCommandResult> RunAsync(SystemCommandRequest command, CancellationToken cancellationToken = default)
        {
            Requests.Add(command);
            _commandsSeen.TrySetResult();

            return Task.FromResult(new SystemCommandResult(0, $"ok:{command.Stage}"));
        }

        public async Task WaitForCountAsync(int count)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (Requests.Count < count)
            {
                cts.Token.ThrowIfCancellationRequested();
                await _commandsSeen.Task.WaitAsync(cts.Token);
                if (Requests.Count < count)
                    await Task.Delay(25, cts.Token);
            }
        }
    }

    private sealed class StubReadinessProbe : ISystemReadinessProbe
    {
        private readonly SystemReadinessResult _result;

        public StubReadinessProbe(SystemReadinessResult result)
        {
            _result = result;
        }

        public Task<SystemReadinessResult> ProbeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_result);
    }

    private sealed class SequenceReadinessProbe : ISystemReadinessProbe
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

    private sealed class SequencedSystemInfo
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

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!await condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(25, cts.Token);
        }
    }
}
