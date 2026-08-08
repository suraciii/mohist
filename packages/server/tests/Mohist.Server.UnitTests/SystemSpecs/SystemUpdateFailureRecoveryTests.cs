using Microsoft.Extensions.Time.Testing;
using Mohist.Server.SystemInfo;
using Xunit;
using static Mohist.Server.UnitTests.SystemSpecs.SystemUpdateTestFactory;

namespace Mohist.Server.UnitTests.SystemSpecs;

public class SystemUpdateFailureRecoveryTests
{
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
        Assert.Contains(latest.Logs, log => log.Stage == "Failed" && log.Message.Contains("mo service start runner"));

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
        await store.WaitForStatusAndStageAsync("failed", "Failed");

        var latest = await store.GetLatestAsync();
        Assert.True(result.Started);
        Assert.Equal("failed", latest!.Status);
        Assert.Equal("Failed", latest.Stage);
        Assert.Equal("failed", latest.Outcome);
        Assert.Equal("Runner", latest.UnavailableCapability);
        Assert.Contains(latest.Logs, log => log.Stage == "Building" && log.Message == "build threw");
        Assert.Contains(latest.Logs, log => log.Stage == "Failed" && log.Message.Contains("mo service start runner"));

        var restoringIndex = store.SavedStates.FindIndex(state => state.Stage == "Restoring runner");
        var finalFailedIndex = store.SavedStates.FindLastIndex(state => state.Status == "failed" && state.Stage == "Failed");
        Assert.True(restoringIndex >= 0);
        Assert.True(finalFailedIndex > restoringIndex);
        Assert.DoesNotContain(store.SavedStates.Take(restoringIndex), state => state.Status == "failed");

        await store.WaitForUnlockAsync();
        Assert.True(await store.TryAcquireLockAsync("job-next"));
    }

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

}
