using Microsoft.Extensions.Time.Testing;
using Mohist.Server.SystemInfo;
using Xunit;
using static Mohist.Server.SpecTests.Specs.SystemSpecs.SystemUpdateTestFactory;

namespace Mohist.Server.SpecTests.Specs.SystemSpecs;

public class SystemUpdateStartSpecs
{
    private static readonly DateTimeOffset FixedNow = new(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);

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

    [Fact]
    public async Task InMemoryUpdateStore_ReleaseStaleLockAsync_ReleasesHeldLockWithoutProcessLocalMatch()
    {
        var store = new InMemoryUpdateStore();
        Assert.True(await store.TryAcquireLockAsync("stale-job"));

        await store.ReleaseStaleLockAsync("stale-job");

        Assert.True(await store.TryAcquireLockAsync("new-job"));
    }

    [Fact]
    public async Task InMemoryUpdateStore_ReleaseStaleLockAsync_IsIdempotentWhenLockNotHeld()
    {
        var store = new InMemoryUpdateStore();

        await store.ReleaseStaleLockAsync("some-job");

        Assert.True(await store.TryAcquireLockAsync("new-job"));
    }

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

}
