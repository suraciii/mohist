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

    [Fact]
    public async Task FileSystemStore_TryAcquireLockAsync_RejectsPersistedActiveJobAfterRestart()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"mohist-system-update-{Guid.NewGuid():N}.json");
        try
        {
            var now = DateTimeOffset.UnixEpoch;
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

    [Fact]
    public async Task FileSystemStore_ReleaseStaleLockAsync_DeletesStaleLockFileAndAllowsReacquisitionOnFreshInstance()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"mohist-system-update-{Guid.NewGuid():N}.json");
        try
        {
            var first = CreateFileSystemStore(statePath);
            Assert.True(await first.TryAcquireLockAsync("stale-job"));

            var refreshed = CreateFileSystemStore(statePath);
            Assert.False(await refreshed.TryAcquireLockAsync("new-job"));

            await refreshed.ReleaseStaleLockAsync("stale-job");

            Assert.False(File.Exists(statePath + ".lock"));

            Assert.True(await refreshed.TryAcquireLockAsync("new-job"));
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
    public async Task FileSystemStore_ReleaseStaleLockAsync_IsIdempotentWhenLockFileAbsent()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"mohist-system-update-{Guid.NewGuid():N}.json");
        try
        {
            var store = CreateFileSystemStore(statePath);

            await store.ReleaseStaleLockAsync("any-job");

            Assert.False(File.Exists(statePath + ".lock"));
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
    public async Task FileSystemStore_ReleaseStaleLockAsync_LeavesLockHeldByDifferentOwner()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"mohist-system-update-{Guid.NewGuid():N}.json");
        try
        {
            var first = CreateFileSystemStore(statePath);
            Assert.True(await first.TryAcquireLockAsync("real-owner"));

            var refreshed = CreateFileSystemStore(statePath);
            await refreshed.ReleaseStaleLockAsync("someone-else");

            Assert.True(File.Exists(statePath + ".lock"));

            Assert.False(await refreshed.TryAcquireLockAsync("new-job"));
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
    public async Task FileSystemStore_ReleaseLockAsync_StillNoOpsAfterRestart()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"mohist-system-update-{Guid.NewGuid():N}.json");
        try
        {
            var first = CreateFileSystemStore(statePath);
            Assert.True(await first.TryAcquireLockAsync("stale-job"));

            var refreshed = CreateFileSystemStore(statePath);

            await refreshed.ReleaseLockAsync("stale-job");

            Assert.True(File.Exists(statePath + ".lock"));
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
