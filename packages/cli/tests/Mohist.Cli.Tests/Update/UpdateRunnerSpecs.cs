using System.Net;
using Mohist.Cli;
using Xunit;

namespace Mohist.Cli.Tests.Update;

public class UpdateRunnerSpecs
{
    [Fact]
    public async Task UpdateRunner_InstallsSourceHashUnderStableRootAndUsesAbsoluteServiceTarget()
    {
        var f = new UpdateTestFactory(root: "/home/test");
        f.SeedRunnerUnit();
        f.Files.AddFile(
            Path.Combine(UpdateTestFactory.UnitDir, "mohist-runner.service"),
            "[Service]\nEnvironment=\"SERVER_URL=http://127.0.0.1:4567\"\nEnvironment=\"RUNNER_ROOT=/runner-data\"\nExecStart=node packages/runner/dist/cli.js\n");
        var identity = UpdateTestFactory.BuildRunnerIdentityResponse("runner-1", "test-host", "abcdef0", "online");
        var updater = f.BuildUpdater(
            new SequenceHttpHandler(new ResponseSpec(HttpStatusCode.OK, identity, "application/json")),
            unitDir: UpdateTestFactory.UnitDir,
            getLocalHostname: () => "test-host");

        var exitCode = await updater.UpdateRunnerAsync("/clean", dryRun: false);

        Assert.Equal(0, exitCode);
        var versionRoot = "/home/test/.local/share/mohist/runtime/runner/versions/abcdef0";
        Assert.True(f.Files.HasFile(Path.Combine(versionRoot, "mohist-build.json")));
        Assert.Equal(versionRoot, f.Files.ReadDirectorySymbolicLink("/home/test/.local/share/mohist/runtime/runner/current"));
        Assert.Equal(versionRoot, f.Files.ReadDirectorySymbolicLink("/home/test/.local/share/mohist/runtime/runner/verified"));
        var unit = f.Files.Read(Path.Combine(UpdateTestFactory.UnitDir, "mohist-runner.service"));
        Assert.Contains("WorkingDirectory=/home/test/.local/share/mohist/runtime/runner", unit);
        Assert.Contains("/home/test/.local/share/mohist/runtime/runner/current/dist/cli.js", unit);
        Assert.DoesNotContain("/clean", unit);
        Assert.Contains("Environment=\"SERVER_URL=http://127.0.0.1:4567\"", unit);
        Assert.Contains("Environment=\"RUNNER_ROOT=/runner-data\"", unit);
        Assert.Contains(f.Commands.ExecutedCommands, command =>
            command.FileName == "npm" && command.Args.SequenceEqual(["run", "build", "-w", "packages/runner"]));
        Assert.Contains("Runner update is verified and current.", f.Stdout.ToString());
    }

    [Fact]
    public async Task UpdateRunner_WhenIdentityDiffers_RestoresVerifiedVersionAndReportsBothHashes()
    {
        var f = new UpdateTestFactory(root: "/home/test");
        f.SeedRunnerUnit();
        var initial = f.BuildUpdater(
            new SequenceHttpHandler(new ResponseSpec(
                HttpStatusCode.OK,
                UpdateTestFactory.BuildRunnerIdentityResponse("runner-1", "test-host", "abcdef0", "online"),
                "application/json")),
            unitDir: UpdateTestFactory.UnitDir,
            getLocalHostname: () => "test-host");
        Assert.Equal(0, await initial.UpdateRunnerAsync("/clean", dryRun: false));
        f.ClearOutput();

        const string candidate = "0123456789abcdef0123456789abcdef01234567";
        f.Commands.SetResultFor("git", args => args.SequenceEqual(["rev-parse", "HEAD"]), 0, candidate + "\n", "");
        var stale = f.BuildUpdater(
            new SequenceHttpHandler(new ResponseSpec(
                HttpStatusCode.OK,
                UpdateTestFactory.BuildRunnerIdentityResponse("runner-1", "test-host", "abcdef0", "online"),
                "application/json")),
            unitDir: UpdateTestFactory.UnitDir,
            getLocalHostname: () => "test-host");

        var exitCode = await stale.UpdateRunnerAsync("/clean", dryRun: false);

        Assert.Equal(1, exitCode);
        Assert.Equal(
            "/home/test/.local/share/mohist/runtime/runner/versions/abcdef0",
            f.Files.ReadDirectorySymbolicLink("/home/test/.local/share/mohist/runtime/runner/current"));
        var error = f.Stderr.ToString();
        Assert.Contains($"expected {candidate}, actual abcdef0", error);
        Assert.Contains("Recovery: restored verified version abcdef0", error);
        Assert.DoesNotContain("Runner update is verified and current.", f.Stdout.ToString());
    }

    [Fact]
    public async Task UpdateRunner_WithoutVerifiedVersion_StopsUnverifiedCandidateAndDoesNotReportSuccess()
    {
        var f = new UpdateTestFactory(root: "/home/test");
        f.SeedRunnerUnit();
        const string source = "0123456789abcdef0123456789abcdef01234567";
        const string stale = "fedcba9876543210fedcba9876543210fedcba98";
        f.Commands.SetResultFor("git", args => args.SequenceEqual(["rev-parse", "HEAD"]), 0, source + "\n", "");
        var updater = f.BuildUpdater(
            new SequenceHttpHandler(new ResponseSpec(
                HttpStatusCode.OK,
                UpdateTestFactory.BuildRunnerIdentityResponse("runner-1", "test-host", stale, "online"),
                "application/json")),
            unitDir: UpdateTestFactory.UnitDir,
            getLocalHostname: () => "test-host");

        var exitCode = await updater.UpdateRunnerAsync("/clean", dryRun: false);

        Assert.Equal(1, exitCode);
        Assert.Null(f.Files.ReadDirectorySymbolicLink("/home/test/.local/share/mohist/runtime/runner/current"));
        Assert.Contains(f.Commands.ExecutedCommands, command =>
            command.FileName == "systemctl" && command.Args.SequenceEqual(["--user", "stop", "mohist-runner.service"]));
        Assert.Contains("Recovery: no verified version existed, stopped the candidate service target", f.Stderr.ToString());
        Assert.DoesNotContain("Runner update is verified and current.", f.Stdout.ToString());
    }

    [Fact]
    public async Task UpdateRunner_WhenNotInstalled_SkipsBeforeResolvingSource()
    {
        var f = new UpdateTestFactory(root: "/home/test");
        var updater = f.BuildUpdater(unitDir: UpdateTestFactory.UnitDir);

        var exitCode = await updater.UpdateRunnerAsync("/clean", dryRun: false);

        Assert.Equal(0, exitCode);
        Assert.Empty(f.Commands.ExecutedCommands);
        Assert.Contains("runner-refresh-skipped(runner service is not installed)", f.Stdout.ToString());
    }
}
