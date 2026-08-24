using EnvironmentAbstractions.TestHelpers;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests.Update;

public class UpdateSlackSpecs
{
    [Fact]
    public async Task UpdateSlack_WhenInstalled_ReplacesBinaryRefreshesLauncherAndStarts()
    {
        var f = new UpdateTestFactory();
        var installer = new FakeServiceInstaller { SlackInstalled = true };
        ConfigureSuccessfulBuild(f, "/repo", "new binary");
        var updater = f.BuildUpdater(serviceInstaller: installer);

        var exitCode = await updater.UpdateSlackAsync("/repo", dryRun: false);

        Assert.Equal(0, exitCode);
        var command = Assert.Single(f.Commands.ExecutedCommands);
        Assert.Equal("go", command.FileName);
        Assert.Equal(
            ["build", "-tags", "netgo,osusergo", "-buildvcs=false", "-o", Path.Combine("bin", ".update") + Path.DirectorySeparatorChar, "./cmd/mohist-slack"],
            command.Args);
        Assert.Equal(Path.Combine("/repo", "packages", "go", "mohist-slack"), command.WorkingDirectory);
        Assert.Equal(
            [
                nameof(FakeServiceInstaller.CaptureSlackServiceAsync),
                nameof(FakeServiceInstaller.StopSlackAsync),
                nameof(FakeServiceInstaller.RefreshSlackServiceAsync),
                nameof(FakeServiceInstaller.StartSlackAsync),
                nameof(FakeServiceInstaller.IsSlackRunningAsync),
                nameof(FakeServiceInstaller.IsSlackRunningAsync),
            ],
            installer.Calls);
        Assert.Equal("new binary", f.Files.Read(InstalledBinary("/repo")));
    }

    [Fact]
    public async Task UpdateSlack_WhenProcessAppearsAfterStart_WaitsForStableActivation()
    {
        var f = new UpdateTestFactory();
        var installer = new FakeServiceInstaller { SlackInstalled = true };
        installer.SlackRunningResults.Enqueue(false);
        installer.SlackRunningResults.Enqueue(true);
        installer.SlackRunningResults.Enqueue(true);
        ConfigureSuccessfulBuild(f, "/repo", "new binary");
        f.Files.AddFile(InstalledBinary("/repo"), "old binary");
        var updater = f.BuildUpdater(serviceInstaller: installer);

        var exitCode = await updater.UpdateSlackAsync("/repo", dryRun: false);

        Assert.Equal(0, exitCode);
        Assert.Equal(3, installer.Calls.Count(call => call == nameof(FakeServiceInstaller.IsSlackRunningAsync)));
        Assert.DoesNotContain(nameof(FakeServiceInstaller.RestoreSlackServiceAsync), installer.Calls);
    }

    [Fact]
    public async Task UpdateSlack_WhenBuildFails_DoesNotRestart()
    {
        var f = new UpdateTestFactory();
        var installer = new FakeServiceInstaller { SlackInstalled = true };
        f.Commands.SetNextResult(17, "build output", "build error");
        var updater = f.BuildUpdater(serviceInstaller: installer);

        var exitCode = await updater.UpdateSlackAsync("/repo", dryRun: false);

        Assert.Equal(17, exitCode);
        Assert.Empty(installer.Calls);
        Assert.Contains("Build failed", f.Stderr.ToString());
    }

    [Fact]
    public async Task UpdateSlack_WhenBuildProducesWrongPlatformBinary_DoesNotStopService()
    {
        var f = new UpdateTestFactory();
        var installer = new FakeServiceInstaller
        {
            SlackInstalled = true,
            SlackBinaryName = "mohist-slack",
        };
        ConfigureSuccessfulWindowsBuild(f, "/repo", "windows binary");
        var updater = f.BuildUpdater(serviceInstaller: installer);

        var exitCode = await updater.UpdateSlackAsync("/repo", dryRun: false);

        Assert.Equal(1, exitCode);
        Assert.Empty(installer.Calls);
        Assert.Contains("without producing", f.Stderr.ToString());
    }

    [Fact]
    public async Task UpdateSlack_WhenNotInstalled_SkipsBuild()
    {
        var f = new UpdateTestFactory();
        var updater = f.BuildUpdater(serviceInstaller: new FakeServiceInstaller());

        var exitCode = await updater.UpdateSlackAsync("/repo", dryRun: false);

        Assert.Equal(0, exitCode);
        Assert.Empty(f.Commands.ExecutedCommands);
        Assert.Contains("slack service is not installed", f.Stdout.ToString());
    }

    [Fact]
    public async Task UpdateSlack_DryRun_DoesNotProbeOrBuild()
    {
        var f = new UpdateTestFactory();
        var installer = new FakeServiceInstaller { SlackInstalled = true };
        var updater = f.BuildUpdater(serviceInstaller: installer);

        var exitCode = await updater.UpdateSlackAsync("/repo with spaces", dryRun: true);

        Assert.Equal(0, exitCode);
        Assert.Empty(f.Commands.ExecutedCommands);
        Assert.Empty(installer.Calls);
        Assert.Contains(
            $"go build -tags netgo,osusergo -buildvcs=false -o {Path.Combine("bin", ".update") + Path.DirectorySeparatorChar} ./cmd/mohist-slack",
            f.Stdout.ToString());
        Assert.Contains("mo service stop slack", f.Stdout.ToString());
        Assert.Contains("refresh the installed Slack service launcher", f.Stdout.ToString());
        Assert.Contains("mo service start slack", f.Stdout.ToString());
    }

    [Fact]
    public async Task UpdateSlack_WhenAnotherTransactionHoldsUserLock_DoesNotInspectOrMutate()
    {
        const string userHome = "/home/test";
        var f = new UpdateTestFactory();
        var installer = new FakeServiceInstaller { SlackInstalled = true };
        using var held = f.Files.TryAcquireFileLock(
            Path.Combine(userHome, ".mohist", "update", "slack", "transaction.lock"));
        Assert.NotNull(held);
        var updater = f.BuildUpdater(serviceInstaller: installer, userHome: userHome);

        var exitCode = await updater.UpdateSlackAsync("/repo", dryRun: false);

        Assert.Equal(1, exitCode);
        Assert.Empty(installer.Calls);
        Assert.Empty(f.Commands.ExecutedCommands);
        Assert.Contains("already running", f.Stderr.ToString());
    }

    [Fact]
    public async Task UpdateSlack_CanonicalizesExplicitRelativeRepositoryRoot()
    {
        var f = new UpdateTestFactory();
        var installer = new FakeServiceInstaller { SlackInstalled = true };
        var expectedRoot = Path.GetFullPath("relative-slack-root", f.Files.CurrentDirectory).Replace('\\', '/');
        ConfigureSuccessfulBuild(f, expectedRoot, "new binary");
        var updater = f.BuildUpdater(serviceInstaller: installer);

        var exitCode = await updater.UpdateSlackAsync("relative-slack-root", dryRun: false);

        Assert.Equal(0, exitCode);
        Assert.Equal(expectedRoot, Assert.Single(installer.RefreshSlackRoots));
        Assert.Equal(
            Path.Combine(expectedRoot, "packages", "go", "mohist-slack"),
            Assert.Single(f.Commands.ExecutedCommands).WorkingDirectory);
    }

    [Fact]
    public async Task UpdateSlack_WhenStopFails_DoesNotReplaceOrRefresh()
    {
        var f = new UpdateTestFactory();
        var installer = new FakeServiceInstaller { SlackInstalled = true, StopSlackResult = 9 };
        ConfigureSuccessfulBuild(f, "/repo", "new binary");
        f.Files.AddFile(InstalledBinary("/repo"), "old binary");
        var updater = f.BuildUpdater(serviceInstaller: installer);

        var exitCode = await updater.UpdateSlackAsync("/repo", dryRun: false);

        Assert.Equal(9, exitCode);
        Assert.Equal("old binary", f.Files.Read(InstalledBinary("/repo")));
        Assert.Equal(
            [nameof(FakeServiceInstaller.CaptureSlackServiceAsync), nameof(FakeServiceInstaller.StopSlackAsync)],
            installer.Calls);
        Assert.True(f.Files.Exists(Path.Combine(
            "/repo", "packages", "go", "mohist-slack", "bin", ".update", "recovery-required")));
    }

    [Fact]
    public async Task UpdateSlack_WhenServiceSnapshotFails_DoesNotStopOrReplace()
    {
        var f = new UpdateTestFactory();
        var installer = new FakeServiceInstaller { SlackInstalled = true, SlackSnapshot = null };
        ConfigureSuccessfulBuild(f, "/repo", "new binary");
        f.Files.AddFile(InstalledBinary("/repo"), "old binary");
        var updater = f.BuildUpdater(serviceInstaller: installer);

        var exitCode = await updater.UpdateSlackAsync("/repo", dryRun: false);

        Assert.Equal(1, exitCode);
        Assert.Equal("old binary", f.Files.Read(InstalledBinary("/repo")));
        Assert.Equal([nameof(FakeServiceInstaller.CaptureSlackServiceAsync)], installer.Calls);
        Assert.Contains("snapshot failed", f.Stderr.ToString());
    }

    [Fact]
    public async Task UpdateSlack_WhenDurableBinaryBackupFails_DoesNotStopOrReplace()
    {
        var f = new UpdateTestFactory();
        var installer = new FakeServiceInstaller { SlackInstalled = true };
        ConfigureSuccessfulBuild(f, "/repo", "new binary");
        f.Files.AddFile(InstalledBinary("/repo"), "old binary");
        f.Files.FailNextOpenWrite = path => path.EndsWith($"{BinaryName()}.previous", StringComparison.Ordinal);
        var updater = f.BuildUpdater(serviceInstaller: installer);

        var exitCode = await updater.UpdateSlackAsync("/repo", dryRun: false);

        Assert.Equal(1, exitCode);
        Assert.Equal("old binary", f.Files.Read(InstalledBinary("/repo")));
        Assert.DoesNotContain(nameof(FakeServiceInstaller.StopSlackAsync), installer.Calls);
        Assert.Contains("binary backup failed", f.Stderr.ToString());
    }

    [Fact]
    public async Task UpdateSlack_WhenBinaryReplacementFails_DoesNotRefreshOrStart()
    {
        var f = new UpdateTestFactory();
        var installer = new FakeServiceInstaller { SlackInstalled = true };
        ConfigureSuccessfulBuild(f, "/repo", "new binary");
        f.Files.AddFile(InstalledBinary("/repo"), "old binary");
        f.Files.FailNextMoveTo = path => string.Equals(path, InstalledBinary("/repo"), StringComparison.OrdinalIgnoreCase);
        var updater = f.BuildUpdater(serviceInstaller: installer);

        var exitCode = await updater.UpdateSlackAsync("/repo", dryRun: false);

        Assert.Equal(1, exitCode);
        Assert.Equal("old binary", f.Files.Read(InstalledBinary("/repo")));
        Assert.Equal(
            [
                nameof(FakeServiceInstaller.CaptureSlackServiceAsync),
                nameof(FakeServiceInstaller.StopSlackAsync),
                nameof(FakeServiceInstaller.StartSlackAsync),
                nameof(FakeServiceInstaller.IsSlackRunningAsync),
                nameof(FakeServiceInstaller.IsSlackRunningAsync),
            ],
            installer.Calls);
        Assert.Contains("binary replacement failed", f.Stderr.ToString());
        Assert.Contains("Previous Slack service was restarted", f.Stdout.ToString());
    }

    [Fact]
    public async Task UpdateSlack_WhenLauncherRefreshFails_RestoresPreviousBinaryAndService()
    {
        var f = new UpdateTestFactory();
        var installer = new FakeServiceInstaller { SlackInstalled = true, RefreshSlackResult = 11 };
        ConfigureSuccessfulBuild(f, "/repo", "new binary");
        f.Files.AddFile(InstalledBinary("/repo"), "old binary");
        var updater = f.BuildUpdater(serviceInstaller: installer);

        var exitCode = await updater.UpdateSlackAsync("/repo", dryRun: false);

        Assert.Equal(11, exitCode);
        Assert.Equal("old binary", f.Files.Read(InstalledBinary("/repo")));
        Assert.Equal(
            [
                nameof(FakeServiceInstaller.CaptureSlackServiceAsync),
                nameof(FakeServiceInstaller.StopSlackAsync),
                nameof(FakeServiceInstaller.RefreshSlackServiceAsync),
                nameof(FakeServiceInstaller.RestoreSlackServiceAsync),
                nameof(FakeServiceInstaller.StartSlackAsync),
                nameof(FakeServiceInstaller.IsSlackRunningAsync),
                nameof(FakeServiceInstaller.IsSlackRunningAsync),
            ],
            installer.Calls);
    }

    [Fact]
    public async Task UpdateSlack_WhenServiceRollbackFails_PreservesBinaryBackup()
    {
        var f = new UpdateTestFactory();
        var installer = new FakeServiceInstaller
        {
            SlackInstalled = true,
            RefreshSlackResult = 11,
            RestoreSlackResult = 17,
        };
        ConfigureSuccessfulBuild(f, "/repo", "new binary");
        f.Files.AddFile(InstalledBinary("/repo"), "old binary");
        var updater = f.BuildUpdater(serviceInstaller: installer);

        var exitCode = await updater.UpdateSlackAsync("/repo", dryRun: false);

        var stagingDir = Path.Combine("/repo", "packages", "go", "mohist-slack", "bin", ".update");
        Assert.Equal(11, exitCode);
        Assert.Equal("new binary", f.Files.Read(InstalledBinary("/repo")));
        Assert.Equal("old binary", f.Files.Read(Path.Combine(stagingDir, $"{BinaryName()}.previous")));
        Assert.True(f.Files.DirectoryExists(stagingDir));
        Assert.Contains("staged recovery files remain", f.Stderr.ToString());

        var buildCount = f.Commands.ExecutedCommands.Count;
        installer.RefreshSlackResult = 0;
        installer.RestoreSlackResult = 0;
        var retryExitCode = await updater.UpdateSlackAsync("/repo", dryRun: false);
        Assert.Equal(0, retryExitCode);
        Assert.Equal(buildCount, f.Commands.ExecutedCommands.Count);
        Assert.Equal("old binary", f.Files.Read(InstalledBinary("/repo")));
        Assert.False(f.Files.DirectoryExists(stagingDir));
        Assert.Contains("Recovered the previous Slack service", f.Stdout.ToString());
    }

    [Fact]
    public async Task UpdateSlack_WhenNewServiceDoesNotStart_RestoresPreviousBinaryAndRetriesStart()
    {
        var f = new UpdateTestFactory();
        var installer = new FakeServiceInstaller { SlackInstalled = true, StartSlackResult = 13 };
        ConfigureSuccessfulBuild(f, "/repo", "new binary");
        f.Files.AddFile(InstalledBinary("/repo"), "old binary");
        var updater = f.BuildUpdater(serviceInstaller: installer);

        var exitCode = await updater.UpdateSlackAsync("/repo", dryRun: false);

        Assert.Equal(13, exitCode);
        Assert.Equal("old binary", f.Files.Read(InstalledBinary("/repo")));
        Assert.Equal(2, installer.Calls.Count(call => call == nameof(FakeServiceInstaller.StartSlackAsync)));
        Assert.Contains("staged recovery files remain", f.Stderr.ToString());
    }

    [Fact]
    public async Task UpdateSlack_WhenNewServiceExitsImmediately_RestoresPreviousService()
    {
        var f = new UpdateTestFactory();
        var installer = new FakeServiceInstaller { SlackInstalled = true };
        installer.SlackRunningProbe = () =>
            installer.Calls.Count(call => call == nameof(FakeServiceInstaller.StartSlackAsync)) > 1;
        ConfigureSuccessfulBuild(f, "/repo", "new binary");
        f.Files.AddFile(InstalledBinary("/repo"), "old binary");
        var updater = f.BuildUpdater(serviceInstaller: installer);

        var exitCode = await updater.UpdateSlackAsync("/repo", dryRun: false);

        Assert.Equal(1, exitCode);
        Assert.Equal("old binary", f.Files.Read(InstalledBinary("/repo")));
        Assert.Equal(2, installer.Calls.Count(call => call == nameof(FakeServiceInstaller.StartSlackAsync)));
        Assert.True(installer.Calls.Count(call => call == nameof(FakeServiceInstaller.IsSlackRunningAsync)) > 2);
        Assert.Contains(nameof(FakeServiceInstaller.RestoreSlackServiceAsync), installer.Calls);
        Assert.Contains("Previous Slack service was restarted", f.Stdout.ToString());
    }

    [Fact]
    public async Task UpdateSlack_WhenFirstMigrationRollbackFails_KeepsNewBinaryForManualRecovery()
    {
        const string userHome = "/home/test";
        var f = new UpdateTestFactory();
        var installer = new FakeServiceInstaller
        {
            SlackInstalled = true,
            RefreshSlackResult = 11,
            RestoreSlackResult = 17,
        };
        ConfigureSuccessfulBuild(f, "/repo", "new binary");
        var updater = f.BuildUpdater(serviceInstaller: installer, userHome: userHome);

        var exitCode = await updater.UpdateSlackAsync("/repo", dryRun: false);

        var stagingDir = Path.Combine("/repo", "packages", "go", "mohist-slack", "bin", ".update");
        Assert.Equal(11, exitCode);
        Assert.Equal("new binary", f.Files.Read(InstalledBinary("/repo")));
        Assert.True(f.Files.Exists(Path.Combine(stagingDir, "recovery-required")));
        Assert.Contains(
            "\"launchContent\":\"launcher\"",
            f.Files.Read(RecoverySnapshotPath("/repo", userHome)));
        Assert.True(f.Files.IsUserOnlyFile(RecoverySnapshotPath("/repo", userHome)));
    }

    [Fact]
    public async Task UpdateSlack_WithNodeLauncherAndDormantGoBinary_UsesRollForwardRecovery()
    {
        var f = new UpdateTestFactory();
        var installer = new FakeServiceInstaller
        {
            SlackInstalled = true,
            SlackSnapshot = new SlackServiceSnapshot(
                "fake",
                "/launcher",
                "node packages/mohist-slack/dist/cli.js"),
        };
        installer.RefreshSlackResults.Enqueue(11);
        installer.RefreshSlackResults.Enqueue(0);
        ConfigureSuccessfulBuild(f, "/repo", "new binary");
        f.Files.AddFile(InstalledBinary("/repo"), "dormant go binary");
        var updater = f.BuildUpdater(serviceInstaller: installer);

        var exitCode = await updater.UpdateSlackAsync("/repo", dryRun: false);

        Assert.Equal(11, exitCode);
        Assert.Equal("new binary", f.Files.Read(InstalledBinary("/repo")));
        Assert.Equal(2, installer.Calls.Count(call => call == nameof(FakeServiceInstaller.RefreshSlackServiceAsync)));
        Assert.Contains(nameof(FakeServiceInstaller.RestoreSlackServiceAsync), installer.Calls);
        Assert.Contains("first-migration recovery completed", f.Stdout.ToString());
    }

    [Fact]
    public async Task UpdateSlack_WhenAnotherRepositoryHasUnresolvedTransaction_DoesNotBuild()
    {
        const string userHome = "/home/test";
        var f = new UpdateTestFactory();
        f.Files.WriteAllTextUserOnly(
            Path.Combine(userHome, ".mohist", "update", "slack", "recovery-required"),
            "{}");
        var installer = new FakeServiceInstaller { SlackInstalled = true };
        var updater = f.BuildUpdater(serviceInstaller: installer, userHome: userHome);

        var exitCode = await updater.UpdateSlackAsync("/other-repo", dryRun: false);

        Assert.Equal(1, exitCode);
        Assert.Empty(installer.Calls);
        Assert.Empty(f.Commands.ExecutedCommands);
        Assert.Contains("another repository", f.Stderr.ToString());
    }

    [Fact]
    public async Task UpdateSlack_WhenRecoveryMarkerCannotBePersisted_DoesNotMutateService()
    {
        var f = new UpdateTestFactory();
        var installer = new FakeServiceInstaller { SlackInstalled = true };
        ConfigureSuccessfulBuild(f, "/repo", "new binary");
        f.Files.AddFile(InstalledBinary("/repo"), "old binary");
        var recoveryMarker = Path.Combine(
            "/repo", "packages", "go", "mohist-slack", "bin", ".update", "recovery-required");
        f.Files.MarkReadOnly(recoveryMarker);
        var updater = f.BuildUpdater(serviceInstaller: installer);

        var exitCode = await updater.UpdateSlackAsync("/repo", dryRun: false);

        Assert.Equal(1, exitCode);
        Assert.Equal("old binary", f.Files.Read(InstalledBinary("/repo")));
        Assert.DoesNotContain(nameof(FakeServiceInstaller.StopSlackAsync), installer.Calls);
        Assert.Contains("recovery state could not be persisted", f.Stderr.ToString());
    }

    [Fact]
    public async Task UpdateSlack_WhenCommitMarkerCannotBePersisted_PreservesRecoveryPayload()
    {
        const string userHome = "/home/test";
        var f = new UpdateTestFactory();
        var installer = new FakeServiceInstaller { SlackInstalled = true };
        ConfigureSuccessfulBuild(f, "/repo", "new binary");
        f.Files.AddFile(InstalledBinary("/repo"), "old binary");
        f.Files.FailNextWrite = (path, _) => path.EndsWith("recovery-required.committed.tmp", StringComparison.Ordinal);
        var updater = f.BuildUpdater(serviceInstaller: installer, userHome: userHome);

        var exitCode = await updater.UpdateSlackAsync("/repo", dryRun: false);

        var stagingDir = Path.Combine("/repo", "packages", "go", "mohist-slack", "bin", ".update");
        Assert.Equal(1, exitCode);
        Assert.Equal("new binary", f.Files.Read(InstalledBinary("/repo")));
        Assert.True(f.Files.DirectoryExists(stagingDir));
        Assert.True(f.Files.Exists(RecoverySnapshotPath("/repo", userHome)));
        Assert.Contains("\"phase\":\"mutating\"", f.Files.Read(Path.Combine(stagingDir, "recovery-required")));
        Assert.Contains("could not be marked committed", f.Stderr.ToString());
    }

    [Fact]
    public async Task UpdateSlack_WhenRollbackCleanupIsDeferred_LeavesCommittedRecoveryMarker()
    {
        const string userHome = "/home/test";
        var f = new UpdateTestFactory();
        var installer = new FakeServiceInstaller { SlackInstalled = true, RefreshSlackResult = 11 };
        ConfigureSuccessfulBuild(f, "/repo", "new binary");
        f.Files.AddFile(InstalledBinary("/repo"), "old binary");
        var snapshotPath = RecoverySnapshotPath("/repo", userHome);
        f.Files.FailNextDelete = path => string.Equals(path, snapshotPath, StringComparison.OrdinalIgnoreCase);
        var updater = f.BuildUpdater(serviceInstaller: installer, userHome: userHome);

        var exitCode = await updater.UpdateSlackAsync("/repo", dryRun: false);

        var marker = Path.Combine(
            "/repo", "packages", "go", "mohist-slack", "bin", ".update", "recovery-required");
        Assert.Equal(11, exitCode);
        Assert.Equal("old binary", f.Files.Read(InstalledBinary("/repo")));
        Assert.Contains("\"phase\":\"committed\"", f.Files.Read(marker));
        Assert.True(f.Files.Exists(snapshotPath));
        Assert.Contains("cleanup was deferred", f.Stderr.ToString());
    }

    [Fact]
    public async Task UpdateSlack_WhenCancelledBeforeStop_DoesNotMutateService()
    {
        var f = new UpdateTestFactory();
        var installer = new FakeServiceInstaller { SlackInstalled = true };
        ConfigureSuccessfulBuild(f, "/repo", "new binary");
        f.Files.AddFile(InstalledBinary("/repo"), "old binary");
        var updater = f.BuildUpdater(serviceInstaller: installer);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => updater.UpdateSlackAsync("/repo", dryRun: false, cancellation.Token));

        Assert.Equal("old binary", f.Files.Read(InstalledBinary("/repo")));
        Assert.DoesNotContain(nameof(FakeServiceInstaller.StopSlackAsync), installer.Calls);
    }

    [Fact]
    public async Task UpdateSlack_WhenCancelledAfterStop_RestartsPreviousServiceBeforeThrowing()
    {
        var f = new UpdateTestFactory();
        using var cancellation = new CancellationTokenSource();
        var installer = new FakeServiceInstaller
        {
            SlackInstalled = true,
            StopSlackAction = cancellation.Cancel,
        };
        ConfigureSuccessfulBuild(f, "/repo", "new binary");
        f.Files.AddFile(InstalledBinary("/repo"), "old binary");
        var updater = f.BuildUpdater(serviceInstaller: installer);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => updater.UpdateSlackAsync("/repo", dryRun: false, cancellation.Token));

        Assert.Equal("old binary", f.Files.Read(InstalledBinary("/repo")));
        Assert.Contains(nameof(FakeServiceInstaller.StartSlackAsync), installer.Calls);
        Assert.DoesNotContain(nameof(FakeServiceInstaller.RefreshSlackServiceAsync), installer.Calls);
        Assert.False(f.Files.DirectoryExists(Path.Combine(
            "/repo", "packages", "go", "mohist-slack", "bin", ".update")));
    }

    [Fact]
    public async Task UpdateSlack_WhenFirstMigrationIsCancelledAfterStop_CompletesGoRecoveryBeforeThrowing()
    {
        var f = new UpdateTestFactory();
        using var cancellation = new CancellationTokenSource();
        var installer = new FakeServiceInstaller
        {
            SlackInstalled = true,
            StopSlackAction = cancellation.Cancel,
        };
        ConfigureSuccessfulBuild(f, "/repo", "new binary");
        var updater = f.BuildUpdater(serviceInstaller: installer);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => updater.UpdateSlackAsync("/repo", dryRun: false, cancellation.Token));

        Assert.Equal("new binary", f.Files.Read(InstalledBinary("/repo")));
        Assert.Contains(nameof(FakeServiceInstaller.RestoreSlackServiceAsync), installer.Calls);
        Assert.Contains(nameof(FakeServiceInstaller.RefreshSlackServiceAsync), installer.Calls);
        Assert.Contains(nameof(FakeServiceInstaller.StartSlackAsync), installer.Calls);
        Assert.False(f.Files.DirectoryExists(Path.Combine(
            "/repo", "packages", "go", "mohist-slack", "bin", ".update")));
    }

    [Fact]
    public async Task UpdateSlack_CompletesInterruptedFirstMigrationBeforeBuildingAgain()
    {
        const string userHome = "/home/test";
        var f = new UpdateTestFactory();
        var installer = new FakeServiceInstaller { SlackInstalled = true };
        var stagingDir = Path.Combine("/repo", "packages", "go", "mohist-slack", "bin", ".update");
        f.Files.AddDirectory(stagingDir);
        f.Files.AddFile(
            Path.Combine(stagingDir, "recovery-required"),
            $"{{\"phase\":\"mutating\",\"hadPreviousBinary\":false,\"previousBinarySha256\":null,\"binaryName\":\"{BinaryName()}\",\"snapshotId\":\"{RecoverySnapshotId("/repo")}\"}}");
        f.Files.WriteAllTextUserOnly(
            RecoverySnapshotPath("/repo", userHome),
            "{\"kind\":\"fake\",\"launchPath\":\"/launcher\",\"launchContent\":\"node launcher\",\"metadataExisted\":false}");
        f.Files.AddFile(InstalledBinary("/repo"), "interrupted new binary");
        var updater = f.BuildUpdater(serviceInstaller: installer, userHome: userHome);

        var exitCode = await updater.UpdateSlackAsync("/repo", dryRun: false);

        Assert.Equal(0, exitCode);
        Assert.Equal("interrupted new binary", f.Files.Read(InstalledBinary("/repo")));
        Assert.False(f.Files.DirectoryExists(stagingDir));
        Assert.Empty(f.Commands.ExecutedCommands);
        Assert.Equal(
            [
                nameof(FakeServiceInstaller.StopSlackAsync),
                nameof(FakeServiceInstaller.RestoreSlackServiceAsync),
                nameof(FakeServiceInstaller.RefreshSlackServiceAsync),
                nameof(FakeServiceInstaller.StartSlackAsync),
                nameof(FakeServiceInstaller.IsSlackRunningAsync),
                nameof(FakeServiceInstaller.IsSlackRunningAsync),
            ],
            installer.Calls);
        Assert.Contains("Completed the interrupted first migration", f.Stdout.ToString());
    }

    [Fact]
    public async Task UpdateSlack_WhenInterruptedFirstMigrationRecoveryIsCancelled_ConvergesBeforeThrowing()
    {
        const string userHome = "/home/test";
        var f = new UpdateTestFactory();
        using var cancellation = new CancellationTokenSource();
        var installer = new FakeServiceInstaller
        {
            SlackInstalled = true,
            StopSlackAction = cancellation.Cancel,
        };
        var stagingDir = Path.Combine("/repo", "packages", "go", "mohist-slack", "bin", ".update");
        f.Files.AddDirectory(stagingDir);
        f.Files.AddFile(
            Path.Combine(stagingDir, "recovery-required"),
            $"{{\"phase\":\"mutating\",\"hadPreviousBinary\":false,\"previousBinarySha256\":null,\"binaryName\":\"{BinaryName()}\",\"snapshotId\":\"{RecoverySnapshotId("/repo")}\"}}");
        f.Files.WriteAllTextUserOnly(
            RecoverySnapshotPath("/repo", userHome),
            "{\"kind\":\"fake\",\"launchPath\":\"/launcher\",\"launchContent\":\"node launcher\",\"metadataExisted\":false}");
        f.Files.AddFile(InstalledBinary("/repo"), "interrupted new binary");
        var updater = f.BuildUpdater(serviceInstaller: installer, userHome: userHome);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => updater.UpdateSlackAsync("/repo", dryRun: false, cancellation.Token));

        Assert.Equal("interrupted new binary", f.Files.Read(InstalledBinary("/repo")));
        Assert.False(f.Files.DirectoryExists(stagingDir));
        Assert.Contains(nameof(FakeServiceInstaller.RefreshSlackServiceAsync), installer.Calls);
        Assert.Contains(nameof(FakeServiceInstaller.StartSlackAsync), installer.Calls);
    }

    [Fact]
    public async Task UpdateSlack_UsesSnapshotIdentityFromManifestWhenRepositoryCasingChanges()
    {
        const string originalRoot = "/Repo";
        const string resumedRoot = "/repo";
        const string userHome = "/home/test";
        var f = new UpdateTestFactory();
        var installer = new FakeServiceInstaller { SlackInstalled = true };
        var stagingDir = Path.Combine(originalRoot, "packages", "go", "mohist-slack", "bin", ".update");
        f.Files.AddDirectory(stagingDir);
        f.Files.AddFile(
            Path.Combine(stagingDir, "recovery-required"),
            $"{{\"phase\":\"mutating\",\"hadPreviousBinary\":false,\"previousBinarySha256\":null,\"binaryName\":\"{BinaryName()}\",\"snapshotId\":\"{RecoverySnapshotId(originalRoot)}\"}}");
        f.Files.WriteAllTextUserOnly(
            RecoverySnapshotPath(originalRoot, userHome),
            "{\"kind\":\"fake\",\"launchPath\":\"/launcher\",\"launchContent\":\"node launcher\",\"metadataExisted\":false}");
        f.Files.AddFile(InstalledBinary(originalRoot), "interrupted new binary");
        var updater = f.BuildUpdater(serviceInstaller: installer, userHome: userHome);

        var exitCode = await updater.UpdateSlackAsync(resumedRoot, dryRun: false);

        Assert.Equal(0, exitCode);
        Assert.Equal("interrupted new binary", f.Files.Read(InstalledBinary(originalRoot)));
        Assert.False(f.Files.DirectoryExists(stagingDir));
        Assert.Empty(f.Commands.ExecutedCommands);
    }

    [Fact]
    public async Task UpdateSlack_WhenManifestRequiresMissingBackup_FailsBeforeStoppingService()
    {
        const string userHome = "/home/test";
        var f = new UpdateTestFactory();
        var installer = new FakeServiceInstaller { SlackInstalled = true };
        var stagingDir = Path.Combine("/repo", "packages", "go", "mohist-slack", "bin", ".update");
        f.Files.AddDirectory(stagingDir);
        f.Files.AddFile(
            Path.Combine(stagingDir, "recovery-required"),
            $"{{\"phase\":\"mutating\",\"hadPreviousBinary\":true,\"previousBinarySha256\":\"missing\",\"binaryName\":\"{BinaryName()}\",\"snapshotId\":\"{RecoverySnapshotId("/repo")}\"}}");
        f.Files.WriteAllTextUserOnly(
            RecoverySnapshotPath("/repo", userHome),
            "{\"kind\":\"fake\",\"launchPath\":\"/launcher\",\"launchContent\":\"launcher\",\"metadataExisted\":false}");
        f.Files.AddFile(InstalledBinary("/repo"), "new binary");
        var updater = f.BuildUpdater(serviceInstaller: installer, userHome: userHome);

        var exitCode = await updater.UpdateSlackAsync("/repo", dryRun: false);

        Assert.Equal(1, exitCode);
        Assert.Equal("new binary", f.Files.Read(InstalledBinary("/repo")));
        Assert.Empty(installer.Calls);
        Assert.True(f.Files.DirectoryExists(stagingDir));
    }

    [Fact]
    public async Task UpdateSlack_WhenMigratedServiceDoesNotStart_CompletesGoRecovery()
    {
        var f = new UpdateTestFactory();
        var installer = new FakeServiceInstaller { SlackInstalled = true };
        installer.StartSlackResults.Enqueue(13);
        installer.StartSlackResults.Enqueue(0);
        ConfigureSuccessfulBuild(f, "/repo", "new binary");
        var updater = f.BuildUpdater(serviceInstaller: installer);

        var exitCode = await updater.UpdateSlackAsync("/repo", dryRun: false);

        Assert.Equal(13, exitCode);
        Assert.Equal("new binary", f.Files.Read(InstalledBinary("/repo")));
        Assert.Contains(nameof(FakeServiceInstaller.RestoreSlackServiceAsync), installer.Calls);
        Assert.Equal(2, installer.Calls.Count(call => call == nameof(FakeServiceInstaller.RefreshSlackServiceAsync)));
        Assert.Contains("first-migration recovery completed", f.Stdout.ToString());
    }

    [Fact]
    public async Task UpdateSlack_MigratesNodeSystemdUnitWithoutChangingEnvironmentOrCredentials()
    {
        var f = new UpdateTestFactory();
        var unitPath = Path.Combine(UpdateTestFactory.UnitDir, "mohist-slack.service");
        f.Files.AddDirectory(UpdateTestFactory.UnitDir);
        f.Files.AddFile(
            unitPath,
            "[Unit]\nDescription=Mohist Slack adapter\n\n[Service]\n" +
            "WorkingDirectory=/old/repo\n" +
            "LoadCredential=operator-token:%h/.mohist/operator-token\n" +
            "Environment=\"SERVER_URL=http://custom:3456\"\n" +
            "ExecStart=node packages/mohist-slack/dist/cli.js\n" +
            "Restart=on-failure\n\n[Install]\nWantedBy=default.target\n");
        f.Commands.SetStdoutFor(
            "systemctl",
            args => args.SequenceEqual(["--user", "is-active", "mohist-slack.service"]),
            "active\n");
        ConfigureSuccessfulBuild(f, "/repo", "new binary");
        var updater = f.BuildUpdater(unitDir: UpdateTestFactory.UnitDir);

        var exitCode = await updater.UpdateSlackAsync("/repo", dryRun: false);

        Assert.Equal(0, exitCode);
        var unit = f.Files.Read(unitPath);
        Assert.Contains("WorkingDirectory=/repo", unit);
        Assert.Contains("ExecStart=/repo/packages/go/mohist-slack/bin/mohist-slack", unit);
        Assert.Contains("LoadCredential=operator-token:%h/.mohist/operator-token", unit);
        Assert.Contains("Environment=\"SERVER_URL=http://custom:3456\"", unit);
        Assert.DoesNotContain("packages/mohist-slack/dist/cli.js", unit);
        Assert.Collection(
            f.Commands.ExecutedCommands.Where(command => command.FileName == "systemctl"),
            command => Assert.Equal(["--user", "stop", "mohist-slack.service"], command.Args),
            command => Assert.Equal(["--user", "daemon-reload"], command.Args),
            command => Assert.Equal(["--user", "start", "mohist-slack.service"], command.Args),
            command => Assert.Equal(["--user", "is-active", "mohist-slack.service"], command.Args),
            command => Assert.Equal(["--user", "is-active", "mohist-slack.service"], command.Args));
    }

    [Fact]
    public async Task UpdateSlack_MigratesWindowsNodeLauncherAndVerifiesProcess()
    {
        var f = new UpdateTestFactory();
        const string userProfile = "/profile";
        var serviceDir = Path.Combine(userProfile, ".mohist", "service");
        var launcherPath = Path.Combine(serviceDir, "mohist-slack.cmd");
        var metadataPath = Path.Combine(serviceDir, "mohist-slack.install.json");
        f.Files.AddDirectory(serviceDir);
        f.Files.AddFile(
            launcherPath,
            "@echo off\r\ncd /d C:\\old-repo\r\n" +
            "set \"SERVER_URL=http://custom:3456\"\r\n" +
            "node packages\\mohist-slack\\dist\\cli.js\r\n");
        f.Files.AddFile(metadataPath, "{\"backend\":\"scheduled-task\",\"serverUrl\":\"http://custom:3456\"}");
        Func<string[], bool> isProcessProbe = args => args[^1].Contains("Win32_Process", StringComparison.Ordinal);
        f.Commands.QueueResultFor("powershell.exe", isProcessProbe, 0, "4321\r\n", "");
        f.Commands.QueueResultFor("powershell.exe", isProcessProbe, 0, "", "");
        f.Commands.QueueResultFor("powershell.exe", isProcessProbe, 0, "4321\r\n", "");
        f.Commands.QueueResultFor("powershell.exe", isProcessProbe, 0, "4321\r\n", "");
        ConfigureSuccessfulWindowsBuild(f, "/repo", "new windows binary");
        var installer = new WindowsScheduledTaskInstaller(
            f.Stdout,
            f.Stderr,
            f.Files,
            f.Commands,
            userProfilePath: userProfile,
            environment: new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false));
        var updater = f.BuildUpdater(serviceInstaller: installer);

        var exitCode = await updater.UpdateSlackAsync("/repo", dryRun: false);

        Assert.Equal(0, exitCode);
        Assert.Equal(
            "new windows binary",
            f.Files.Read(Path.Combine("/repo", "packages", "go", "mohist-slack", "bin", "mohist-slack.exe")));
        Assert.Contains("packages\\go\\mohist-slack\\bin\\mohist-slack.exe", f.Files.Read(launcherPath));
        Assert.Contains("http://custom:3456", f.Files.Read(launcherPath));
        Assert.Contains("\"backend\":\"scheduled-task\"", f.Files.Read(metadataPath));
        Assert.Contains(f.Commands.ExecutedCommands, command => command.FileName == "schtasks" && command.Args[0] == "/Run");
        Assert.True(f.Commands.ExecutedCommands.Count(command => command.FileName == "powershell.exe") >= 3);
    }

    [Fact]
    public async Task RefreshSlackService_WhenDaemonReloadFails_RestoresPreviousSystemdUnit()
    {
        var f = new UpdateTestFactory();
        var unitPath = Path.Combine(UpdateTestFactory.UnitDir, "mohist-slack.service");
        const string original =
            "[Service]\nWorkingDirectory=/old/repo\n" +
            "Environment=\"SERVER_URL=http://custom:3456\"\n" +
            "ExecStart=node packages/mohist-slack/dist/cli.js\n";
        f.Files.AddDirectory(UpdateTestFactory.UnitDir);
        f.Files.AddFile(unitPath, original);
        f.Commands.SetResultFor(
            "systemctl",
            args => args.SequenceEqual(["--user", "daemon-reload"]),
            7,
            "",
            "reload failed");

        var exitCode = await f.Installer.RefreshSlackServiceAsync("/repo", UpdateTestFactory.UnitDir);

        Assert.Equal(7, exitCode);
        Assert.Equal(original, f.Files.Read(unitPath));
        Assert.Equal(
            2,
            f.Commands.ExecutedCommands.Count(command =>
                command.FileName == "systemctl"
                && command.Args.SequenceEqual(["--user", "daemon-reload"])));
    }

    [Fact]
    public async Task RefreshSlackService_WhenRollbackReloadFails_ReturnsRollbackFailure()
    {
        var f = new UpdateTestFactory();
        var unitPath = Path.Combine(UpdateTestFactory.UnitDir, "mohist-slack.service");
        f.Files.AddDirectory(UpdateTestFactory.UnitDir);
        f.Files.AddFile(unitPath, "[Service]\nWorkingDirectory=/old/repo\nExecStart=node old.js\n");
        Func<string[], bool> isReload = args => args.SequenceEqual(["--user", "daemon-reload"]);
        f.Commands.QueueResultFor("systemctl", isReload, 7, "", "reload failed");
        f.Commands.QueueResultFor("systemctl", isReload, 19, "", "rollback reload failed");

        var exitCode = await f.Installer.RefreshSlackServiceAsync("/repo", UpdateTestFactory.UnitDir);

        Assert.Equal(19, exitCode);
        Assert.Contains("rollback failed", f.Stderr.ToString());
    }

    private static void ConfigureSuccessfulBuild(UpdateTestFactory factory, string root, string contents)
    {
        factory.Commands.OnExecute = (fileName, args) =>
        {
            if (fileName != "go" || args.Length == 0 || args[0] != "build") return;
            var stagingDir = Path.Combine(root, "packages", "go", "mohist-slack", "bin", ".update");
            factory.Files.AddDirectory(stagingDir);
            factory.Files.AddFile(Path.Combine(stagingDir, BinaryName()), contents);
        };
    }

    private static void ConfigureSuccessfulWindowsBuild(UpdateTestFactory factory, string root, string contents)
    {
        factory.Commands.OnExecute = (fileName, args) =>
        {
            if (fileName != "go" || args.Length == 0 || args[0] != "build") return;
            var stagingDir = Path.Combine(root, "packages", "go", "mohist-slack", "bin", ".update");
            factory.Files.AddDirectory(stagingDir);
            factory.Files.AddFile(Path.Combine(stagingDir, "mohist-slack.exe"), contents);
        };
    }

    private static string InstalledBinary(string root) =>
        Path.Combine(root, "packages", "go", "mohist-slack", "bin", BinaryName());

    private static string RecoverySnapshotPath(string root, string userHome)
        => Path.Combine(userHome, ".mohist", "update", "slack", $"{RecoverySnapshotId(root)}.service-snapshot.json");

    private static string RecoverySnapshotId(string root)
    {
        var installedBinary = InstalledBinary(root).Replace('\\', '/');
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(installedBinary)))
            .ToLowerInvariant();
    }

    private static string BinaryName() => OperatingSystem.IsWindows() ? "mohist-slack.exe" : "mohist-slack";
}
