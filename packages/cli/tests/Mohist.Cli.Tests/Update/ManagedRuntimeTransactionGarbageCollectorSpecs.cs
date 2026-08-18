using System.Text.Json;
using Mohist.Cli;
using Xunit;

namespace Mohist.Cli.Tests.Update;

#pragma warning disable RS0030
public sealed class ManagedRuntimeTransactionGarbageCollectorSpecs
{
    [Fact]
    public void Collect_ReclaimsOldVerifiedPayloadButKeepsPointersStateAndRelease()
    {
        var fixture = ManagedRuntimeTransactionSpecs.ManagedFixture.Create(activationCode: 0);
        var files = fixture.Files;
        files.AddDirectory(fixture.RuntimeRoot);
        files.AddDirectory(Path.Combine(fixture.RuntimeRoot, "transactions"));
        WritePointer(files, fixture.RuntimeRoot, "active", State("candidate-activated", "active-tx"));
        WritePointer(files, fixture.RuntimeRoot, "verified", State("verified", "verified-tx"));
        SeedTransaction(files, fixture.RuntimeRoot, "old-verified", "verified");
        SeedTransaction(files, fixture.RuntimeRoot, "active-tx", "candidate-activated");
        SeedTransaction(files, fixture.RuntimeRoot, "verified-tx", "verified");
        var releasePath = Path.Combine(fixture.RuntimeRoot, "releases", "old-release", "server", "app").Replace('\\', '/');
        files.AddFile(releasePath, "immutable release");

        var result = new ManagedRuntimeTransactionGarbageCollector(files, TextWriter.Null)
            .Collect(fixture.RuntimeRoot, "current-tx");

        Assert.Equal(3, result.ReclaimedPayloadRoots);
        AssertPayloadAbsent(files, fixture.RuntimeRoot, "old-verified");
        AssertPayloadPresent(files, fixture.RuntimeRoot, "active-tx");
        AssertPayloadPresent(files, fixture.RuntimeRoot, "verified-tx");
        Assert.True(files.HasFile(StatePath(fixture.RuntimeRoot, "old-verified")));
        Assert.True(files.HasFile(releasePath));
        Assert.True(files.HasFile(Path.Combine(fixture.RuntimeRoot, "active.json").Replace('\\', '/')));
        Assert.True(files.HasFile(Path.Combine(fixture.RuntimeRoot, "verified.json").Replace('\\', '/')));
    }

    [Theory]
    [InlineData("candidate-staged")]
    [InlineData("candidate-activated")]
    [InlineData("recovery-failed")]
    [InlineData("unknown")]
    public void Collect_PreservesLiveRecoveryAndUnknownStates(string status)
    {
        var fixture = ManagedRuntimeTransactionSpecs.ManagedFixture.Create(activationCode: 0);
        var files = fixture.Files;
        files.AddDirectory(fixture.RuntimeRoot);
        files.AddDirectory(Path.Combine(fixture.RuntimeRoot, "transactions"));
        SeedTransaction(files, fixture.RuntimeRoot, "retained", status);

        var result = new ManagedRuntimeTransactionGarbageCollector(files, TextWriter.Null)
            .Collect(fixture.RuntimeRoot, "current-tx");

        Assert.Equal(0, result.ReclaimedPayloadRoots);
        AssertPayloadPresent(files, fixture.RuntimeRoot, "retained");
        Assert.True(files.HasFile(StatePath(fixture.RuntimeRoot, "retained")));
    }

    [Fact]
    public void Collect_PreservesCurrentAndMalformedOrMissingStateTransactions()
    {
        var fixture = ManagedRuntimeTransactionSpecs.ManagedFixture.Create(activationCode: 0);
        var files = fixture.Files;
        files.AddDirectory(fixture.RuntimeRoot);
        files.AddDirectory(Path.Combine(fixture.RuntimeRoot, "transactions"));
        SeedTransaction(files, fixture.RuntimeRoot, "current-tx", "verified");
        SeedTransaction(files, fixture.RuntimeRoot, "malformed", "verified");
        files.AddFile(StatePath(fixture.RuntimeRoot, "malformed"), "not-json");
        SeedTransactionWithoutState(files, fixture.RuntimeRoot, "missing-state");

        var result = new ManagedRuntimeTransactionGarbageCollector(files, TextWriter.Null)
            .Collect(fixture.RuntimeRoot, "current-tx");

        Assert.Equal(0, result.ReclaimedPayloadRoots);
        AssertPayloadPresent(files, fixture.RuntimeRoot, "current-tx");
        AssertPayloadPresent(files, fixture.RuntimeRoot, "malformed");
        AssertPayloadPresent(files, fixture.RuntimeRoot, "missing-state");
    }

    [Fact]
    public void Collect_FailsOpenWhenPointerIsMalformed()
    {
        var fixture = ManagedRuntimeTransactionSpecs.ManagedFixture.Create(activationCode: 0);
        var files = fixture.Files;
        files.AddDirectory(fixture.RuntimeRoot);
        files.AddDirectory(Path.Combine(fixture.RuntimeRoot, "transactions"));
        files.AddFile(Path.Combine(fixture.RuntimeRoot, "active.json"), "not-json");
        SeedTransaction(files, fixture.RuntimeRoot, "old-verified", "verified");

        var result = new ManagedRuntimeTransactionGarbageCollector(files, TextWriter.Null)
            .Collect(fixture.RuntimeRoot, "current-tx");

        Assert.Equal(0, result.ReclaimedPayloadRoots);
        AssertPayloadPresent(files, fixture.RuntimeRoot, "old-verified");
    }

    [Fact]
    public void RealFileSystem_CleanupRemovesReadOnlySnapshotTree()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mohist-runtime-gc-{Guid.NewGuid():N}");
        var nested = Path.Combine(root, "nested");
        var file = Path.Combine(nested, "payload.txt");
        Directory.CreateDirectory(nested);
        File.WriteAllText(file, "payload");

        try
        {
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(root, FileAttributes.ReadOnly);
                File.SetAttributes(nested, FileAttributes.ReadOnly);
                File.SetAttributes(file, FileAttributes.ReadOnly);
            }
            else
            {
                File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                File.SetUnixFileMode(nested, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                File.SetUnixFileMode(file, UnixFileMode.UserRead);
            }

            RealFileSystem.Instance.DeleteDirectoryForCleanup(root);

            Assert.False(Directory.Exists(root));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    if (Directory.Exists(nested))
                        File.SetUnixFileMode(nested, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    if (File.Exists(file))
                        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                else
                {
                    File.SetAttributes(root, FileAttributes.Normal);
                    if (Directory.Exists(nested)) File.SetAttributes(nested, FileAttributes.Normal);
                    if (File.Exists(file)) File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ManagedUpdate_ReleasesExclusiveLockWhenPreparationFails()
    {
        var fixture = ManagedRuntimeTransactionSpecs.ManagedFixture.Create(activationCode: 0);
        var lockHandle = new TrackingLock();
        fixture.Files.TryAcquireExclusiveLockOverride = _ => lockHandle;
        fixture.Commands.SetResultFor(
            "npm",
            args => args.SequenceEqual(["ci", "--include=dev"]),
            1,
            "",
            "disk full");

        var prepared = await fixture.Transaction.PrepareAsync("/repo", "server", "tx-lock-failure", null);

        Assert.Null(prepared.Session);
        Assert.True(lockHandle.Disposed);
    }

    [Fact]
    public async Task ManagedUpdate_ReleasesExclusiveLockAfterCommit()
    {
        var fixture = ManagedRuntimeTransactionSpecs.ManagedFixture.Create(activationCode: 0);
        var lockHandle = new TrackingLock();
        fixture.Files.TryAcquireExclusiveLockOverride = _ => lockHandle;

        var prepared = await fixture.Transaction.PrepareAsync("/repo", "server", "tx-lock-commit", null);
        var session = Assert.IsType<ManagedUpdateSession>(prepared.Session);
        Assert.False(lockHandle.Disposed);

        Assert.Equal(0, await fixture.Transaction.CommitAsync(session));
        Assert.True(lockHandle.Disposed);
    }

    private static void SeedTransaction(FakeFileSystem files, string runtimeRoot, string id, string status)
    {
        var root = Path.Combine(runtimeRoot, "transactions", id).Replace('\\', '/');
        files.AddDirectory(root);
        foreach (var name in new[] { "snapshot", "build", "candidate" })
        {
            var payload = Path.Combine(root, name).Replace('\\', '/');
            files.AddDirectory(payload);
            files.AddFile(Path.Combine(payload, "payload.bin"), "payload");
        }
        files.AddFile(StatePath(runtimeRoot, id), JsonSerializer.Serialize(State(status, id)));
    }

    private static void SeedTransactionWithoutState(FakeFileSystem files, string runtimeRoot, string id)
    {
        SeedTransaction(files, runtimeRoot, id, "verified");
        files.Delete(StatePath(runtimeRoot, id));
    }

    private static void WritePointer(FakeFileSystem files, string runtimeRoot, string name, RuntimeTargetSet value) =>
        files.AddFile(Path.Combine(runtimeRoot, $"{name}.json").Replace('\\', '/'), JsonSerializer.Serialize(value));

    private static RuntimeTargetSet State(string status, string id) =>
        new(status, 1, id, null, null, null, null);

    private static string StatePath(string runtimeRoot, string id) =>
        Path.Combine(runtimeRoot, "transactions", id, "state.json").Replace('\\', '/');

    private static void AssertPayloadAbsent(FakeFileSystem files, string runtimeRoot, string id)
    {
        foreach (var name in new[] { "snapshot", "build", "candidate" })
            Assert.False(files.DirectoryExists(Path.Combine(runtimeRoot, "transactions", id, name).Replace('\\', '/')));
    }

    private static void AssertPayloadPresent(FakeFileSystem files, string runtimeRoot, string id)
    {
        foreach (var name in new[] { "snapshot", "build", "candidate" })
            Assert.True(files.DirectoryExists(Path.Combine(runtimeRoot, "transactions", id, name).Replace('\\', '/')));
    }

    private sealed class TrackingLock : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
#pragma warning restore RS0030
