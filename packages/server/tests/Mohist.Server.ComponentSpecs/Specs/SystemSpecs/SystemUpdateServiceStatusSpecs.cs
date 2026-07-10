using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.SystemInfo;
using Mohist.Server.ComponentSpecs.Support;
using Xunit;
using EnvironmentAbstractions.TestHelpers;
using static Mohist.Server.ComponentSpecs.Specs.SystemSpecs.SystemUpdateServiceTestSupport;

namespace Mohist.Server.ComponentSpecs.Specs.SystemSpecs;

public class SystemUpdateServiceStatusSpecs
{
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
}
