using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.SystemInfo;
using Mohist.Server.Tests.Support;
using Xunit;
using EnvironmentAbstractions.TestHelpers;

namespace Mohist.Server.Tests.Specs.SystemSpecs;

public class SystemUpdateServiceSpecs
{
    private static readonly TimeSpan AsyncWaitTimeout = TimeSpan.FromSeconds(5);

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
    public async Task AdvanceActiveJobAsync_WhenReady_RestartsRunnerBeforeReadyCompletion()
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
        Assert.True(await store.TryAcquireLockAsync("job-next"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
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
        Assert.True(await store.TryAcquireLockAsync("job-next"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
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
        await store.WaitForStatusAsync("failed");

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
        await store.WaitForStatusAsync("waiting-for-reconnect");

        Assert.True(result.Started);
        Assert.False(await store.TryAcquireLockAsync("job-2"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task GetStatusEnvelopeAsync_DoesNotSucceedUntilReadinessAndHashMatch()
    {
        var store = new InMemoryUpdateStore();
        var now = DateTimeOffset.UtcNow;
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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task GetStatusEnvelopeAsync_PersistsReadinessFailuresAcrossReconnectBoundary()
    {
        var store = new InMemoryUpdateStore();
        var now = DateTimeOffset.UtcNow;
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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task AdvanceActiveJobAsync_DoesNotPersistDuplicateReadinessFailure()
    {
        var store = new InMemoryUpdateStore();
        var now = DateTimeOffset.UtcNow;
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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task AdvanceActiveJobAsync_BoundsPersistedLogEntries()
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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task AdvanceActiveJobAsync_StaleWaitingForReconnectIsSuperseded()
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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task GetStatusEnvelopeAsync_ActiveWaitingForReconnectIsPreservedWhenHashMatches()
    {
        var store = new InMemoryUpdateStore();
        var now = DateTimeOffset.UtcNow;
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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task AdvanceActiveJobAsync_EmptyRunningHashDoesNotSupersede()
    {
        var store = new InMemoryUpdateStore();
        var now = DateTimeOffset.UtcNow;
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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task SupersededStatus_DoesNotBlockNewUpdateStarts()
    {
        var store = new InMemoryUpdateStore();
        var now = DateTimeOffset.UtcNow;
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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task GetLatestStatusAsync_DispatchesNoCommandsForActiveJob()
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

        Assert.NotNull(status);
        Assert.Equal("waiting-for-reconnect", status!.Status);
        Assert.Empty(commands.Requests);

        var latest = await store.GetLatestAsync();
        Assert.NotNull(latest);
        Assert.Single(latest!.Logs);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task GetLatestStatusAsync_DoesNotPersistStateFile()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"mohist-system-update-{Guid.NewGuid():N}.json");
        try
        {
            var store = CreateFileSystemStore(statePath);
            var commands = new RecordingCommandRunner();
            var now = DateTimeOffset.UtcNow;
            var initial = new SystemUpdateJobState(
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
                null);
            await store.SaveAsync(initial);

            var beforeBytes = await File.ReadAllBytesAsync(statePath);

            var service = CreateService(
                new SequencedSystemInfo(CreateInfo(runningGitHash: "newhash", sourceHead: "newhash")),
                store,
                commands,
                new StubReadinessProbe(new(true, true, true, "/assets/app.js", null)));

            var status = await service.GetLatestStatusAsync();

            Assert.NotNull(status);
            Assert.Empty(commands.Requests);

            var afterBytes = await File.ReadAllBytesAsync(statePath);
            Assert.Equal(beforeBytes, afterBytes);
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
    public async Task GetLatestStatusAsync_DoesNotReleaseLockAndStartStillRejected()
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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
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
            Logs: [new SystemUpdateLogEntry(DateTimeOffset.UtcNow, "Verifying workflow runtime", "all checks passed")],
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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
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

        var stageTime = DateTimeOffset.UtcNow;
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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task RecordCliOutcomeAsync_MarksStaleWebJobAsSuperseded()
    {
        var store = new InMemoryUpdateStore();
        var now = DateTimeOffset.UtcNow;
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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task RecordCliOutcomeAsync_AlwaysPersistsWithoutAcquiringLock()
    {
        var store = new InMemoryUpdateStore(acquireLock: true);
        var now = DateTimeOffset.UtcNow;
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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task RecordCliOutcomeAsync_NewOutcomeReplacesPriorTerminalJob()
    {
        var store = new InMemoryUpdateStore();
        var now = DateTimeOffset.UtcNow;
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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task GetConsistencyAsync_AllCoherentReturnsConsistent()
    {
        var store = new InMemoryUpdateStore();
        var skillDataDir = Path.Combine(Path.GetTempPath(), $"mohist-consistency-{Guid.NewGuid():N}");
        Directory.CreateDirectory(skillDataDir);
        Directory.CreateDirectory(Path.Combine(skillDataDir, "mohist"));
        File.WriteAllText(Path.Combine(skillDataDir, "mohist", "SKILL.md"), "---\nname: mohist\ndescription: test.\n---\n\n# mohist\n");
        try
        {
            var service = CreateConsistencyService(
                new SequencedSystemInfo(CreateInfo(runningGitHash: "newhash", sourceHead: "newhash")),
                store,
                new RecordingCommandRunner(),
                new StubReadinessProbe(new(true, true, true, "/assets/app.js", null)),
                skillDataDir);

            var response = await service.GetConsistencyAsync();

            Assert.Equal("consistent", response.Status);
            Assert.All(response.Components, component => Assert.Equal("consistent", component.Status));
        }
        finally
        {
            Directory.Delete(skillDataDir, recursive: true);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task GetConsistencyAsync_RunnerUnavailableIsReported()
    {
        var store = new InMemoryUpdateStore();
        var skillDataDir = Path.Combine(Path.GetTempPath(), $"mohist-consistency-{Guid.NewGuid():N}");
        Directory.CreateDirectory(skillDataDir);
        Directory.CreateDirectory(Path.Combine(skillDataDir, "mohist"));
        File.WriteAllText(Path.Combine(skillDataDir, "mohist", "SKILL.md"), "---\nname: mohist\ndescription: test.\n---\n\n# mohist\n");
        try
        {
            var service = CreateConsistencyService(
                new SequencedSystemInfo(CreateInfo(
                    runningGitHash: "newhash",
                    sourceHead: "newhash",
                    serverServiceStatus: "active",
                    runnerServiceStatus: "inactive")),
                store,
                new RecordingCommandRunner(),
                new StubReadinessProbe(new(true, true, true, "/assets/app.js", null)),
                skillDataDir);

            var response = await service.GetConsistencyAsync();

            Assert.Equal("inconsistent", response.Status);
            var runner = Assert.Single(response.Components, c => c.Name == "runner");
            Assert.Equal("unavailable", runner.Status);
        }
        finally
        {
            Directory.Delete(skillDataDir, recursive: true);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task GetConsistencyAsync_ManagedAssetsMismatchedWhenSkillFilesMissing()
    {
        var store = new InMemoryUpdateStore();
        var missingDir = Path.Combine(Path.GetTempPath(), $"mohist-consistency-{Guid.NewGuid():N}");
        try
        {
            var service = CreateConsistencyService(
                new SequencedSystemInfo(CreateInfo(runningGitHash: "newhash", sourceHead: "newhash")),
                store,
                new RecordingCommandRunner(),
                new StubReadinessProbe(new(true, true, true, "/assets/app.js", null)),
                missingDir);

            var response = await service.GetConsistencyAsync();

            Assert.Equal("inconsistent", response.Status);
            var managed = Assert.Single(response.Components, c => c.Name == "managed-assets");
            Assert.Equal("mismatched", managed.Status);
        }
        finally
        {
            if (Directory.Exists(missingDir))
                Directory.Delete(missingDir, recursive: true);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task GetConsistencyAsync_ManagedAssetsMismatchedWhenSkillDataDirMissing()
    {
        var store = new InMemoryUpdateStore();
        var skillDataDir = Path.Combine(Path.GetTempPath(), $"mohist-consistency-{Guid.NewGuid():N}");
        try
        {
            var service = CreateConsistencyService(
                new SequencedSystemInfo(CreateInfo(runningGitHash: "newhash", sourceHead: "newhash")),
                store,
                new RecordingCommandRunner(),
                new StubReadinessProbe(new(true, true, true, "/assets/app.js", null)),
                skillDataDir);

            var response = await service.GetConsistencyAsync();

            Assert.Equal("inconsistent", response.Status);
            var managed = Assert.Single(response.Components, c => c.Name == "managed-assets");
            Assert.Equal("mismatched", managed.Status);
        }
        finally
        {
            if (Directory.Exists(skillDataDir))
                Directory.Delete(skillDataDir, recursive: true);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public void SourceAudit_FailedStateIsDefinedOnlyInCreateFailedTransition()
    {
        var source = ReadSource();
        var composerStart = source.IndexOf("private (SystemUpdateJobState State, SystemUpdateLogEntry LogEntry) CreateFailedTransition", StringComparison.Ordinal);
        if (composerStart < 0)
        {
            composerStart = source.IndexOf("private static (SystemUpdateJobState State, SystemUpdateLogEntry LogEntry) CreateFailedTransition", StringComparison.Ordinal);
        }
        Assert.True(composerStart >= 0, "CreateFailedTransition method not found");
        var composerEnd = FindMethodEnd(source, composerStart);

        var matches = Regex.Matches(source, @"state\s+with\s*\{[^}]*Status\s*=\s*""failed""", RegexOptions.Singleline);
        Assert.Single(matches);
        Assert.InRange(matches[0].Index, composerStart, composerEnd);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public void SourceAudit_SaveAsyncOnlyInSharedHelpersAndStartAsync()
    {
        var source = ReadSource();
        var persistStart = source.IndexOf("private async Task<SystemUpdateJobState> PersistTransitionAsync", StringComparison.Ordinal);
        var persistEnd = FindMethodEnd(source, persistStart);
        var startAsyncStart = source.IndexOf("public async Task<(bool Started, string? Error, string? Code, SystemUpdateStatusResponse? Status)> StartAsync", StringComparison.Ordinal);
        var startAsyncEnd = FindMethodEnd(source, startAsyncStart);

        var matches = Regex.Matches(source, @"await\s+_store\.SaveAsync\s*\(");
        foreach (Match match in matches)
        {
            var inPersist = match.Index >= persistStart && match.Index <= persistEnd;
            var inStartAsync = match.Index >= startAsyncStart && match.Index <= startAsyncEnd;
            Assert.True(inPersist || inStartAsync,
                $"_store.SaveAsync call at position {match.Index} is not inside PersistTransitionAsync or StartAsync");
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public void SourceAudit_AppendLogInvocationsStayOnSharedHelperPath()
    {
        var source = ReadSource();
        var applyLogStart = source.IndexOf("private SystemUpdateJobState ApplyTransitionLog", StringComparison.Ordinal);
        if (applyLogStart < 0)
        {
            applyLogStart = source.IndexOf("private static SystemUpdateJobState ApplyTransitionLog", StringComparison.Ordinal);
        }
        Assert.True(applyLogStart >= 0, "ApplyTransitionLog method not found");
        var applyLogEnd = FindMethodEnd(source, applyLogStart);
        var recordOutcomeStart = source.IndexOf("public async Task<SystemUpdateStatusResponse> RecordCliOutcomeAsync", StringComparison.Ordinal);
        Assert.True(recordOutcomeStart >= 0, "RecordCliOutcomeAsync method not found");
        var recordOutcomeEnd = FindMethodEnd(source, recordOutcomeStart);

        var matches = Regex.Matches(source, @"(?<!IReadOnlyList<SystemUpdateLogEntry>\s)AppendLog\s*\(");
        Assert.Equal(2, matches.Count);
        foreach (Match match in matches)
        {
            var inApplyLog = match.Index >= applyLogStart && match.Index <= applyLogEnd;
            var inRecordOutcome = match.Index >= recordOutcomeStart && match.Index <= recordOutcomeEnd;
            Assert.True(inApplyLog || inRecordOutcome,
                $"AppendLog invocation at position {match.Index} is not inside ApplyTransitionLog or CLI outcome log ingestion");
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public void SourceAudit_SaveIfCurrentAsyncOnlyInPersistTransitionAsync()
    {
        var source = ReadSource();
        var persistStart = source.IndexOf("private async Task<SystemUpdateJobState> PersistTransitionAsync", StringComparison.Ordinal);
        var persistEnd = FindMethodEnd(source, persistStart);

        var matches = Regex.Matches(source, @"await\s+_store\.SaveIfCurrentAsync\s*\(");
        foreach (Match match in matches)
        {
            Assert.True(match.Index >= persistStart && match.Index <= persistEnd,
                $"_store.SaveIfCurrentAsync call at position {match.Index} is not inside PersistTransitionAsync");
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public void SourceAudit_ReleaseLockAsyncOnlyInSharedHelpersAndRunUpdateFinally()
    {
        var source = ReadSource();
        var persistStart = source.IndexOf("private async Task<SystemUpdateJobState> PersistTransitionAsync", StringComparison.Ordinal);
        var persistEnd = FindMethodEnd(source, persistStart);
        var runUpdateStart = source.IndexOf("private async Task RunUpdateAsync", StringComparison.Ordinal);
        var runUpdateEnd = FindMethodEnd(source, runUpdateStart);

        var matches = Regex.Matches(source, @"await\s+_store\.ReleaseLockAsync\s*\(");
        foreach (Match match in matches)
        {
            var inPersist = match.Index >= persistStart && match.Index <= persistEnd;
            var inRunUpdate = match.Index >= runUpdateStart && match.Index <= runUpdateEnd;
            Assert.True(inPersist || inRunUpdate,
                $"_store.ReleaseLockAsync call at position {match.Index} is not inside PersistTransitionAsync or RunUpdateAsync");
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public void SourceAudit_LogCapDefinedOnce()
    {
        var source = ReadSource();
        Assert.Contains("private const int MaxLogEntries = 200;", source);
        var capMatches = Regex.Matches(source, @"\b200\b");
        Assert.Single(capMatches);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public void SourceAudit_IsUpdateEnabledUsesExplicitControlFlow()
    {
        var source = ReadSource();
        var methodStart = source.IndexOf("private bool IsUpdateEnabled()", StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "IsUpdateEnabled method not found");
        var methodEnd = FindMethodEnd(source, methodStart);
        var body = source.Substring(methodStart, methodEnd - methodStart);

        var singleLinePattern = new Regex(
            @"return\s+string\.IsNullOrWhiteSpace\s*\([^)]*\)\s*\|\|\s*bool\.TryParse\s*\([^)]*\)\s*&&\s*",
            RegexOptions.Singleline);
        Assert.Empty(singleLinePattern.Matches(body));

        Assert.Contains("if (!string.IsNullOrWhiteSpace(", body);
        Assert.Contains("bool.TryParse(", body);
        Assert.Matches(new Regex(@"return\s+true\s*;"), body);
    }

    private static string SourcePath => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "src", "Mohist.Server", "SystemInfo", "SystemUpdateService.cs"));

    private static string ReadSource() => File.ReadAllText(SourcePath);

    private static int FindMethodEnd(string source, int methodStart)
    {
        var match = Regex.Match(source.Substring(methodStart), @"\n    (?:private|public|internal) ");
        return match.Success ? methodStart + match.Index : source.Length;
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
        string? enabled)
    {
        return CreateService(new SequencedSystemInfo(systemInfo), store, commandRunner, readinessProbe, enabled);
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
        return CreateService(systemInfo, store, commandRunner, readinessProbe, enabled: "true");
    }

    private static SystemUpdateService CreateService(
        SequencedSystemInfo systemInfo,
        ISystemUpdateStore store,
        ISystemUpdateCommandRunner commandRunner,
        ISystemReadinessProbe readinessProbe,
        string? enabled)
    {
        return CreateService(
            systemInfo,
            store,
            commandRunner,
            readinessProbe,
            new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero)),
            enabled).Service;
    }

    private static (SystemUpdateService Service, FakeTimeProvider Time) CreateService(
        SequencedSystemInfo systemInfo,
        ISystemUpdateStore store,
        ISystemUpdateCommandRunner commandRunner,
        ISystemReadinessProbe readinessProbe,
        FakeTimeProvider time,
        string? enabled = "true")
    {
        var settings = new Dictionary<string, string?>
        {
            ["Mohist:SystemUpdate:Enabled"] = enabled
        };
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
            new RunningInfo("1.2.3", runningGitHash, DateTimeOffset.UtcNow),
            new SourceInfo(sourcePath, "main", sourceHead, sourceDirty),
            new InstallInfo(installMode, "systemd-user", "mohist.service", "mohist-runner.service", installMode),
            new UpdateInfo(updateStatus, available, updateStatus),
            new ServiceInfo(serverServiceStatus, runnerServiceStatus),
            new SystemPaths("/db", "/config", "/logs", "/opencode"));
    }

    private static SystemUpdateService CreateConsistencyService(
        SequencedSystemInfo systemInfo,
        ISystemUpdateStore store,
        ISystemUpdateCommandRunner commandRunner,
        ISystemReadinessProbe readinessProbe,
        string managedAssetsPath)
    {
        return CreateConsistencyService(systemInfo, store, commandRunner, readinessProbe, managedAssetsPath, new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero))).Service;
    }

    private static (SystemUpdateService Service, FakeTimeProvider Time) CreateConsistencyService(
        SequencedSystemInfo systemInfo,
        ISystemUpdateStore store,
        ISystemUpdateCommandRunner commandRunner,
        ISystemReadinessProbe readinessProbe,
        string managedAssetsPath,
        FakeTimeProvider time)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mohist:SystemUpdate:Enabled"] = "true",
            ["Mohist:CliSkillDataPath"] = managedAssetsPath
        }).Build();

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
