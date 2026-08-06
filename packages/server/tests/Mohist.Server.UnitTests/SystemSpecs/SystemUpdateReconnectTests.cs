using Microsoft.Extensions.Time.Testing;
using Mohist.Server.SystemInfo;
using Xunit;
using static Mohist.Server.TestSupport.SystemSpecs.SystemUpdateTestFactory;

namespace Mohist.Server.UnitTests.SystemSpecs;

public class SystemUpdateReconnectTests
{
    [Fact]
    public async Task AdvanceActiveJobAsync_WhenReady_RestartsRunnerBeforeReadyCompletion()
    {
        var store = new InMemoryUpdateStore();
        var commands = new RecordingCommandRunner();
        var now = FixedNow;
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

        await service.AdvanceActiveJobAsync();

        var latest = await store.GetLatestAsync();
        Assert.Equal("succeeded", latest!.Status);
        Assert.Equal("Ready", latest.Stage);
        Assert.Equal(latest.CompletedAt, latest.UpdatedAt);
        Assert.Collection(commands.Requests, command =>
        {
            Assert.Equal("systemctl", command.FileName);
            Assert.Equal(["--user", "restart", "mohist-runner.service"], command.Arguments);
            Assert.Equal("/repo", command.WorkingDirectory);
        });
        Assert.True(await store.TryAcquireLockAsync("job-2"));
    }

    [Fact]
    public async Task GetStatusEnvelopeAsync_DoesNotSucceedUntilReadinessAndHashMatch()
    {
        var store = new InMemoryUpdateStore();
        var now = FixedNow;
        await store.SaveAsync(new SystemUpdateJobState(
            "job-1",
            "waiting-for-reconnect",
            "Waiting for reconnect",
            true,
            "newhash",
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
            CreateInfo(runningGitHash: "newhash", sourceHead: "newhash"),
            CreateInfo(runningGitHash: "newhash", sourceHead: "newhash"));
        var service = CreateService(systemInfo, store, new RecordingCommandRunner(), readiness);

        var first = await service.GetStatusEnvelopeAsync();
        var second = await service.GetStatusEnvelopeAsync();
        var third = await service.GetStatusEnvelopeAsync();

        Assert.Equal("waiting-for-reconnect", first.Job!.Status);
        Assert.Equal("waiting-for-reconnect", second.Job!.Status);
        Assert.Equal("succeeded", third.Job!.Status);
        Assert.Equal("Ready", third.Job.Stage);
        Assert.Contains(third.Job.Logs, log => log.Stage == "Ready" && log.Message.Contains("asset /assets/app.js is ready"));
    }

    [Fact]
    public async Task GetStatusEnvelopeAsync_PersistsReadinessFailuresAcrossReconnectBoundary()
    {
        var store = new InMemoryUpdateStore();
        var now = FixedNow;
        await store.SaveAsync(new SystemUpdateJobState(
            "job-1",
            "waiting-for-reconnect",
            "Waiting for reconnect",
            true,
            "newhash",
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
        var service = CreateService(new SequencedSystemInfo(CreateInfo(runningGitHash: "newhash", sourceHead: "newhash")), store, new RecordingCommandRunner(), readiness);

        var first = await service.GetStatusEnvelopeAsync();
        var second = await service.GetStatusEnvelopeAsync();

        Assert.Equal("API health endpoint is not ready", first.Job!.Reason);
        Assert.Equal("Bundled asset is not ready", second.Job!.Reason);
        Assert.Contains(second.Job.Logs, log => log.Stage == "Waiting for reconnect" && log.Message.Contains("Bundled asset is not ready"));
    }

    [Fact]
    public async Task AdvanceActiveJobAsync_DoesNotPersistDuplicateReadinessFailure()
    {
        var store = new InMemoryUpdateStore();
        var now = FixedNow;
        await store.SaveAsync(new SystemUpdateJobState(
            "job-1",
            "waiting-for-reconnect",
            "Waiting for reconnect",
            true,
            "newhash",
            "newhash",
            "/repo",
            "mohist.service",
            "mohist-runner.service",
            "Still waiting",
            [new SystemUpdateLogEntry(now, "Waiting for reconnect", "Still waiting")],
            now,
            now,
            null));

        var service = CreateService(
            new SequencedSystemInfo(CreateInfo(runningGitHash: "newhash", sourceHead: "newhash")),
            store,
            new RecordingCommandRunner(),
            new StubReadinessProbe(new(false, false, false, null, "Still waiting")));

        await service.AdvanceActiveJobAsync();

        var latest = await store.GetLatestAsync();
        Assert.Equal("waiting-for-reconnect", latest!.Status);
        Assert.Single(latest.Logs);
        Assert.Single(store.SavedStates);
    }

    [Fact]
    public async Task AdvanceActiveJobAsync_WaitingForReconnectTransition_RecordsAdvancedClockAsUpdatedAt()
    {
        var store = new InMemoryUpdateStore();
        var baseline = new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);
        await store.SaveAsync(new SystemUpdateJobState(
            "job-1",
            "waiting-for-reconnect",
            "Waiting for reconnect",
            true,
            "newhash",
            "newhash",
            "/repo",
            "mohist.service",
            "mohist-runner.service",
            "Initial wait",
            [new SystemUpdateLogEntry(baseline, "Waiting for reconnect", "Initial wait")],
            baseline,
            baseline,
            null));

        var (service, time) = CreateService(
            new SequencedSystemInfo(CreateInfo(runningGitHash: "newhash", sourceHead: "newhash")),
            store,
            new RecordingCommandRunner(),
            new StubReadinessProbe(new(false, false, false, null, "API health endpoint is not ready")),
            new FakeTimeProvider(baseline));

        var advanced = baseline.AddMinutes(3);
        time.Advance(advanced - baseline);

        await service.AdvanceActiveJobAsync();

        var latest = await store.GetLatestAsync();
        Assert.NotNull(latest);
        Assert.Equal("waiting-for-reconnect", latest!.Status);
        Assert.Equal("Waiting for reconnect", latest.Stage);
        Assert.Equal(advanced, latest.UpdatedAt);
        var waitingLog = Assert.Single(latest.Logs, entry => entry.Stage == "Waiting for reconnect" && entry.Message == "API health endpoint is not ready");
        Assert.Equal(advanced, waitingLog.At);
    }

    [Fact]
    public async Task AdvanceActiveJobAsync_BoundsPersistedLogEntries()
    {
        var store = new InMemoryUpdateStore();
        var now = FixedNow;
        var logs = Enumerable.Range(0, 220)
            .Select(i => new SystemUpdateLogEntry(now.AddSeconds(i), "Waiting for reconnect", $"entry-{i}"))
            .ToArray();
        await store.SaveAsync(new SystemUpdateJobState(
            "job-1",
            "waiting-for-reconnect",
            "Waiting for reconnect",
            true,
            "newhash",
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
            new SequencedSystemInfo(CreateInfo(runningGitHash: "newhash", sourceHead: "newhash")),
            store,
            new RecordingCommandRunner(),
            new StubReadinessProbe(new(false, false, false, null, "Still waiting")));

        await service.AdvanceActiveJobAsync();

        var latest = await store.GetLatestAsync();
        Assert.Equal(200, latest!.Logs.Count);
        Assert.DoesNotContain(latest.Logs, log => log.Message == "entry-0");
        Assert.Contains(latest.Logs, log => log.Message == "Still waiting");
    }

    [Fact]
    public async Task AdvanceActiveJobAsync_StaleWaitingForReconnectIsSuperseded()
    {
        var store = new InMemoryUpdateStore();
        var now = FixedNow;
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
            "Waiting",
            [new SystemUpdateLogEntry(now, "Waiting for reconnect", "Waiting")],
            now,
            now,
            null));

        var service = CreateService(
            new SequencedSystemInfo(CreateInfo(runningGitHash: "currenthash", sourceHead: "currenthash")),
            store,
            new RecordingCommandRunner(),
            new StubReadinessProbe(new(false, false, false, null, "ignored")));

        await service.AdvanceActiveJobAsync();

        var latest = await store.GetLatestAsync();
        Assert.Equal("superseded", latest!.Status);
        Assert.Equal("Superseded", latest.Stage);
        Assert.Contains(latest.Logs, log => log.Stage == "Superseded" && log.Message.Contains("currenthash"));
        Assert.Equal("currenthash", latest.RunningGitHash);
    }

    [Fact]
    public async Task AdvanceActiveJobAsync_SupersededOnHashDrift_RecordsAdvancedClockAsCompletedAt()
    {
        var store = new InMemoryUpdateStore();
        var baseline = new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);
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
            "Waiting",
            [new SystemUpdateLogEntry(baseline, "Waiting for reconnect", "Waiting")],
            baseline,
            baseline,
            null));

        var (service, time) = CreateService(
            new SequencedSystemInfo(CreateInfo(runningGitHash: "currenthash", sourceHead: "currenthash")),
            store,
            new RecordingCommandRunner(),
            new StubReadinessProbe(new(false, false, false, null, "ignored")),
            new FakeTimeProvider(baseline));

        var advanced = baseline.AddMinutes(7);
        time.SetUtcNow(advanced);

        await service.AdvanceActiveJobAsync();

        var latest = await store.GetLatestAsync();
        Assert.NotNull(latest);
        Assert.Equal("superseded", latest!.Status);
        Assert.Equal("Superseded", latest.Stage);
        Assert.Equal(advanced, latest.CompletedAt);
        Assert.Equal(advanced, latest.UpdatedAt);
        var log = Assert.Single(latest.Logs, entry => entry.Stage == "Superseded");
        Assert.Equal(advanced, log.At);
    }

    [Fact]
    public async Task GetStatusEnvelopeAsync_ActiveWaitingForReconnectIsPreservedWhenHashMatches()
    {
        var store = new InMemoryUpdateStore();
        var now = FixedNow;
        await store.SaveAsync(new SystemUpdateJobState(
            "job-1",
            "waiting-for-reconnect",
            "Waiting for reconnect",
            true,
            "newhash",
            "newhash",
            "/repo",
            "mohist.service",
            "mohist-runner.service",
            "Waiting",
            [new SystemUpdateLogEntry(now, "Waiting for reconnect", "Waiting")],
            now,
            now,
            null));

        var service = CreateService(
            new SequencedSystemInfo(CreateInfo(runningGitHash: "newhash", sourceHead: "newhash")),
            store,
            new RecordingCommandRunner(),
            new StubReadinessProbe(new(false, false, false, null, "still waiting")));

        var envelope = await service.GetStatusEnvelopeAsync();

        Assert.Equal("waiting-for-reconnect", envelope.Job!.Status);
        var latest = await store.GetLatestAsync();
        Assert.Equal("waiting-for-reconnect", latest!.Status);
    }

    [Fact]
    public async Task AdvanceActiveJobAsync_EmptyRunningHashDoesNotSupersede()
    {
        var store = new InMemoryUpdateStore();
        var now = FixedNow;
        await store.SaveAsync(new SystemUpdateJobState(
            "job-1",
            "waiting-for-reconnect",
            "Waiting for reconnect",
            true,
            null,
            "newhash",
            "/repo",
            "mohist.service",
            "mohist-runner.service",
            "Waiting",
            [new SystemUpdateLogEntry(now, "Waiting for reconnect", "Waiting")],
            now,
            now,
            null));

        var service = CreateService(
            new SequencedSystemInfo(CreateInfo(runningGitHash: null, sourceHead: "newhash")),
            store,
            new RecordingCommandRunner(),
            new StubReadinessProbe(new(false, false, false, null, "waiting")));

        await service.AdvanceActiveJobAsync();

        var latest = await store.GetLatestAsync();
        Assert.Equal("waiting-for-reconnect", latest!.Status);
    }

    [Fact]
    public async Task SupersededStatus_DoesNotBlockNewUpdateStarts()
    {
        var store = new InMemoryUpdateStore();
        var now = FixedNow;
        await store.SaveAsync(new SystemUpdateJobState(
            "job-1",
            "superseded",
            "Superseded",
            true,
            "currenthash",
            "newhash",
            "/repo",
            "mohist.service",
            "mohist-runner.service",
            "Superseded by newer runtime",
            [new SystemUpdateLogEntry(now, "Superseded", "Superseded by newer runtime")],
            now,
            now,
            now));

        var persisted = await store.GetLatestAsync();
        Assert.NotNull(persisted);
        Assert.False(SystemUpdateService.IsActive(persisted!));
        Assert.True(await store.TryAcquireLockAsync("job-2"));
    }

    [Fact]
    public async Task GetLatestStatusAsync_DispatchesNoCommandsForActiveJob()
    {
        var store = new InMemoryUpdateStore();
        var commands = new RecordingCommandRunner();
        var now = FixedNow;
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

        Assert.NotNull(status);
        Assert.Equal("waiting-for-reconnect", status!.Status);
        Assert.Empty(commands.Requests);

        var latest = await store.GetLatestAsync();
        Assert.NotNull(latest);
        Assert.Single(latest!.Logs);
    }

    [Fact]
    public async Task GetLatestStatusAsync_DoesNotReleaseLockAndStartStillRejected()
    {
        var store = new InMemoryUpdateStore();
        var commands = new RecordingCommandRunner();
        var now = FixedNow;
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

        Assert.False(await store.TryAcquireLockAsync("job-2"));

        var service = CreateService(
            new SequencedSystemInfo(CreateInfo(runningGitHash: "newhash", sourceHead: "newhash")),
            store,
            commands,
            new StubReadinessProbe(new(true, true, true, "/assets/app.js", null)));

        var status = await service.GetLatestStatusAsync();

        Assert.NotNull(status);
        Assert.Empty(commands.Requests);
        Assert.False(await store.TryAcquireLockAsync("job-2"));
    }

}
