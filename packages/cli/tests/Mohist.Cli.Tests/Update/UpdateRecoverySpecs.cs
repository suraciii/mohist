using System.Net;
using Mohist.Cli;
using Xunit;

namespace Mohist.Cli.Tests.Update;

public class UpdateRecoverySpecs
{
    [Fact]
    public async Task UpdateAll_WhenServerUpdateFails_LeavesActiveRunnerUntouched()
    {
        var tempRoot = "/mohist-tests/mohist-update-all-fail1";
        var f = new UpdateTestFactory(tempRoot);
        f.SeedPackagedSkillAssets();
        f.SeedRunnerUnit();

        f.Commands.SetStdoutFor("systemctl", args => args.Length >= 3 && args[1] == "is-active", "active\n");
        f.Commands.SetExitCodeFor("dotnet", args => args.Length > 0 && args[0] == "publish", 1);
        var updater = f.BuildUpdater(new SequenceHttpHandler(HttpStatusCode.OK), unitDir: UpdateTestFactory.UnitDir);

        var exitCode = await updater.UpdateAllAsync(tempRoot, dryRun: false, cliPath: "/home/user/.local/bin/mo", continueAfterCliUpdate: true);

        Assert.Equal(1, exitCode);
        Assert.Contains(f.Commands.ExecutedCommands, c =>
            c.FileName == "systemctl" && c.Args.SequenceEqual(["--user", "is-active", "mohist-runner.service"]));
        Assert.DoesNotContain(f.Commands.ExecutedCommands, c =>
            c.FileName == "systemctl" && c.Args.SequenceEqual(["--user", "stop", "mohist-runner.service"]));
        Assert.DoesNotContain(f.Commands.ExecutedCommands, c =>
            c.FileName == "systemctl" && c.Args.SequenceEqual(["--user", "start", "mohist-runner.service"]));
    }

    [Fact]
    public async Task UpdateAll_WhenServerUpdateFailsAndRunnerWasNotRunning_DoesNotRestoreRunner()
    {
        var tempRoot = "/mohist-tests/mohist-update-all-fail1b";
        var f = new UpdateTestFactory(tempRoot);
        f.SeedPackagedSkillAssets();
        f.SeedRunnerUnit();

        f.Commands.SetStdoutFor("systemctl", args => args.Length >= 3 && args[1] == "is-active", "inactive\n");
        f.Commands.SetExitCodeFor("dotnet", args => args.Length > 0 && args[0] == "publish", 1);
        var updater = f.BuildUpdater(new SequenceHttpHandler(HttpStatusCode.OK), unitDir: UpdateTestFactory.UnitDir);

        var exitCode = await updater.UpdateAllAsync(tempRoot, dryRun: false, cliPath: "/home/user/.local/bin/mo", continueAfterCliUpdate: true);

        Assert.Equal(1, exitCode);
        Assert.Contains(f.Commands.ExecutedCommands, c =>
            c.FileName == "systemctl" && c.Args.SequenceEqual(["--user", "is-active", "mohist-runner.service"]));
        Assert.DoesNotContain(f.Commands.ExecutedCommands, c =>
            c.FileName == "systemctl" && c.Args.SequenceEqual(["--user", "stop", "mohist-runner.service"]));
        Assert.DoesNotContain(f.Commands.ExecutedCommands, c =>
            c.FileName == "systemctl" && c.Args.SequenceEqual(["--user", "start", "mohist-runner.service"]));
    }

    [Fact]
    public async Task UpdateAll_WhenServerReadinessFails_LeavesActiveRunnerUntouched()
    {
        var tempRoot = "/mohist-tests/mohist-update-all-timeout";
        var f = new UpdateTestFactory(tempRoot);
        f.SeedPackagedSkillAssets();
        f.SeedRunnerUnit();

        f.Commands.SetStdoutFor("systemctl", args => args.Length >= 3 && args[1] == "is-active", "active\n");
        var readiness = new SequenceHttpHandler(
            new ResponseSpec(HttpStatusCode.ServiceUnavailable));
        var updater = f.BuildUpdater(
            readiness,
            serverReadyTimeout: TimeSpan.FromMilliseconds(150),
            unitDir: UpdateTestFactory.UnitDir);

        var exitCode = await updater.UpdateAllAsync(tempRoot, dryRun: false, cliPath: "/home/user/.local/bin/mo", continueAfterCliUpdate: true);

        Assert.Equal(1, exitCode);
        Assert.DoesNotContain(f.Commands.ExecutedCommands, c =>
            c.FileName == "systemctl" && c.Args.SequenceEqual(["--user", "stop", "mohist-runner.service"]));
        Assert.DoesNotContain(f.Commands.ExecutedCommands, c =>
            c.FileName == "systemctl" && c.Args.SequenceEqual(["--user", "start", "mohist-runner.service"]));
    }

    [Fact]
    public async Task UpdateAll_WhenInterruptedAfterRunnerStop_RestoresRunner()
    {
        var tempRoot = "/mohist-tests/mohist-update-all-ctrlc";
        var f = new UpdateTestFactory(tempRoot);
        f.SeedPackagedSkillAssets();
        f.SeedRunnerUnit();

        f.Commands.SetStdoutFor("systemctl", args => args.Length >= 3 && args[1] == "is-active", "active\n");
        var updater = f.BuildUpdater(
            new SequenceHttpHandler(HttpStatusCode.OK),
            unitDir: UpdateTestFactory.UnitDir);

        using var cts = new CancellationTokenSource();
        f.Commands.OnExecute = (fileName, args) =>
        {
            if (fileName == "systemctl" && args.SequenceEqual(["--user", "stop", "mohist-runner.service"]))
                cts.Cancel();
        };
        var exitCode = await updater.UpdateAllAsync(tempRoot, dryRun: false, cliPath: "/home/user/.local/bin/mo", cts.Token, continueAfterCliUpdate: true);

        Assert.Equal(130, exitCode);
        Assert.Contains(f.Commands.ExecutedCommands, c =>
            c.FileName == "systemctl" && c.Args.SequenceEqual(["--user", "stop", "mohist-runner.service"]));
        Assert.Contains(f.Commands.ExecutedCommands, c =>
            c.FileName == "systemctl" && c.Args.SequenceEqual(["--user", "start", "mohist-runner.service"]));
    }

    [Fact]
    public async Task UpdateAll_WhenInterruptedBeforeRunnerStop_ExitsCleanlyWithoutRestoringRunner()
    {
        var tempRoot = "/mohist-tests/mohist-update-all-early-cancel";
        var f = new UpdateTestFactory(tempRoot);
        f.SeedPackagedSkillAssets();

        f.Commands.SetStdoutFor("systemctl", args => args.Length >= 3 && args[1] == "is-active", "active\n");
        var updater = f.BuildUpdater(new SequenceHttpHandler(HttpStatusCode.OK), unitDir: UpdateTestFactory.UnitDir);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var exitCode = await updater.UpdateAllAsync(tempRoot, dryRun: false, cliPath: "/home/user/.local/bin/mo", cts.Token, continueAfterCliUpdate: true);

        Assert.Equal(130, exitCode);
        Assert.Contains("No recovery needed", f.Stdout.ToString());
        Assert.DoesNotContain(f.Commands.ExecutedCommands, c =>
            c.FileName == "systemctl" && c.Args.SequenceEqual(["--user", "stop", "mohist-runner.service"]));
        Assert.DoesNotContain(f.Commands.ExecutedCommands, c =>
            c.FileName == "systemctl" && c.Args.SequenceEqual(["--user", "start", "mohist-runner.service"]));
    }

    [Fact]
    public async Task UpdateAll_WhenRunnerRollbackAndPriorRestartFail_ReportUnavailableCapabilityAndManualCommand()
    {
        var tempRoot = "/mohist-tests/mohist-update-all-restore-fail";
        var f = new UpdateTestFactory(tempRoot);
        f.SeedPackagedSkillAssets();
        f.SeedRunnerUnit();

        f.Commands.SetStdoutFor("systemctl", args => args.Length >= 3 && args[1] == "is-active", "active\n");
        f.Commands.SetExitCodeFor("systemctl", args => args.Length >= 2 && args[1] == "start", 1);
        f.Runtime.RunnerIdentityTransform = identity => identity with
        {
            BuildGitHash = "stale-runner-source",
            ArtifactDigest = "0000000000000000000000000000000000000000000000000000000000000000",
        };
        f.Files.DirectoryLinkDeleteFailure = link => link.EndsWith("/runtime/runner/current", StringComparison.Ordinal)
            ? new IOException("runner current link delete denied")
            : null;
        var updater = f.BuildUpdater(
            new SequenceHttpHandler(HttpStatusCode.OK),
            unitDir: UpdateTestFactory.UnitDir);

        var exitCode = await updater.UpdateAllAsync(tempRoot, dryRun: false, cliPath: "/home/user/.local/bin/mo", continueAfterCliUpdate: true);

        Assert.Equal(1, exitCode);
        Assert.Contains("Runtime recovery failed", f.Stderr.ToString());
        Assert.Contains("mo service start runner", f.Stderr.ToString());
    }
}
