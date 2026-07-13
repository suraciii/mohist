using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.SystemInfo;
using Mohist.Server.UnitTests.Support;
using Xunit;
using EnvironmentAbstractions.TestHelpers;
using static Mohist.Server.UnitTests.SystemSpecs.SystemUpdateServiceTestSupport;

namespace Mohist.Server.UnitTests.SystemSpecs;

public class SystemUpdateServiceStatusTests
{
    [Fact]
    public async Task GetLatestStatusAsync_DispatchesNoCommandsForActiveJob()
    {
        var store = new InMemoryUpdateStore();
        var commands = new RecordingCommandRunner();
        var now = DateTimeOffset.UnixEpoch;
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
    public async Task GetLatestStatusAsync_DoesNotPersistStateFile()
    {
        var files = new InMemorySystemUpdateStateFiles();
        const string statePath = "/test/system-update.json";
        var store = CreateFileSystemStore(files, statePath);
            var commands = new RecordingCommandRunner();
            var now = DateTimeOffset.UnixEpoch;
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

        var beforeBytes = files.ReadAllBytes(statePath);

        var service = CreateService(
                new SequencedSystemInfo(CreateInfo(runningGitHash: "newhash", sourceHead: "newhash")),
                store,
                commands,
                new StubReadinessProbe(new(true, true, true, "/assets/app.js", null)));

        var status = await service.GetLatestStatusAsync();

        Assert.NotNull(status);
        Assert.Empty(commands.Requests);

        var afterBytes = files.ReadAllBytes(statePath);
        Assert.Equal(beforeBytes, afterBytes);
    }

    [Fact]
    public async Task GetLatestStatusAsync_DoesNotReleaseLockAndStartStillRejected()
    {
        var store = new InMemoryUpdateStore();
        var commands = new RecordingCommandRunner();
        var now = DateTimeOffset.UnixEpoch;
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
