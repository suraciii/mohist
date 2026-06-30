using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.SystemInfo;
using Mohist.Server.Tests.Support;
using Xunit;
using EnvironmentAbstractions.TestHelpers;

namespace Mohist.Server.Tests.Specs.SystemSpecs;

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

        var first = await service.GetLatestStatusAsync();
        var second = await service.GetLatestStatusAsync();
        var third = await service.GetLatestStatusAsync();

        Assert.Equal("waiting-for-reconnect", first!.Status);
        Assert.Equal("waiting-for-reconnect", second!.Status);
        Assert.Equal("succeeded", third!.Status);
        Assert.Equal("Ready", third.Stage);
        Assert.Contains(third.Logs, log => log.Stage == "Ready" && log.Message.Contains("asset /assets/app.js is ready"));
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

        await service.GetLatestStatusAsync();

        var latest = await store.GetLatestAsync();
        Assert.Equal(200, latest!.Logs.Count);
        Assert.DoesNotContain(latest.Logs, log => log.Message == "entry-0");
        Assert.Contains(latest.Logs, log => log.Message == "Still waiting");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task GetLatestStatusAsync_StaleWaitingForReconnectIsSuperseded()
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

        var status = await service.GetLatestStatusAsync();

        Assert.Equal("superseded", status!.Status);
        Assert.Equal("Superseded", status.Stage);
        Assert.Contains(status.Logs, log => log.Stage == "Superseded" && log.Message.Contains("currenthash"));

        var latest = await store.GetLatestAsync();
        Assert.Equal("superseded", latest!.Status);
        Assert.Equal("currenthash", latest.RunningGitHash);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task GetLatestStatusAsync_ActiveWaitingForReconnectIsPreservedWhenHashMatches()
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

        var status = await service.GetLatestStatusAsync();

        Assert.Equal("waiting-for-reconnect", status!.Status);
        var latest = await store.GetLatestAsync();
        Assert.Equal("waiting-for-reconnect", latest!.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task GetLatestStatusAsync_EmptyRunningHashDoesNotSupersede()
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

        var status = await service.GetLatestStatusAsync();

        Assert.Equal("waiting-for-reconnect", status!.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task GetLatestStatusAsync_SupersededStatusDoesNotBlockNewUpdateStarts()
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
        await WaitUntilAsync(async () => (await store.GetLatestAsync())?.Status == "recovered");

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
        await WaitUntilAsync(async () => (await store.GetLatestAsync())?.Status == "failed");

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
        await WaitUntilAsync(async () => (await store.GetLatestAsync())?.Status == "recovered");

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
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mohist:SystemUpdate:Enabled"] = "true",
            ["Mohist:CliSkillDataPath"] = managedAssetsPath
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

    private sealed class InMemoryUpdateStore : ISystemUpdateStore
    {
        private readonly List<StatusWaiter> _statusWaiters = [];
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
            CompleteStatusWaiters();
            return Task.CompletedTask;
        }

        public Task<bool> SaveIfCurrentAsync(SystemUpdateJobState expected, SystemUpdateJobState next, CancellationToken cancellationToken = default)
        {
            if (_latest is null
                || !string.Equals(_latest.JobId, expected.JobId, StringComparison.Ordinal)
                || !string.Equals(_latest.Status, expected.Status, StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }
            _latest = next;
            CompleteStatusWaiters();
            return Task.FromResult(true);
        }

        public Task WaitForStatusAsync(string status)
        {
            if (string.Equals(_latest?.Status, status, StringComparison.Ordinal))
                return Task.CompletedTask;

            var waiter = new StatusWaiter(status);
            _statusWaiters.Add(waiter);
            return waiter.Task;
        }

        private void CompleteStatusWaiters()
        {
            for (var i = _statusWaiters.Count - 1; i >= 0; i--)
            {
                var waiter = _statusWaiters[i];
                if (!string.Equals(_latest?.Status, waiter.Status, StringComparison.Ordinal))
                    continue;

                _statusWaiters.RemoveAt(i);
                waiter.Complete();
            }
        }
    }

    private sealed class RecordingCommandRunner : ISystemUpdateCommandRunner
    {
        private readonly List<CountWaiter> _waiters = [];
        public List<SystemCommandRequest> Requests { get; } = [];

        public Task<SystemCommandResult> RunAsync(SystemCommandRequest command, CancellationToken cancellationToken = default)
        {
            Requests.Add(command);
            CompleteSatisfiedWaiters();

            return Task.FromResult(new SystemCommandResult(0, $"ok:{command.Stage}"));
        }

        public Task WaitForCountAsync(int count)
        {
            if (Requests.Count >= count)
                return Task.CompletedTask;

            var waiter = new CountWaiter(count);
            _waiters.Add(waiter);
            return waiter.Task;
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
        private readonly (int Index, string FileName, SystemCommandResult Result)[] _script;
        private readonly List<CountWaiter> _waiters = [];
        public List<SystemCommandRequest> Requests { get; } = [];

        public ScriptedCommandRunner(params (int Index, string FileName, SystemCommandResult Result)[] script)
        {
            _script = script;
        }

        public Task<SystemCommandResult> RunAsync(SystemCommandRequest command, CancellationToken cancellationToken = default)
        {
            var index = Requests.Count;
            Requests.Add(command);
            CompleteSatisfiedWaiters();

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
            if (Requests.Count >= count)
                return Task.CompletedTask;

            var waiter = new CountWaiter(count);
            _waiters.Add(waiter);
            return waiter.Task;
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

        public StatusWaiter(string status) => Status = status;

        public string Status { get; }

        public Task Task => _tcs.Task;

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
