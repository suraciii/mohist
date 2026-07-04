using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.SystemInfo;
using Mohist.Server.Tests.Support;
using Xunit;
using EnvironmentAbstractions.TestHelpers;

namespace Mohist.Server.Tests.Specs.SystemSpecs;

public class SystemUpdateRecoverySpecs
{
    private static readonly DateTimeOffset ProcessStart = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task StartAsync_StaleRunningJob_IsMarkedFailedAndLockReleased()
    {
        var store = new InMemoryRecoveryStore();
        var time = new FakeTimeProvider(ProcessStart.AddMinutes(1));
        var persistedUpdatedAt = ProcessStart.AddMinutes(-5);
        await store.SaveAsync(BuildJob("stale-job", "running", persistedUpdatedAt));

        var reconciler = BuildReconciler(store, time, ProcessStart);

        await reconciler.StartAsync(CancellationToken.None);

        var latest = await store.GetLatestAsync();
        Assert.NotNull(latest);
        Assert.Equal("stale-job", latest!.JobId);
        Assert.Equal("failed", latest.Status);
        Assert.Equal("Failed", latest.Stage);
        Assert.Equal(SystemUpdateRecoveryService.InterruptedByProcessRestartReason, latest.Reason);
        Assert.Equal(time.GetUtcNow(), latest.UpdatedAt);
        Assert.Equal(time.GetUtcNow(), latest.CompletedAt);
        Assert.NotEmpty(latest.Logs);
        Assert.Contains(latest.Logs, entry => entry.Stage == "Failed"
            && entry.Message.Contains(SystemUpdateRecoveryService.InterruptedByProcessRestartReason));

        Assert.Contains(store.ReleasedStaleJobs, jobId => jobId == "stale-job");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task StartAsync_StaleWaitingForReconnectJob_IsMarkedFailedAndLockReleased()
    {
        var store = new InMemoryRecoveryStore();
        var time = new FakeTimeProvider(ProcessStart.AddMinutes(2));
        var persistedUpdatedAt = ProcessStart.AddMinutes(-1);
        await store.SaveAsync(BuildJob("waiting-job", "waiting-for-reconnect", persistedUpdatedAt));

        var reconciler = BuildReconciler(store, time, ProcessStart);

        await reconciler.StartAsync(CancellationToken.None);

        var latest = await store.GetLatestAsync();
        Assert.NotNull(latest);
        Assert.Equal("waiting-job", latest!.JobId);
        Assert.Equal("failed", latest.Status);
        Assert.Equal(SystemUpdateRecoveryService.InterruptedByProcessRestartReason, latest.Reason);
        Assert.Contains(store.ReleasedStaleJobs, jobId => jobId == "waiting-job");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task StartAsync_NoPersistedJob_IsNoOp()
    {
        var store = new InMemoryRecoveryStore();
        var time = new FakeTimeProvider(ProcessStart.AddMinutes(1));

        var reconciler = BuildReconciler(store, time, ProcessStart);

        await reconciler.StartAsync(CancellationToken.None);

        Assert.Empty(store.SavedStates);
        Assert.Empty(store.ReleasedStaleJobs);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task StartAsync_FreshActiveJob_IsLeftInPlace()
    {
        var store = new InMemoryRecoveryStore();
        var time = new FakeTimeProvider(ProcessStart.AddMinutes(3));
        var freshUpdatedAt = ProcessStart.AddMinutes(1);
        await store.SaveAsync(BuildJob("fresh-job", "running", freshUpdatedAt));

        var reconciler = BuildReconciler(store, time, ProcessStart);

        await reconciler.StartAsync(CancellationToken.None);

        var latest = await store.GetLatestAsync();
        Assert.NotNull(latest);
        Assert.Equal("fresh-job", latest!.JobId);
        Assert.Equal("running", latest.Status);
        Assert.Equal(freshUpdatedAt, latest.UpdatedAt);
        Assert.Empty(store.ReleasedStaleJobs);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task StartAsync_FreshWaitingForReconnectJob_IsLeftInPlace()
    {
        var store = new InMemoryRecoveryStore();
        var time = new FakeTimeProvider(ProcessStart.AddMinutes(3));
        var freshUpdatedAt = ProcessStart;
        await store.SaveAsync(BuildJob("fresh-waiting", "waiting-for-reconnect", freshUpdatedAt));

        var reconciler = BuildReconciler(store, time, ProcessStart);

        await reconciler.StartAsync(CancellationToken.None);

        var latest = await store.GetLatestAsync();
        Assert.NotNull(latest);
        Assert.Equal("waiting-for-reconnect", latest!.Status);
        Assert.Empty(store.ReleasedStaleJobs);
    }

    [Theory]
    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [InlineData("succeeded")]
    [InlineData("failed")]
    [InlineData("recovered")]
    [InlineData("superseded")]
    [InlineData("cancelled")]
    public async Task StartAsync_TerminalJob_IsNeverModifiedRegardlessOfUpdatedAt(string terminalStatus)
    {
        var store = new InMemoryRecoveryStore();
        var time = new FakeTimeProvider(ProcessStart.AddMinutes(10));
        var oldUpdatedAt = ProcessStart.AddMinutes(-30);
        var original = BuildJob("terminal-job", terminalStatus, oldUpdatedAt, completedAt: oldUpdatedAt);
        await store.SaveAsync(original);

        var reconciler = BuildReconciler(store, time, ProcessStart);

        await reconciler.StartAsync(CancellationToken.None);

        var latest = await store.GetLatestAsync();
        Assert.NotNull(latest);
        Assert.Equal(terminalStatus, latest!.Status);
        Assert.Equal(oldUpdatedAt, latest.UpdatedAt);
        Assert.Equal(oldUpdatedAt, latest.CompletedAt);
        Assert.Empty(store.ReleasedStaleJobs);
        Assert.Single(store.SavedStates);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task StartAsync_AfterRecovery_FreshStartAsyncAcquiresLockWithoutUpdateInProgress()
    {
        var store = new InMemoryRecoveryStore();
        var time = new FakeTimeProvider(ProcessStart.AddMinutes(1));
        var staleUpdatedAt = ProcessStart.AddMinutes(-2);
        await store.SaveAsync(BuildJob("stale-job", "running", staleUpdatedAt));

        var reconciler = BuildReconciler(store, time, ProcessStart);
        await reconciler.StartAsync(CancellationToken.None);

        var service = CreateSystemUpdateService(store, time);

        var result = await service.StartAsync(new SystemUpdateRequest(), CancellationToken.None);

        Assert.True(result.Started);
        Assert.Null(result.Code);
        Assert.Null(result.Error);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task StartAsync_AfterFileSystemStoreRecovery_FreshStartAsyncAcquiresLock()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"mohist-recovery-{Guid.NewGuid():N}.json");
        try
        {
            var first = CreateFileSystemStore(statePath);
            Assert.True(await first.TryAcquireLockAsync("stale-job"));
            var staleUpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
            await first.SaveAsync(BuildJob("stale-job", "running", staleUpdatedAt));

            var refreshed = CreateFileSystemStore(statePath);
            Assert.True(File.Exists(statePath + ".lock"));
            Assert.False(await refreshed.TryAcquireLockAsync("new-job"));

            var reconciler = BuildReconciler(refreshed, new FakeTimeProvider(staleUpdatedAt.AddMinutes(10)), staleUpdatedAt.AddMinutes(1));
            await reconciler.StartAsync(CancellationToken.None);

            Assert.False(File.Exists(statePath + ".lock"));

            Assert.True(await refreshed.TryAcquireLockAsync("new-job"));
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
    public void ProcessStartTimeProvider_DefaultReadsActualProcess()
    {
        var provider = new ProcessStartTimeProvider();

        var start = provider.GetStartTime();

        Assert.True(start <= DateTimeOffset.UtcNow);
        Assert.Equal(TimeSpan.Zero, start.Offset);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public void SourceAudit_ReconcilerHasNoWallClockOrProcessInfoReads()
    {
        var source = File.ReadAllText(SourcePath);
        var codeOnly = StripXmlDocComments(source);

        Assert.DoesNotContain("DateTimeOffset.UtcNow", codeOnly);
        Assert.DoesNotContain("DateTime.UtcNow", codeOnly);
        Assert.DoesNotContain("Environment.TickCount", codeOnly);
        Assert.DoesNotContain("GetCurrentProcess", codeOnly);
        Assert.DoesNotContain("Process.StartTime", codeOnly);
        Assert.DoesNotContain("Process.GetCurrentProcess", codeOnly);
        Assert.DoesNotContain("SystemUpdateService", codeOnly);
    }

    private static string StripXmlDocComments(string source)
    {
        return Regex.Replace(source, @"<see\s+cref\s*=\s*""[^""]*""\s*/>", string.Empty);
    }

    private static SystemUpdateRecoveryService BuildReconciler(
        ISystemUpdateStore store,
        TimeProvider time,
        DateTimeOffset processStart)
    {
        return new SystemUpdateRecoveryService(
            store,
            time,
            new FakeProcessStartTimeProvider(processStart),
            NullLogger<SystemUpdateRecoveryService>.Instance);
    }

    private static SystemUpdateService CreateSystemUpdateService(ISystemUpdateStore store, TimeProvider time)
    {
        var systemInfo = new SystemInfoResponse(
            new RunningInfo("1.0.0", "oldhash", DateTimeOffset.UtcNow),
            new SourceInfo("/repo", "main", "newhash", false),
            new InstallInfo("local-source", "systemd-user", "mohist.service", "mohist-runner.service", "local-source"),
            new UpdateInfo("update-available", true, null),
            new ServiceInfo("active", "active"),
            new SystemPaths("/db", "/config", "/logs", "/opencode"));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mohist:SystemUpdate:Enabled"] = "true"
        }).Build();
        return new SystemUpdateService(
            _ => Task.FromResult(systemInfo),
            store,
            new StubCommandRunner(),
            new StubReadinessProbe(new SystemReadinessResult(true, true, true, "/assets/app.js", null)),
            configuration,
            new MockEnvironmentVariableProvider(),
            NullLogger<SystemUpdateService>.Instance,
            time);
    }

    private static FileSystemSystemUpdateStore CreateFileSystemStore(string statePath)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mohist:SystemUpdate:StatePath"] = statePath
        }).Build();
        return new FileSystemSystemUpdateStore(configuration);
    }

    private static SystemUpdateJobState BuildJob(
        string jobId,
        string status,
        DateTimeOffset updatedAt,
        DateTimeOffset? completedAt = null)
    {
        return new SystemUpdateJobState(
            jobId,
            status,
            status == "running" ? "Building" : status == "waiting-for-reconnect" ? "Waiting for reconnect" : status,
            true,
            "oldhash",
            "newhash",
            "/repo",
            "mohist.service",
            "mohist-runner.service",
            null,
            [new SystemUpdateLogEntry(updatedAt, status, $"entry-{jobId}")],
            updatedAt,
            updatedAt,
            completedAt);
    }

    private static string SourcePath => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "src", "Mohist.Server", "SystemInfo", "SystemUpdateRecoveryService.cs"));

    private sealed class FakeProcessStartTimeProvider : IProcessStartTimeProvider
    {
        private readonly DateTimeOffset _start;

        public FakeProcessStartTimeProvider(DateTimeOffset start) => _start = start;

        public DateTimeOffset GetStartTime() => _start;
    }

    private sealed class InMemoryRecoveryStore : ISystemUpdateStore
    {
        private readonly object _gate = new();
        private SystemUpdateJobState? _latest;
        public List<SystemUpdateJobState> SavedStates { get; } = [];
        public List<string> ReleasedStaleJobs { get; } = [];

        public Task<SystemUpdateJobState?> GetLatestAsync(CancellationToken cancellationToken = default)
        {
            lock (_gate) return Task.FromResult(_latest);
        }

        public Task<bool> TryAcquireLockAsync(string jobId, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (_latest?.Status is "running" or "waiting-for-reconnect")
                    return Task.FromResult(false);
                return Task.FromResult(true);
            }
        }

        public Task ReleaseLockAsync(string jobId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task ReleaseStaleLockAsync(string jobId, CancellationToken cancellationToken = default)
        {
            lock (_gate) ReleasedStaleJobs.Add(jobId);
            return Task.CompletedTask;
        }

        public Task SaveAsync(SystemUpdateJobState state, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _latest = state;
                SavedStates.Add(state);
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
                return Task.FromResult(true);
            }
        }
    }

    private sealed class StubCommandRunner : ISystemUpdateCommandRunner
    {
        public Task<SystemCommandResult> RunAsync(SystemCommandRequest command, CancellationToken cancellationToken = default)
            => Task.FromResult(new SystemCommandResult(0, "ok"));
    }

    private sealed class StubReadinessProbe : ISystemReadinessProbe
    {
        private readonly SystemReadinessResult _result;
        public StubReadinessProbe(SystemReadinessResult result) => _result = result;
        public Task<SystemReadinessResult> ProbeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_result);
    }
}