using System.Net;
using Mohist.Cli;
using Xunit;

namespace Mohist.Cli.Tests.Update;

public class UpdateRuntimeTransactionSpecs
{
    [Fact]
    public async Task UpdateAll_WhenServiceManagerUnavailable_DoesNotStopExistingRunnerOrChangeRuntime()
    {
        var f = new UpdateTestFactory("/home/test");
        f.SeedRunnerUnit();
        var before = f.Files.Read(Path.Combine(UpdateTestFactory.UnitDir, "mohist-runner.service"));
        f.Commands.SetStdoutFor(
            "systemctl",
            args => args.SequenceEqual(["--user", "is-active", "mohist-runner.service"]),
            "active\n");
        f.Commands.SetResultFor(
            "systemctl",
            args => args.SequenceEqual(["--user", "show-environment"]),
            1,
            "",
            "user manager unavailable");
        var updater = f.BuildUpdater(unitDir: UpdateTestFactory.UnitDir);

        var exitCode = await updater.UpdateAllAsync(
            "/clean",
            dryRun: false,
            cliPath: "/home/user/.local/bin/mo",
            continueAfterCliUpdate: true);

        Assert.Equal(1, exitCode);
        Assert.Equal(before, f.Files.Read(Path.Combine(UpdateTestFactory.UnitDir, "mohist-runner.service")));
        Assert.Null(f.Files.ReadDirectorySymbolicLink("/home/test/.local/share/mohist/runtime/runner/current"));
        Assert.DoesNotContain(f.Commands.ExecutedCommands, command =>
            command.FileName == "systemctl" && command.Args.SequenceEqual(["--user", "stop", "mohist-runner.service"]));
        Assert.DoesNotContain(f.Commands.ExecutedCommands, command =>
            command.FileName is "git" or "dotnet" or "npm");
        Assert.Contains("service manager is unavailable", f.Stderr.ToString());
    }

    [Fact]
    public async Task UpdateRunner_WhenServiceManagerUnavailable_LeavesLegacyUnitAndRuntimeUntouched()
    {
        var f = new UpdateTestFactory("/home/test");
        f.SeedRunnerUnit();
        var before = f.Files.Read(Path.Combine(UpdateTestFactory.UnitDir, "mohist-runner.service"));
        f.Commands.SetResultFor(
            "systemctl",
            args => args.SequenceEqual(["--user", "show-environment"]),
            1,
            "",
            "user manager unavailable");
        var updater = f.BuildUpdater(unitDir: UpdateTestFactory.UnitDir);

        var exitCode = await updater.UpdateRunnerAsync("/clean", dryRun: false);

        Assert.Equal(1, exitCode);
        Assert.Equal(before, f.Files.Read(Path.Combine(UpdateTestFactory.UnitDir, "mohist-runner.service")));
        Assert.Null(f.Files.ReadDirectorySymbolicLink("/home/test/.local/share/mohist/runtime/runner/current"));
        Assert.DoesNotContain(f.Commands.ExecutedCommands, command => command.FileName == "npm");
        Assert.Contains("service manager is unavailable", f.Stderr.ToString());
    }

    [Fact]
    public async Task UpdateServer_WhenLegacyLocalSourceRuntimeFails_RestoresExactUnitBeforeRestart()
    {
        const string legacyUnit = "[Service]\nWorkingDirectory=/legacy/mohist\nExecStart=dotnet /legacy/mohist/Mohist.Server.dll --urls http://127.0.0.1:4577\n";
        var f = new UpdateTestFactory("/home/test");
        f.Files.AddDirectory(UpdateTestFactory.UnitDir);
        f.Files.AddFile(Path.Combine(UpdateTestFactory.UnitDir, "mohist.service"), legacyUnit);
        f.Runtime.SetServerIdentityOverride(
            "fedcba9876543210fedcba9876543210fedcba98",
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
        var updater = f.BuildUpdater(HealthyServerHandler(), unitDir: UpdateTestFactory.UnitDir);

        var exitCode = await updater.UpdateServerAsync("/clean", dryRun: false);

        Assert.Equal(1, exitCode);
        Assert.Equal(legacyUnit, f.Files.Read(Path.Combine(UpdateTestFactory.UnitDir, "mohist.service")));
        Assert.Null(f.Files.ReadDirectorySymbolicLink("/home/test/.local/share/mohist/runtime/server/current"));
        Assert.Contains(f.Commands.ExecutedCommands, command =>
            command.FileName == "systemctl" && command.Args.SequenceEqual(["--user", "restart", "mohist.service"]));
        Assert.Contains("Recovery: restored prior local-source service target with no verified runtime version", f.Stderr.ToString());
    }

    [Fact]
    public async Task UpdateServer_PreservesExistingListenUrlOnManagedReplacement()
    {
        const string listenUrl = "http://127.0.0.1:4577";
        var f = new UpdateTestFactory("/home/test");
        f.Files.AddDirectory(UpdateTestFactory.UnitDir);
        f.Files.AddFile(
            Path.Combine(UpdateTestFactory.UnitDir, "mohist.service"),
            $"[Service]\nWorkingDirectory=/legacy/mohist\nExecStart=dotnet /legacy/mohist/Mohist.Server.dll --urls {listenUrl}\n");
        var updater = f.BuildUpdater(HealthyServerHandler(), unitDir: UpdateTestFactory.UnitDir);

        var exitCode = await updater.UpdateServerAsync("/clean", dryRun: false);

        Assert.Equal(0, exitCode);
        Assert.Contains($"--urls {listenUrl}", f.Files.Read(Path.Combine(UpdateTestFactory.UnitDir, "mohist.service")));
    }

    [Fact]
    public async Task UpdateServer_WhenVerifiedLinkWriteFails_RestoresPriorLocalSourceUnit()
    {
        const string legacyUnit = "[Service]\nWorkingDirectory=/legacy/mohist\nExecStart=dotnet /legacy/mohist/Mohist.Server.dll\n";
        var f = new UpdateTestFactory("/home/test");
        f.Files.AddDirectory(UpdateTestFactory.UnitDir);
        f.Files.AddFile(Path.Combine(UpdateTestFactory.UnitDir, "mohist.service"), legacyUnit);
        f.Files.DirectoryLinkReplaceFailure = (link, _) => link.EndsWith("/verified", StringComparison.Ordinal)
            ? new IOException("verified link write denied")
            : null;
        var updater = f.BuildUpdater(HealthyServerHandler(), unitDir: UpdateTestFactory.UnitDir);

        var exitCode = await updater.UpdateServerAsync("/clean", dryRun: false);

        Assert.Equal(1, exitCode);
        Assert.Equal(legacyUnit, f.Files.Read(Path.Combine(UpdateTestFactory.UnitDir, "mohist.service")));
        Assert.Null(f.Files.ReadDirectorySymbolicLink("/home/test/.local/share/mohist/runtime/server/current"));
        Assert.Contains("could not record the verified server version", f.Stderr.ToString());
    }

    [Fact]
    public async Task UpdateServer_WhenLinkRollbackThrows_RestoresLegacyUnitAndStopsCandidate()
    {
        const string legacyUnit = "[Service]\nWorkingDirectory=/legacy/mohist\nExecStart=dotnet /legacy/mohist/Mohist.Server.dll\n";
        var f = new UpdateTestFactory("/home/test");
        f.Files.AddDirectory(UpdateTestFactory.UnitDir);
        f.Files.AddFile(Path.Combine(UpdateTestFactory.UnitDir, "mohist.service"), legacyUnit);
        f.Runtime.SetServerIdentityOverride(
            "fedcba9876543210fedcba9876543210fedcba98",
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
        f.Files.DirectoryLinkDeleteFailure = link => link.EndsWith("/current", StringComparison.Ordinal)
            ? new IOException("current link deletion denied")
            : null;
        var updater = f.BuildUpdater(HealthyServerHandler(), unitDir: UpdateTestFactory.UnitDir);

        var exitCode = await updater.UpdateServerAsync("/clean", dryRun: false);

        Assert.Equal(1, exitCode);
        Assert.Equal(legacyUnit, f.Files.Read(Path.Combine(UpdateTestFactory.UnitDir, "mohist.service")));
        Assert.Contains(f.Commands.ExecutedCommands, command =>
            command.FileName == "systemctl" && command.Args.SequenceEqual(["--user", "daemon-reload"]));
        Assert.Contains(f.Commands.ExecutedCommands, command =>
            command.FileName == "systemctl" && command.Args.SequenceEqual(["--user", "stop", "mohist.service"]));
        Assert.Contains("service unit was restored but runtime link recovery was not confirmed", f.Stderr.ToString());
        Assert.DoesNotContain("Server runtime verification: current", f.Stdout.ToString());
    }

    [Fact]
    public async Task UpdateServer_WhenCandidatePayloadIsTampered_DoesNotInstallOrActivateIt()
    {
        var f = new UpdateTestFactory("/home/test");
        f.Files.OnDirectoryLinkReplace = (link, target) =>
        {
            if (link.EndsWith("/current", StringComparison.Ordinal)
                && target.Contains("/runtime/server/versions/", StringComparison.Ordinal))
            {
                f.Files.WriteAllText(Path.Combine(target, "Mohist.Server.dll"), "tampered-server-entry");
            }
        };
        var updater = f.BuildUpdater(HealthyServerHandler(), unitDir: UpdateTestFactory.UnitDir);

        var exitCode = await updater.UpdateServerAsync("/clean", dryRun: false);

        Assert.Equal(1, exitCode);
        Assert.Null(f.Files.ReadDirectorySymbolicLink("/home/test/.local/share/mohist/runtime/server/current"));
        Assert.False(f.Files.HasFile(Path.Combine(UpdateTestFactory.UnitDir, "mohist.service")));
        Assert.Contains("manifest, payload, or digest did not validate", f.Stderr.ToString());
        Assert.Contains("Recovery: no prior service target existed; stopped candidate service target", f.Stderr.ToString());
        Assert.DoesNotContain("service target was left unchanged", f.Stderr.ToString());
    }

    [Fact]
    public async Task UpdateRunner_WhenBinaryPayloadChangesWithoutTextChange_RejectsTheCandidate()
    {
        var f = new UpdateTestFactory("/home/test");
        f.SeedRunnerUnit();
        f.Runtime.RunnerEntryPayload = [0x80];
        f.Files.OnDirectoryLinkReplace = (link, target) =>
        {
            if (link.EndsWith("/runtime/runner/current", StringComparison.Ordinal)
                && target.Contains("/runtime/runner/versions/", StringComparison.Ordinal))
            {
                f.Files.WriteAllBytes(Path.Combine(target, "dist", "cli.js"), [0x81]);
            }
        };
        var before = f.Files.Read(Path.Combine(UpdateTestFactory.UnitDir, "mohist-runner.service"));
        var updater = f.BuildUpdater(unitDir: UpdateTestFactory.UnitDir);

        var exitCode = await updater.UpdateRunnerAsync("/clean", dryRun: false);

        Assert.Equal(1, exitCode);
        Assert.Equal(before, f.Files.Read(Path.Combine(UpdateTestFactory.UnitDir, "mohist-runner.service")));
        Assert.Null(f.Files.ReadDirectorySymbolicLink("/home/test/.local/share/mohist/runtime/runner/current"));
        Assert.Contains("manifest, payload, or digest did not validate", f.Stderr.ToString());
    }

    [Fact]
    public async Task UpdateRunner_ShortNotReadyIntervalWaitsForExplicitSignalWithoutRollback()
    {
        var f = new UpdateTestFactory("/home/test");
        f.SeedRunnerUnit();
        f.Runtime.HoldRunnerReadiness = true;
        var updater = f.BuildUpdater(unitDir: UpdateTestFactory.UnitDir);

        var update = updater.UpdateRunnerAsync("/clean", dryRun: false);
        await f.Runtime.RunnerReadinessWaited;
        Assert.False(update.IsCompleted);

        f.Runtime.ReleaseRunnerReadiness();
        var exitCode = await update;

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("Recovery:", f.Stderr.ToString());
        Assert.Contains("Runner update is verified and current.", f.Stdout.ToString());
    }

    [Fact]
    public async Task UpdateLease_RejectsSameComponentInterleavingButAllowsOtherComponent()
    {
        var f = new UpdateTestFactory("/home/test");
        f.SeedRunnerUnit();
        f.Runtime.HoldRunnerReadiness = true;
        var updater = f.BuildUpdater(new SequenceHttpHandler(HttpStatusCode.OK), unitDir: UpdateTestFactory.UnitDir);

        var firstRunner = updater.UpdateRunnerAsync("/clean", dryRun: false);
        await f.Runtime.RunnerReadinessWaited;

        var competingRunnerExit = await updater.UpdateRunnerAsync("/clean", dryRun: false);
        var serverExit = await updater.UpdateServerAsync("/clean", dryRun: false);

        f.Runtime.ReleaseRunnerReadiness();
        var firstRunnerExit = await firstRunner;

        Assert.Equal(1, competingRunnerExit);
        Assert.Equal(0, serverExit);
        Assert.Equal(0, firstRunnerExit);
        Assert.Contains("Runner update is already in progress", f.Stderr.ToString());
    }

    [Fact]
    public async Task UpdateAll_WhenServerBuildFails_LeavesActiveRunnerUntouched()
    {
        var f = new UpdateTestFactory("/home/test");
        f.SeedRunnerUnit();
        var runnerUnit = f.Files.Read(Path.Combine(UpdateTestFactory.UnitDir, "mohist-runner.service"));
        f.Commands.SetStdoutFor(
            "systemctl",
            args => args.SequenceEqual(["--user", "is-active", "mohist-runner.service"]),
            "active\n");
        f.Commands.SetExitCodeFor("dotnet", args => args.Length > 0 && args[0] == "publish", 1);
        var updater = f.BuildUpdater(HealthyServerHandler(), unitDir: UpdateTestFactory.UnitDir);

        var exitCode = await updater.UpdateAllAsync(
            "/clean",
            dryRun: false,
            cliPath: "/home/user/.local/bin/mo",
            continueAfterCliUpdate: true);

        Assert.Equal(1, exitCode);
        Assert.Equal(runnerUnit, f.Files.Read(Path.Combine(UpdateTestFactory.UnitDir, "mohist-runner.service")));
        Assert.DoesNotContain(f.Commands.ExecutedCommands, command =>
            command.FileName == "systemctl" && command.Args.SequenceEqual(["--user", "stop", "mohist-runner.service"]));
        Assert.DoesNotContain(f.Commands.ExecutedCommands, command =>
            command.FileName == "systemctl" && command.Args.SequenceEqual(["--user", "start", "mohist-runner.service"]));
    }

    [Fact]
    public async Task UpdateAll_WhenRunnerCandidateBuildFails_RollsBackServerWithoutStoppingRunner()
    {
        const string legacyServerUnit = "[Service]\nWorkingDirectory=/legacy/mohist\nExecStart=dotnet /legacy/mohist/Mohist.Server.dll\n";
        var f = new UpdateTestFactory("/home/test");
        f.Files.AddDirectory(UpdateTestFactory.UnitDir);
        f.Files.AddFile(Path.Combine(UpdateTestFactory.UnitDir, "mohist.service"), legacyServerUnit);
        f.SeedRunnerUnit();
        var runnerUnit = f.Files.Read(Path.Combine(UpdateTestFactory.UnitDir, "mohist-runner.service"));
        f.Commands.SetStdoutFor(
            "systemctl",
            args => args.SequenceEqual(["--user", "is-active", "mohist-runner.service"]),
            "active\n");
        f.Commands.SetExitCodeFor(
            "npm",
            args => args.SequenceEqual(["run", "build", "-w", "packages/runner"]),
            1);
        var updater = f.BuildUpdater(HealthyServerHandler(), unitDir: UpdateTestFactory.UnitDir);

        var exitCode = await updater.UpdateAllAsync(
            "/clean",
            dryRun: false,
            cliPath: "/home/user/.local/bin/mo",
            continueAfterCliUpdate: true);

        Assert.Equal(1, exitCode);
        Assert.Equal(legacyServerUnit, f.Files.Read(Path.Combine(UpdateTestFactory.UnitDir, "mohist.service")));
        Assert.Equal(runnerUnit, f.Files.Read(Path.Combine(UpdateTestFactory.UnitDir, "mohist-runner.service")));
        Assert.Null(f.Files.ReadDirectorySymbolicLink("/home/test/.local/share/mohist/runtime/server/current"));
        Assert.DoesNotContain(f.Commands.ExecutedCommands, command =>
            command.FileName == "systemctl" && command.Args.SequenceEqual(["--user", "stop", "mohist-runner.service"]));
        Assert.DoesNotContain(f.Commands.ExecutedCommands, command =>
            command.FileName == "systemctl" && command.Args.SequenceEqual(["--user", "start", "mohist-runner.service"]));
        Assert.Contains("Server runtime verification failed", f.Stderr.ToString());
    }

    [Fact]
    public async Task UpdateAll_WhenRunnerIdentityFails_RestoresBothStagedTargetsInReverseOrder()
    {
        const string legacyServerUnit = "[Service]\nWorkingDirectory=/legacy/mohist\nExecStart=dotnet /legacy/mohist/Mohist.Server.dll\n";
        var f = new UpdateTestFactory("/home/test");
        f.Files.AddDirectory(UpdateTestFactory.UnitDir);
        f.Files.AddFile(Path.Combine(UpdateTestFactory.UnitDir, "mohist.service"), legacyServerUnit);
        f.SeedRunnerUnit();
        var runnerUnit = f.Files.Read(Path.Combine(UpdateTestFactory.UnitDir, "mohist-runner.service"));
        f.Runtime.RunnerIdentityTransform = identity => identity with
        {
            BuildGitHash = "old-runner-source",
            ArtifactDigest = "0000000000000000000000000000000000000000000000000000000000000000",
        };
        f.Commands.SetStdoutFor(
            "systemctl",
            args => args.SequenceEqual(["--user", "is-active", "mohist-runner.service"]),
            "active\n");
        var updater = f.BuildUpdater(HealthyServerHandler(), unitDir: UpdateTestFactory.UnitDir);

        var exitCode = await updater.UpdateAllAsync(
            "/clean",
            dryRun: false,
            cliPath: "/home/user/.local/bin/mo",
            continueAfterCliUpdate: true);

        Assert.Equal(1, exitCode);
        Assert.Equal(legacyServerUnit, f.Files.Read(Path.Combine(UpdateTestFactory.UnitDir, "mohist.service")));
        Assert.Equal(runnerUnit, f.Files.Read(Path.Combine(UpdateTestFactory.UnitDir, "mohist-runner.service")));
        Assert.Null(f.Files.ReadDirectorySymbolicLink("/home/test/.local/share/mohist/runtime/server/current"));
        Assert.Null(f.Files.ReadDirectorySymbolicLink("/home/test/.local/share/mohist/runtime/runner/current"));
        Assert.Contains(f.Commands.ExecutedCommands, command =>
            command.FileName == "systemctl" && command.Args.SequenceEqual(["--user", "stop", "mohist-runner.service"]));
        Assert.Contains("Runner runtime verification failed: expected abcdef0, actual old-runner-source", f.Stderr.ToString());
        Assert.Contains("Server runtime verification failed: expected abcdef0, actual <unavailable>; update batch ended with exit 1", f.Stderr.ToString());
        Assert.DoesNotContain("Update complete. Mohist is ready.", f.Stdout.ToString());
    }

    [Fact]
    public async Task UpdateAll_WhenStagedServerRecoveryFails_ReportsTheComponentAndRecoveryFact()
    {
        const string legacyServerUnit = "[Service]\nWorkingDirectory=/legacy/mohist\nExecStart=dotnet /legacy/mohist/Mohist.Server.dll\n";
        var f = new UpdateTestFactory("/home/test");
        f.Files.AddDirectory(UpdateTestFactory.UnitDir);
        f.Files.AddFile(Path.Combine(UpdateTestFactory.UnitDir, "mohist.service"), legacyServerUnit);
        f.SeedRunnerUnit();
        f.Commands.SetStdoutFor(
            "systemctl",
            args => args.SequenceEqual(["--user", "is-active", "mohist-runner.service"]),
            "active\n");
        f.Commands.SetExitCodeFor(
            "npm",
            args => args.SequenceEqual(["run", "build", "-w", "packages/runner"]),
            1);
        f.Files.DirectoryLinkDeleteFailure = link => link.EndsWith("/runtime/server/current", StringComparison.Ordinal)
            ? new IOException("server current link delete denied")
            : null;
        var updater = f.BuildUpdater(HealthyServerHandler(), unitDir: UpdateTestFactory.UnitDir);

        var exitCode = await updater.UpdateAllAsync(
            "/clean",
            dryRun: false,
            cliPath: "/home/user/.local/bin/mo",
            continueAfterCliUpdate: true);

        Assert.Equal(1, exitCode);
        Assert.Contains("Batch recovery failed for Server: service unit was restored but runtime link recovery was not confirmed", f.Stderr.ToString());
        Assert.Contains("Mohist is not fully usable. Unavailable capability: Runtime recovery failed.", f.Stderr.ToString());
    }

    private static SequenceHttpHandler HealthyServerHandler() =>
        new(HttpStatusCode.OK);
}
