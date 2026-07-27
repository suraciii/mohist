using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.SystemInfo;
using Mohist.Server.UnitTests.Support;
using Xunit;
using EnvironmentAbstractions.TestHelpers;

namespace Mohist.Server.UnitTests.SystemSpecs;

public class SystemUpdateRecoveryTests
{
    private static readonly DateTimeOffset ProcessStart = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FixtureNow = new(2026, 7, 1, 12, 30, 0, TimeSpan.Zero);

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

    [Fact]
    public async Task StartAsync_WhenStaleLockCannotBeReleased_DoesNotCommitTerminalState()
    {
        var store = new InMemoryRecoveryStore { ReleaseStaleLockSucceeds = false };
        var time = new FakeTimeProvider(ProcessStart.AddMinutes(1));
        var persistedUpdatedAt = ProcessStart.AddMinutes(-5);
        await store.SaveAsync(BuildJob("stale-job", "running", persistedUpdatedAt));

        var reconciler = BuildReconciler(store, time, ProcessStart);

        await reconciler.StartAsync(CancellationToken.None);

        var latest = await store.GetLatestAsync();
        Assert.NotNull(latest);
        Assert.Equal("running", latest!.Status);
        Assert.Equal(persistedUpdatedAt, latest.UpdatedAt);
        Assert.Single(store.SavedStates);
    }

    [Fact]
    public async Task StartAsync_StaleRecoveryLog_IsCappedAtTwoHundredEntries()
    {
        var store = new InMemoryRecoveryStore();
        var time = new FakeTimeProvider(ProcessStart.AddMinutes(1));
        var persistedUpdatedAt = ProcessStart.AddMinutes(-5);
        var existingLogs = Enumerable.Range(0, 200)
            .Select(index => new SystemUpdateLogEntry(persistedUpdatedAt.AddSeconds(index), "Building", $"entry-{index}"))
            .ToArray();
        await store.SaveAsync(BuildJob("stale-job", "running", persistedUpdatedAt, logs: existingLogs));

        var reconciler = BuildReconciler(store, time, ProcessStart);

        await reconciler.StartAsync(CancellationToken.None);

        var latest = await store.GetLatestAsync();
        Assert.NotNull(latest);
        Assert.Equal(200, latest!.Logs.Count);
        Assert.DoesNotContain(latest.Logs, entry => entry.Message == "entry-0");
        Assert.Contains(latest.Logs, entry => entry.Stage == "Failed"
            && entry.Message.Contains(SystemUpdateRecoveryService.InterruptedByProcessRestartReason));
    }

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
            new RunningInfo("1.0.0", "oldhash", FixtureNow),
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
            new AvailableManagedAssetCatalog(),
            NullLogger<SystemUpdateService>.Instance,
            time,
            NoopBackgroundTaskLauncher.Instance);
    }

    private static SystemUpdateJobState BuildJob(
        string jobId,
        string status,
        DateTimeOffset updatedAt,
        DateTimeOffset? completedAt = null,
        IReadOnlyList<SystemUpdateLogEntry>? logs = null,
        string? reason = null)
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
            reason,
            logs ?? [new SystemUpdateLogEntry(updatedAt, status, $"entry-{jobId}")],
            updatedAt,
            updatedAt,
            completedAt);
    }

    private sealed class AvailableManagedAssetCatalog : IManagedAssetCatalog
    {
        public ManagedAssetCatalogState GetState() => ManagedAssetCatalogState.Available;
    }

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
        public bool ReleaseStaleLockSucceeds { get; init; } = true;

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

        public Task<bool> ReleaseStaleLockAsync(string jobId, CancellationToken cancellationToken = default)
        {
            lock (_gate) ReleasedStaleJobs.Add(jobId);
            return Task.FromResult(ReleaseStaleLockSucceeds);
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
