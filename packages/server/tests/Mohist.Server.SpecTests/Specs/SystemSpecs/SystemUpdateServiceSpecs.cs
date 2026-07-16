using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.SystemInfo;
using Mohist.Server.SpecTests.Support;
using Xunit;
using EnvironmentAbstractions.TestHelpers;

namespace Mohist.Server.SpecTests.Specs.SystemSpecs;

public class SystemUpdateServiceSpecs
{
    private static readonly TimeSpan AsyncWaitTimeout = TimeSpan.FromSeconds(5);
    private static readonly DateTimeOffset FixedNow = new(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
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
        await store.WaitForStatusAsync("waiting-for-reconnect");

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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task StartAsync_DisabledByConfig_ReturnsUpdateDisabledWithoutSideEffects()
    {
        var store = new InMemoryUpdateStore();
        var commands = new RecordingCommandRunner();
        var service = CreateService(
            systemInfo: CreateInfo(),
            store: store,
            commandRunner: commands,
            readinessProbe: new StubReadinessProbe(new(true, true, true, "/assets/app.js", null)),
            enabled: "false");

        var result = await service.StartAsync(new SystemUpdateRequest(), CancellationToken.None);

        Assert.False(result.Started);
        Assert.Null(result.Status);
        Assert.Equal("update_disabled", result.Code);
        Assert.Equal("System update is disabled by configuration", result.Error);
        Assert.Empty(commands.Requests);
        Assert.Empty(store.SavedStates);
        Assert.Equal(0, store.AcquireAttempts);
        Assert.True(await store.TryAcquireLockAsync("job-next"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Theory]
    [InlineData("dirty-source", true, true)]
    [InlineData("up-to-date", false, false)]
    public async Task StartAsync_DisabledByConfig_TakesPrecedenceOverDirtySourceAndNoUpdateAvailable(
        string updateStatus,
        bool available,
        bool sourceDirty)
    {
        var store = new InMemoryUpdateStore();
        var commands = new RecordingCommandRunner();
        var service = CreateService(
            systemInfo: CreateInfo(updateStatus: updateStatus, available: available, sourceDirty: sourceDirty),
            store: store,
            commandRunner: commands,
            readinessProbe: new StubReadinessProbe(new(true, true, true, "/assets/app.js", null)),
            enabled: "false");

        var result = await service.StartAsync(new SystemUpdateRequest(), CancellationToken.None);

        Assert.False(result.Started);
        Assert.Null(result.Status);
        Assert.Equal("update_disabled", result.Code);
        Assert.Equal("System update is disabled by configuration", result.Error);
        Assert.Empty(commands.Requests);
        Assert.Empty(store.SavedStates);
        Assert.Equal(0, store.AcquireAttempts);
        Assert.True(await store.TryAcquireLockAsync("job-next"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task StartAsync_ExplicitTrueEnablesGate_ProceedsToOtherValidations()
    {
        var commands = new RecordingCommandRunner();
        var store = new InMemoryUpdateStore(acquireLock: false);
        var service = CreateService(
            systemInfo: CreateInfo(updateStatus: "update_in_progress_lock_held_by_another", available: true),
            store: store,
            commandRunner: commands,
            readinessProbe: new StubReadinessProbe(new(true, true, true, "/assets/app.js", null)),
            enabled: "true");

        var result = await service.StartAsync(new SystemUpdateRequest(), CancellationToken.None);

        Assert.False(result.Started);
        Assert.NotEqual("update_disabled", result.Code);
        Assert.Empty(commands.Requests);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Theory]
    [InlineData(null, false)]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    public async Task StartAsync_UnconfiguredEnabled_DefaultsToEnabledAndStarts(
        string? enabled,
        bool includeEnabled)
    {
        var store = new InMemoryUpdateStore();
        var commands = new RecordingCommandRunner();
        var service = CreateService(
            systemInfo: CreateInfo(),
            store: store,
            commandRunner: commands,
            readinessProbe: new StubReadinessProbe(new(false, false, false, null, "API health endpoint is not ready")),
            enabled: enabled,
            includeEnabled: includeEnabled);

        var result = await service.StartAsync(new SystemUpdateRequest(), CancellationToken.None);
        await commands.WaitForCountAsync(2);
        await store.WaitForStatusAsync("waiting-for-reconnect");

        Assert.True(result.Started);
        Assert.Null(result.Error);
        Assert.Null(result.Code);
        Assert.NotNull(result.Status);
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
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task StartAsync_WhenPersistedActiveJobExistsAfterRestart_ReturnsConflict()
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task InMemoryUpdateStore_ReleaseStaleLockAsync_ReleasesHeldLockWithoutProcessLocalMatch()
    {
        var store = new InMemoryUpdateStore();
        Assert.True(await store.TryAcquireLockAsync("stale-job"));

        await store.ReleaseStaleLockAsync("stale-job");

        Assert.True(await store.TryAcquireLockAsync("new-job"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task InMemoryUpdateStore_ReleaseStaleLockAsync_IsIdempotentWhenLockNotHeld()
    {
        var store = new InMemoryUpdateStore();

        await store.ReleaseStaleLockAsync("some-job");

        Assert.True(await store.TryAcquireLockAsync("new-job"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
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
        await store.WaitForStatusAsync("failed");

        var latest = await store.GetLatestAsync();
        Assert.True(result.Started);
        Assert.Empty(commands.Requests);
        Assert.Equal("failed", latest!.Status);
        Assert.Equal("Trusted install facts changed before update execution", latest.Reason);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
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
        await store.WaitForStatusAsync("waiting-for-reconnect");

        Assert.True(result.Started);
        Assert.False(await store.TryAcquireLockAsync("job-2"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task RecordCliOutcomeAsync_PersistsOutcomeViaStore()
    {
        var store = new InMemoryUpdateStore();
        var service = CreateService(
            new SequencedSystemInfo(CreateInfo(runningGitHash: "newhash", sourceHead: "newhash")),
            store,
            new RecordingCommandRunner(),
            new StubReadinessProbe(new(true, true, true, "/assets/app.js", null)));

        var response = await service.RecordCliOutcomeAsync(new SystemUpdateOutcomeRequest(
            JobId: "cli-job-1",
            Status: "succeeded",
            Stage: "Verifying workflow runtime",
            Outcome: "succeeded",
            UnavailableCapability: null,
            Logs: [new SystemUpdateLogEntry(FixedNow, "Verifying workflow runtime", "all checks passed")],
            SourceHead: "newhash",
            SourcePath: "/repo",
            ServerUnit: "mohist.service",
            RunnerUnit: "mohist-runner.service"));

        Assert.Equal("succeeded", response.Status);
        Assert.Equal("succeeded", response.Outcome);
        Assert.Null(response.UnavailableCapability);
        Assert.Equal("cli-job-1", response.JobId);
        Assert.Equal("newhash", response.SourceHead);

        var latest = await store.GetLatestAsync();
        Assert.NotNull(latest);
        Assert.Equal("cli-job-1", latest!.JobId);
        Assert.Equal("succeeded", latest.Status);
        Assert.Equal("succeeded", latest.Outcome);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task RecordCliOutcomeAsync_AppendsRequestLogsToPersistedJobLog()
    {
        var store = new InMemoryUpdateStore();
        var service = CreateService(
            new SequencedSystemInfo(CreateInfo(runningGitHash: "newhash", sourceHead: "newhash")),
            store,
            new RecordingCommandRunner(),
            new StubReadinessProbe(new(true, true, true, "/assets/app.js", null)));

        var stageTime = FixedNow;
        var request = new SystemUpdateOutcomeRequest(
            JobId: "cli-job-logs",
            Status: "succeeded",
            Stage: "Verifying workflow runtime",
            Outcome: "succeeded",
            UnavailableCapability: null,
            Logs:
            [
                new SystemUpdateLogEntry(stageTime, "Updating CLI", "starting"),
                new SystemUpdateLogEntry(stageTime, "Preparing workflow runner", "runner stopped"),
                new SystemUpdateLogEntry(stageTime, "Verifying workflow runtime", "all checks passed"),
            ],
            SourceHead: "newhash",
            SourcePath: "/repo",
            ServerUnit: "mohist.service",
            RunnerUnit: "mohist-runner.service");

        await service.RecordCliOutcomeAsync(request);

        var latest = await store.GetLatestAsync();
        Assert.NotNull(latest);
        Assert.Contains(latest!.Logs, entry => entry.Stage == "Updating CLI" && entry.Message == "starting");
        Assert.Contains(latest.Logs, entry => entry.Stage == "Preparing workflow runner" && entry.Message == "runner stopped");
        Assert.Contains(latest.Logs, entry => entry.Stage == "Verifying workflow runtime" && entry.Message == "all checks passed");
        Assert.Contains(latest.Logs, entry => entry.Message.StartsWith("CLI reported outcome"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task RecordCliOutcomeAsync_MarksStaleWebJobAsSuperseded()
    {
        var store = new InMemoryUpdateStore();
        var now = FixedNow;
        await store.SaveAsync(new SystemUpdateJobState(
            "web-job-1",
            "waiting-for-reconnect",
            "Waiting for reconnect",
            true,
            "oldhash",
            "oldsource",
            "/repo",
            "mohist.service",
            "mohist-runner.service",
            "Waiting for old restart",
            [new SystemUpdateLogEntry(now, "Waiting for reconnect", "Waiting for old restart")],
            now,
            now,
            null));

        var service = CreateService(
            new SequencedSystemInfo(CreateInfo(runningGitHash: "newhash", sourceHead: "newhash")),
            store,
            new RecordingCommandRunner(),
            new StubReadinessProbe(new(true, true, true, "/assets/app.js", null)));

        var response = await service.RecordCliOutcomeAsync(new SystemUpdateOutcomeRequest(
            JobId: "cli-job-1",
            Status: "succeeded",
            Stage: "Verifying workflow runtime",
            Outcome: "succeeded",
            SourceHead: "newhash"));

        Assert.Equal("cli-job-1", response.JobId);
        Assert.Equal("succeeded", response.Status);

        var latest = await store.GetLatestAsync();
        Assert.Equal("cli-job-1", latest!.JobId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task RecordCliOutcomeAsync_AlwaysPersistsWithoutAcquiringLock()
    {
        var store = new InMemoryUpdateStore(acquireLock: true);
        var now = FixedNow;
        await store.SaveAsync(new SystemUpdateJobState(
            "web-job-active",
            "running",
            "Building",
            true,
            "oldhash",
            "newhash",
            "/repo",
            "mohist.service",
            "mohist-runner.service",
            null,
            [new SystemUpdateLogEntry(now, "Building", "Building")],
            now,
            now,
            null));

        var service = CreateService(
            new SequencedSystemInfo(CreateInfo(runningGitHash: "newhash", sourceHead: "newhash")),
            store,
            new RecordingCommandRunner(),
            new StubReadinessProbe(new(true, true, true, "/assets/app.js", null)));

        var response = await service.RecordCliOutcomeAsync(new SystemUpdateOutcomeRequest(
            JobId: "cli-job-1",
            Status: "succeeded",
            Stage: "Ready",
            Outcome: "succeeded",
            SourceHead: "newhash"));

        Assert.Equal("cli-job-1", response.JobId);
        Assert.Equal("succeeded", response.Status);

        var latest = await store.GetLatestAsync();
        Assert.Equal("cli-job-1", latest!.JobId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task RecordCliOutcomeAsync_NewOutcomeReplacesPriorTerminalJob()
    {
        var store = new InMemoryUpdateStore();
        var now = FixedNow;
        await store.SaveAsync(new SystemUpdateJobState(
            "other-job-terminal",
            "succeeded",
            "Ready",
            true,
            "oldhash",
            "newhash",
            "/repo",
            "mohist.service",
            "mohist-runner.service",
            null,
            [new SystemUpdateLogEntry(now, "Ready", "ready")],
            now,
            now,
            now,
            "succeeded",
            null));

        var service = CreateService(
            new SequencedSystemInfo(CreateInfo(runningGitHash: "newhash", sourceHead: "newhash")),
            store,
            new RecordingCommandRunner(),
            new StubReadinessProbe(new(true, true, true, "/assets/app.js", null)));

        var response = await service.RecordCliOutcomeAsync(new SystemUpdateOutcomeRequest(
            JobId: "cli-job-1",
            Status: "succeeded",
            Stage: "Verifying workflow runtime",
            Outcome: "succeeded",
            SourceHead: "newhash"));

        Assert.Equal("cli-job-1", response.JobId);
        Assert.Equal("succeeded", response.Status);

        var latest = await store.GetLatestAsync();
        Assert.Equal("cli-job-1", latest!.JobId);
        Assert.Equal("succeeded", latest.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task RecordCliOutcomeAsync_RejectsUnknownStatus()
    {
        var store = new InMemoryUpdateStore();
        var service = CreateService(
            new SequencedSystemInfo(CreateInfo(runningGitHash: "newhash", sourceHead: "newhash")),
            store,
            new RecordingCommandRunner(),
            new StubReadinessProbe(new(true, true, true, "/assets/app.js", null)));

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.RecordCliOutcomeAsync(new SystemUpdateOutcomeRequest(
            JobId: "cli-job-bogus",
            Status: "bogus",
            Stage: "Ready",
            Outcome: "succeeded",
            SourceHead: "newhash")));

        Assert.Contains("bogus", ex.Message);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task GetConsistencyAsync_AllCoherentReturnsConsistent()
    {
        var service = CreateService(
            new SequencedSystemInfo(CreateInfo(runningGitHash: "newhash", sourceHead: "newhash")),
            new InMemoryUpdateStore(),
            new RecordingCommandRunner(),
            new StubReadinessProbe(new(true, true, true, "/assets/app.js", null)),
            managedAssets: new InMemoryManagedAssetCatalog());

        var response = await service.GetConsistencyAsync();

        Assert.Equal("consistent", response.Status);
        Assert.All(response.Components, component => Assert.Equal("consistent", component.Status));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task GetConsistencyAsync_RunnerUnavailableIsReported()
    {
        var service = CreateService(
            new SequencedSystemInfo(CreateInfo(
                runningGitHash: "newhash",
                sourceHead: "newhash",
                serverServiceStatus: "active",
                runnerServiceStatus: "inactive")),
            new InMemoryUpdateStore(),
            new RecordingCommandRunner(),
            new StubReadinessProbe(new(true, true, true, "/assets/app.js", null)),
            managedAssets: new InMemoryManagedAssetCatalog());

        var response = await service.GetConsistencyAsync();

        Assert.Equal("inconsistent", response.Status);
        var runner = Assert.Single(response.Components, c => c.Name == "runner");
        Assert.Equal("unavailable", runner.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task RunUpdateAsync_OnBuildFailure_RestoresRunnerAndMarksRecovered()
    {
        var store = new InMemoryUpdateStore();
        var commands = new ScriptedCommandRunner(
            (0, "dotnet", new SystemCommandResult(1, "build failed")),
            (1, "systemctl", new SystemCommandResult(0, "runner restart ok")));
        var service = CreateService(
            systemInfo: CreateInfo(),
            store: store,
            commandRunner: commands,
            readinessProbe: new StubReadinessProbe(new(false, false, false, null, "ignored")));

        var result = await service.StartAsync(new SystemUpdateRequest(), CancellationToken.None);
        await commands.WaitForCountAsync(2);
        await store.WaitForStatusAsync("recovered");

        var latest = await store.GetLatestAsync();
        Assert.True(result.Started);
        Assert.Equal("recovered", latest!.Status);
        Assert.Equal("Recovered", latest.Stage);
        Assert.Equal("recovered", latest.Outcome);
        Assert.Null(latest.UnavailableCapability);
        Assert.Contains(latest.Logs, log => log.Stage == "Restoring runner");
        Assert.Contains(latest.Logs, log => log.Stage == "Recovered" && log.Message.Contains("Runner restore succeeded"));

        Assert.Collection(commands.Requests,
            command =>
            {
                Assert.Equal("dotnet", command.FileName);
                Assert.Equal(["build", "Mohist.sln"], command.Arguments);
            },
            command =>
            {
                Assert.Equal("systemctl", command.FileName);
                Assert.Equal(["--user", "restart", "mohist-runner.service"], command.Arguments);
            });

        // The terminal status was saved on the background Task.Run thread;
        // the lock is released only afterwards in RunUpdateAsync's `finally`.
        // Waiting on the status alone races with that release, so wait for the
        // explicit unlock signal before asserting the lock is free.
        await store.WaitForUnlockAsync();
        Assert.True(await store.TryAcquireLockAsync("job-next"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task RunUpdateAsync_OnBuildFailure_RunnerRestoreFails_MarksFailedWithUnavailableCapability()
    {
        var store = new InMemoryUpdateStore();
        var commands = new ScriptedCommandRunner(
            (0, "dotnet", new SystemCommandResult(1, "build failed")),
            (1, "systemctl", new SystemCommandResult(1, "runner restart failed")));
        var service = CreateService(
            systemInfo: CreateInfo(),
            store: store,
            commandRunner: commands,
            readinessProbe: new StubReadinessProbe(new(false, false, false, null, "ignored")));

        var result = await service.StartAsync(new SystemUpdateRequest(), CancellationToken.None);
        await commands.WaitForCountAsync(2);
        await store.WaitForStatusAndStageAsync("failed", "Failed");

        var latest = await store.GetLatestAsync();
        Assert.True(result.Started);
        Assert.Equal("failed", latest!.Status);
        Assert.Equal("Failed", latest.Stage);
        Assert.Equal("failed", latest.Outcome);
        Assert.Equal("Runner", latest.UnavailableCapability);
        Assert.Contains(latest.Logs, log => log.Stage == "Failed" && log.Message.Contains("mo server start --runner"));

        Assert.Collection(commands.Requests,
            command => Assert.Equal("dotnet", command.FileName),
            command =>
            {
                Assert.Equal("systemctl", command.FileName);
                Assert.Equal(["--user", "restart", "mohist-runner.service"], command.Arguments);
            });

        // The terminal status was saved on the background Task.Run thread;
        // the lock is released only afterwards in RunUpdateAsync's `finally`.
        // Waiting on the status alone races with that release, so wait for the
        // explicit unlock signal before asserting the lock is free.
        await store.WaitForUnlockAsync();
        Assert.True(await store.TryAcquireLockAsync("job-next"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task RunUpdateAsync_OnBuildException_RestoresRunnerWithoutPersistingFailedBeforeRecovery()
    {
        var store = new InMemoryUpdateStore();
        var commands = new ThrowingCommandRunner(
            ("dotnet", () => throw new InvalidOperationException("build threw")),
            ("systemctl", () => new SystemCommandResult(0, "runner restart ok")));
        var service = CreateService(
            systemInfo: CreateInfo(),
            store: store,
            commandRunner: commands,
            readinessProbe: new StubReadinessProbe(new(false, false, false, null, "ignored")));

        var result = await service.StartAsync(new SystemUpdateRequest(), CancellationToken.None);
        await commands.WaitForCountAsync(2);
        await store.WaitForStatusAsync("recovered");

        var latest = await store.GetLatestAsync();
        Assert.True(result.Started);
        Assert.Equal("recovered", latest!.Status);
        Assert.Equal("Recovered", latest.Stage);
        Assert.Equal("recovered", latest.Outcome);
        Assert.Null(latest.UnavailableCapability);
        Assert.Contains(latest.Logs, log => log.Stage == "Building" && log.Message == "build threw");
        Assert.Contains(latest.Logs, log => log.Stage == "Restoring runner");
        Assert.Contains(latest.Logs, log => log.Stage == "Recovered" && log.Message.Contains("Runner restore succeeded"));

        var restoringIndex = store.SavedStates.FindIndex(state => state.Stage == "Restoring runner");
        Assert.True(restoringIndex >= 0);
        Assert.DoesNotContain(store.SavedStates.Take(restoringIndex), state => state.Status == "failed");
        Assert.DoesNotContain(store.SavedStates, state => state.Status == "failed");

        Assert.Collection(commands.Requests,
            command => Assert.Equal("dotnet", command.FileName),
            command =>
            {
                Assert.Equal("systemctl", command.FileName);
                Assert.Equal(["--user", "restart", "mohist-runner.service"], command.Arguments);
            });

        await store.WaitForUnlockAsync();
        Assert.True(await store.TryAcquireLockAsync("job-next"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task RunUpdateAsync_OnBuildException_RunnerRestoreFails_MarksFailedAfterRestoreAttempt()
    {
        var store = new InMemoryUpdateStore();
        var commands = new ThrowingCommandRunner(
            ("dotnet", () => throw new InvalidOperationException("build threw")),
            ("systemctl", () => new SystemCommandResult(1, "runner restart failed")));
        var service = CreateService(
            systemInfo: CreateInfo(),
            store: store,
            commandRunner: commands,
            readinessProbe: new StubReadinessProbe(new(false, false, false, null, "ignored")));

        var result = await service.StartAsync(new SystemUpdateRequest(), CancellationToken.None);
        await commands.WaitForCountAsync(2);
        await WaitUntilAsync(async () =>
        {
            var current = await store.GetLatestAsync();
            return current?.Status == "failed" && current.Stage == "Failed";
        });

        var latest = await store.GetLatestAsync();
        Assert.True(result.Started);
        Assert.Equal("failed", latest!.Status);
        Assert.Equal("Failed", latest.Stage);
        Assert.Equal("failed", latest.Outcome);
        Assert.Equal("Runner", latest.UnavailableCapability);
        Assert.Contains(latest.Logs, log => log.Stage == "Building" && log.Message == "build threw");
        Assert.Contains(latest.Logs, log => log.Stage == "Failed" && log.Message.Contains("mo server start --runner"));

        var restoringIndex = store.SavedStates.FindIndex(state => state.Stage == "Restoring runner");
        var finalFailedIndex = store.SavedStates.FindLastIndex(state => state.Status == "failed" && state.Stage == "Failed");
        Assert.True(restoringIndex >= 0);
        Assert.True(finalFailedIndex > restoringIndex);
        Assert.DoesNotContain(store.SavedStates.Take(restoringIndex), state => state.Status == "failed");

        await store.WaitForUnlockAsync();
        Assert.True(await store.TryAcquireLockAsync("job-next"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task RunUpdateAsync_OnServerRestartFailure_RestoresRunnerAndMarksRecovered()
    {
        var store = new InMemoryUpdateStore();
        var commands = new ScriptedCommandRunner(
            (0, "dotnet", new SystemCommandResult(0, "build ok")),
            (1, "systemctl", new SystemCommandResult(1, "server restart failed")),
            (2, "systemctl", new SystemCommandResult(0, "runner restart ok")));
        var service = CreateService(
            systemInfo: CreateInfo(),
            store: store,
            commandRunner: commands,
            readinessProbe: new StubReadinessProbe(new(false, false, false, null, "ignored")));

        var result = await service.StartAsync(new SystemUpdateRequest(), CancellationToken.None);
        await commands.WaitForCountAsync(3);
        await store.WaitForStatusAsync("recovered");

        var latest = await store.GetLatestAsync();
        Assert.True(result.Started);
        Assert.Equal("recovered", latest!.Status);
        Assert.Equal("Recovered", latest.Stage);
        Assert.Equal("recovered", latest.Outcome);
        Assert.Null(latest.UnavailableCapability);
        Assert.Contains(latest.Logs, log => log.Stage == "Restoring runner");
        Assert.Contains(latest.Logs, log => log.Stage == "Recovered" && log.Message.Contains("Runner restore succeeded"));

        Assert.Collection(commands.Requests,
            command => Assert.Equal("dotnet", command.FileName),
            command =>
            {
                Assert.Equal("systemctl", command.FileName);
                Assert.Equal(["--user", "restart", "mohist.service"], command.Arguments);
            },
            command =>
            {
                Assert.Equal("systemctl", command.FileName);
                Assert.Equal(["--user", "restart", "mohist-runner.service"], command.Arguments);
            });

        // The terminal status was saved on the background Task.Run thread;
        // the lock is released only afterwards in RunUpdateAsync's `finally`.
        // Waiting on the status alone races with that release, so wait for the
        // explicit unlock signal before asserting the lock is free.
        await store.WaitForUnlockAsync();
        Assert.True(await store.TryAcquireLockAsync("job-next"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Theory]
    [InlineData(ManagedAssetCatalogState.Empty)]
    [InlineData(ManagedAssetCatalogState.Unavailable)]
    public async Task GetConsistencyAsync_ManagedAssetsMismatchedWhenCatalogIsNotAvailable(
        ManagedAssetCatalogState state)
    {
        var service = CreateService(
            new SequencedSystemInfo(CreateInfo(runningGitHash: "newhash", sourceHead: "newhash")),
            new InMemoryUpdateStore(),
            new RecordingCommandRunner(),
            new StubReadinessProbe(new(true, true, true, "/assets/app.js", null)),
            managedAssets: new InMemoryManagedAssetCatalog(state));

        var response = await service.GetConsistencyAsync();

        Assert.Equal("inconsistent", response.Status);
        var managed = Assert.Single(response.Components, c => c.Name == "managed-assets");
        Assert.Equal("mismatched", managed.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task PersistTransitionAsync_ReleasesLockOnlyAfterSave()
    {
        var store = new OrderTrackingStore();
        var service = CreateService(
            new SequencedSystemInfo(CreateInfo(runningGitHash: "newhash", sourceHead: "newhash")),
            store,
            new RecordingCommandRunner(),
            new StubReadinessProbe(new(true, true, true, "/assets/app.js", null)));

        await service.RecordCliOutcomeAsync(new SystemUpdateOutcomeRequest(
            JobId: "cli-job-1",
            Status: "succeeded",
            Stage: "Ready",
            Outcome: "succeeded",
            SourceHead: "newhash"));

        var saveIndex = store.Events.IndexOf("Save");
        var releaseIndex = store.Events.IndexOf("ReleaseLock");
        Assert.True(saveIndex >= 0);
        Assert.True(releaseIndex >= 0);
        Assert.True(saveIndex < releaseIndex, "ReleaseLockAsync must run strictly after SaveAsync");
    }

    private static SystemUpdateService CreateService(
        SystemInfoResponse systemInfo,
        ISystemUpdateStore store,
        ISystemUpdateCommandRunner commandRunner,
        ISystemReadinessProbe readinessProbe)
    {
        return CreateService(new SequencedSystemInfo(systemInfo), store, commandRunner, readinessProbe);
    }

    private static SystemUpdateService CreateService(
        SystemInfoResponse systemInfo,
        ISystemUpdateStore store,
        ISystemUpdateCommandRunner commandRunner,
        ISystemReadinessProbe readinessProbe,
        string? enabled,
        bool includeEnabled = true)
    {
        return CreateService(new SequencedSystemInfo(systemInfo), store, commandRunner, readinessProbe, enabled, includeEnabled);
    }

    private static SystemUpdateService CreateService(
        SequencedSystemInfo systemInfo,
        ISystemUpdateStore store,
        ISystemUpdateCommandRunner commandRunner,
        ISystemReadinessProbe readinessProbe)
    {
        return CreateService(systemInfo, store, commandRunner, readinessProbe, enabled: "true");
    }

    private static SystemUpdateService CreateService(
        SequencedSystemInfo systemInfo,
        ISystemUpdateStore store,
        ISystemUpdateCommandRunner commandRunner,
        ISystemReadinessProbe readinessProbe,
        IManagedAssetCatalog managedAssets)
    {
        return CreateService(
            systemInfo,
            store,
            commandRunner,
            readinessProbe,
            new FakeTimeProvider(FixedNow),
            managedAssets: managedAssets).Service;
    }

    private static SystemUpdateService CreateService(
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

    private static (SystemUpdateService Service, FakeTimeProvider Time) CreateService(
        SequencedSystemInfo systemInfo,
        ISystemUpdateStore store,
        ISystemUpdateCommandRunner commandRunner,
        ISystemReadinessProbe readinessProbe,
        FakeTimeProvider time,
        string? enabled = "true",
        bool includeEnabled = true,
        IManagedAssetCatalog? managedAssets = null)
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
            managedAssets ?? new InMemoryManagedAssetCatalog(),
            NullLogger<SystemUpdateService>.Instance,
            time);
        return (service, time);
    }

    private static SystemInfoResponse CreateInfo(
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
            new RunningInfo("1.2.3", runningGitHash, FixedNow),
            new SourceInfo(sourcePath, "main", sourceHead, sourceDirty),
            new InstallInfo(installMode, "systemd-user", "mohist.service", "mohist-runner.service", installMode),
            new UpdateInfo(updateStatus, available, updateStatus),
            new ServiceInfo(serverServiceStatus, runnerServiceStatus),
            new SystemPaths("/db", "/config", "/logs", "/opencode"));
    }

    private sealed class OrderTrackingStore : ISystemUpdateStore
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

    private sealed class InMemoryUpdateStore : ISystemUpdateStore
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

    private sealed class RecordingCommandRunner : ISystemUpdateCommandRunner
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

    private sealed class ScriptedCommandRunner : ISystemUpdateCommandRunner
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

    private sealed class ThrowingCommandRunner : ISystemUpdateCommandRunner
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

    private sealed class CountWaiter
    {
        private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CountWaiter(int count) => Count = count;

        public int Count { get; }

        public Task Task => _tcs.Task;

        public void Complete() => _tcs.TrySetResult();
    }

    private sealed class StatusWaiter
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
        await TestWait.ForAsync(
            condition,
            value => value,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(25),
            "system update condition");
    }
}
