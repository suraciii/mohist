using Mohist.Cli;
using Xunit;

namespace Mohist.Cli.Tests.Update;

public sealed partial class ManagedRuntimeTransactionSpecs
{
    [Fact]
    public async Task Commit_ReclaimsOnlyItsOwnRegenerableTransactionPayload()
    {
        var fixture = ManagedFixture.Create(activationCode: 0);
        const string transactionId = "tx-reclaim";
        var prepared = await fixture.Transaction.PrepareAsync("/repo", "server", transactionId, null);
        var session = Assert.IsType<ManagedUpdateSession>(prepared.Session);
        var transactionRoot = Path.Combine(fixture.RuntimeRoot, "transactions", transactionId).Replace('\\', '/');
        var snapshotRoot = Path.Combine(transactionRoot, "snapshot").Replace('\\', '/');
        var buildRoot = Path.Combine(transactionRoot, "build").Replace('\\', '/');
        var candidateRoot = Path.Combine(transactionRoot, "candidate").Replace('\\', '/');
        fixture.Files.AddFile(Path.Combine(candidateRoot, "leftover.txt"), "staged payload");

        Assert.True(fixture.Files.DirectoryExists(snapshotRoot));
        Assert.True(fixture.Files.DirectoryExists(buildRoot));
        Assert.True(fixture.Files.DirectoryExists(candidateRoot));

        Assert.Equal(0, await fixture.Transaction.CommitAsync(session));

        Assert.False(fixture.Files.DirectoryExists(snapshotRoot));
        Assert.False(fixture.Files.DirectoryExists(buildRoot));
        Assert.False(fixture.Files.DirectoryExists(candidateRoot));
        Assert.True(fixture.Files.HasFile(Path.Combine(transactionRoot, "state.json")));
        Assert.True(fixture.Files.DirectoryExists(session.ReleaseRoot));
    }

    [Fact]
    public async Task Commit_WhenFinalizeAndRollbackFail_PreservesTransactionPayload()
    {
        var fixture = ManagedFixture.Create(activationCode: 0);
        const string transactionId = "tx-preserve-after-failure";
        var launcherPath = UpdateOperations.ResolveCliWrapperPath("/home/test");
        fixture.Files.AddFile(launcherPath, "previous CLI launcher");
        var prepared = await fixture.Transaction.PrepareAsync("/repo", "cli", transactionId, launcherPath);
        var session = Assert.IsType<ManagedUpdateSession>(prepared.Session);
        var transactionRoot = Path.Combine(fixture.RuntimeRoot, "transactions", transactionId).Replace('\\', '/');
        var snapshotRoot = Path.Combine(transactionRoot, "snapshot").Replace('\\', '/');
        var buildRoot = Path.Combine(transactionRoot, "build").Replace('\\', '/');
        var backupPath = Path.Combine(transactionRoot, "cli-launcher.previous").Replace('\\', '/');
        fixture.Files.FailNextDelete = path => string.Equals(path, backupPath, StringComparison.Ordinal);

        Assert.Equal(1, await fixture.Transaction.CommitAsync(session));
        fixture.Activator.RestoreCode = 17;

        Assert.Equal(17, await fixture.Transaction.RollbackAsync(session, "commit finalization failed"));
        Assert.True(fixture.Files.DirectoryExists(snapshotRoot));
        Assert.True(fixture.Files.DirectoryExists(buildRoot));
        Assert.True(fixture.Files.HasFile(Path.Combine(transactionRoot, "state.json")));
    }
}
