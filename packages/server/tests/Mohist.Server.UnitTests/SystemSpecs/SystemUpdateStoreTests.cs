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

public class SystemUpdateStoreTests
{
    [Fact]
    public async Task FileSystemStore_TryAcquireLockAsync_IsDurableAcrossStoreInstances()
    {
        var files = new InMemorySystemUpdateStateFiles();
        const string statePath = "/test/system-update.json";
        var first = CreateFileSystemStore(files, statePath);
        var second = CreateFileSystemStore(files, statePath);

        Assert.True(await first.TryAcquireLockAsync("job-1"));
        Assert.False(await second.TryAcquireLockAsync("job-2"));

        await first.ReleaseLockAsync("job-1");
        Assert.True(await second.TryAcquireLockAsync("job-2"));
    }

    [Fact]
    public async Task FileSystemStore_TryAcquireLockAsync_RejectsPersistedActiveJobAfterRestart()
    {
        var files = new InMemorySystemUpdateStateFiles();
        var now = DateTimeOffset.UnixEpoch;
        var first = CreateFileSystemStore(files);
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

        var restarted = CreateFileSystemStore(files);

        Assert.False(await restarted.TryAcquireLockAsync("job-2"));
    }

    [Fact]
    public async Task FileSystemStore_ReleaseStaleLockAsync_DeletesStaleLockFileAndAllowsReacquisitionOnFreshInstance()
    {
        var files = new InMemorySystemUpdateStateFiles();
        const string statePath = "/test/system-update.json";
        var first = CreateFileSystemStore(files, statePath);
        Assert.True(await first.TryAcquireLockAsync("stale-job"));

        var refreshed = CreateFileSystemStore(files, statePath);
        Assert.False(await refreshed.TryAcquireLockAsync("new-job"));

        await refreshed.ReleaseStaleLockAsync("stale-job");

        Assert.False(files.Exists(statePath + ".lock"));

        Assert.True(await refreshed.TryAcquireLockAsync("new-job"));
    }

    [Fact]
    public async Task FileSystemStore_ReleaseStaleLockAsync_IsIdempotentWhenLockFileAbsent()
    {
        var files = new InMemorySystemUpdateStateFiles();
        const string statePath = "/test/system-update.json";
        var store = CreateFileSystemStore(files, statePath);

        await store.ReleaseStaleLockAsync("any-job");

        Assert.False(files.Exists(statePath + ".lock"));
    }

    [Fact]
    public async Task FileSystemStore_ReleaseStaleLockAsync_LeavesLockHeldByDifferentOwner()
    {
        var files = new InMemorySystemUpdateStateFiles();
        const string statePath = "/test/system-update.json";
        var first = CreateFileSystemStore(files, statePath);
        Assert.True(await first.TryAcquireLockAsync("real-owner"));

        var refreshed = CreateFileSystemStore(files, statePath);
        await refreshed.ReleaseStaleLockAsync("someone-else");

        Assert.True(files.Exists(statePath + ".lock"));

        Assert.False(await refreshed.TryAcquireLockAsync("new-job"));
    }

    [Fact]
    public async Task FileSystemStore_ReleaseLockAsync_StillNoOpsAfterRestart()
    {
        var files = new InMemorySystemUpdateStateFiles();
        const string statePath = "/test/system-update.json";
        var first = CreateFileSystemStore(files, statePath);
        Assert.True(await first.TryAcquireLockAsync("stale-job"));

        var refreshed = CreateFileSystemStore(files, statePath);

        await refreshed.ReleaseLockAsync("stale-job");

        Assert.True(files.Exists(statePath + ".lock"));
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
}
