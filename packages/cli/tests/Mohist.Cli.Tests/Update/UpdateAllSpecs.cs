using System.Net;
using Mohist.Cli;
using Xunit;

namespace Mohist.Cli.Tests.Update;

public class UpdateAllSpecs
{
    [Fact]
    public async Task UpdateAll_UpdatesCliThenContinuesWithRefreshedProcess()
    {
        var tempRoot = "/mohist-tests/mohist-update-all";
        var f = new UpdateTestFactory(tempRoot);
        f.SeedPackagedSkillAssets();
        f.SeedRunnerUnit();

        f.Commands.SetStdoutFor(
            "/home/user/.local/bin/mo",
            args => args.SequenceEqual([
                "update",
                "--continue-after-cli-update",
                "--cli-path",
                "/home/user/.local/bin/mo",
                "--repo-root",
                tempRoot,
            ]),
            "continued update output\n");
        var updater = f.BuildUpdater(
            SequenceHttpHandler.WithSystemInfo(UpdateTestFactory.HealthySystemInfoJson(runningGitHash: "testsha"), new ResponseSpec(HttpStatusCode.OK)),
            unitDir: UpdateTestFactory.UnitDir);

        var exitCode = await updater.UpdateAllAsync(tempRoot, dryRun: false, cliPath: "/home/user/.local/bin/mo");

        var explicitCli = "/home/user/.local/bin/mo";
        var wrapper = Path.Combine(tempRoot, ".local", "bin", "mo").Replace('\\', '/');
        Assert.Equal(0, exitCode);
        Assert.Equal("dotnet", f.Commands.ExecutedCommands[0].FileName);
        Assert.Equal("publish", f.Commands.ExecutedCommands[0].Args[0]);
        Assert.Equal("cp", f.Commands.ExecutedCommands[1].FileName);
        Assert.Equal(explicitCli + ".tmp", f.Commands.ExecutedCommands[1].Args[1]);
        Assert.Equal("chmod", f.Commands.ExecutedCommands[2].FileName);
        Assert.Equal("mv", f.Commands.ExecutedCommands[3].FileName);
        Assert.Equal(explicitCli, f.Commands.ExecutedCommands[3].Args[1]);
        Assert.DoesNotContain(f.Commands.ExecutedCommands, c => c.FileName == "chmod" && c.Args.SequenceEqual(["+x", wrapper]));
        Assert.Contains(f.Commands.ExecutedCommands, c =>
            c.FileName == explicitCli
            && c.WorkingDirectory == tempRoot
            && c.Args.SequenceEqual([
                "update",
                "--continue-after-cli-update",
                "--cli-path",
                explicitCli,
                "--repo-root",
                tempRoot,
            ]));
        Assert.DoesNotContain(f.Commands.ExecutedCommands, c =>
            c.FileName == "systemctl" && c.Args.SequenceEqual(["--user", "stop", "mohist-runner.service"]));
        Assert.DoesNotContain(f.Commands.ExecutedCommands, c =>
            c.FileName == "dotnet" && c.Args.SequenceEqual(["build", "Mohist.sln"]));
        Assert.DoesNotContain(f.Commands.ExecutedCommands, c => c.FileName == "npm");
        Assert.DoesNotContain(f.Commands.ExecutedCommands, c => c.FileName == "git" && c.Args.SequenceEqual(["pull"]));
        f.AssertManagedSkillAssetsSynced();
    }

    [Fact]
    public async Task UpdateAll_WhenContinuingAfterCliUpdate_UpdatesServerAndRunnerWithoutPulling()
    {
        var tempRoot = "/mohist-tests/mohist-update-all-continue";
        var f = new UpdateTestFactory(tempRoot);
        f.SeedPackagedSkillAssets();
        f.SeedRunnerUnit();

        f.Commands.SetStdoutFor("systemctl", args => args.Length >= 3 && args[1] == "is-active", "active\n");
        f.Commands.SetStdoutFor("/home/user/.local/bin/mo", _ => true, "1.0.0+testsha");
        f.Commands.SetStdoutFor("git", _ => true, "testsha");
        var updater = f.BuildUpdater(
            SequenceHttpHandler.WithSystemInfo(UpdateTestFactory.HealthySystemInfoJson(runningGitHash: "testsha"), new ResponseSpec(HttpStatusCode.OK)),
            unitDir: UpdateTestFactory.UnitDir);

        var exitCode = await updater.UpdateAllAsync(
            tempRoot,
            dryRun: false,
            cliPath: "/home/user/.local/bin/mo",
            continueAfterCliUpdate: true);

        Assert.Equal(0, exitCode);
        Assert.Contains(f.Commands.ExecutedCommands, c =>
            c.FileName == "dotnet" && c.Args.Length > 0 && c.Args[0] == "publish");
        Assert.Contains(f.Commands.ExecutedCommands, c =>
            c.FileName == "systemctl" && c.Args.SequenceEqual(["--user", "is-active", "mohist-runner.service"]));
        Assert.Contains(f.Commands.ExecutedCommands, c =>
            c.FileName == "systemctl" && c.Args.SequenceEqual(["--user", "stop", "mohist-runner.service"]));
        Assert.Contains(f.Commands.ExecutedCommands, c =>
            c.FileName == "dotnet" && c.Args.Length > 0 && c.Args[0] == "publish");
        Assert.Contains(f.Commands.ExecutedCommands, c =>
            c.FileName == "systemctl" && c.Args.SequenceEqual(["--user", "restart", "mohist.service"]));
        Assert.Contains(f.Commands.ExecutedCommands, c => c.FileName == "npm");
        Assert.Contains(f.Commands.ExecutedCommands, c =>
            c.FileName == "systemctl" && c.Args.SequenceEqual(["--user", "restart", "mohist-runner.service"]));
        Assert.Contains(f.Commands.ExecutedCommands, c =>
            c.FileName == "git" && c.Args.SequenceEqual(["rev-parse", "HEAD"]));
        Assert.DoesNotContain(f.Commands.ExecutedCommands, c => c.FileName == "git" && c.Args.SequenceEqual(["pull"]));
    }

    [Fact]
    public async Task UpdateAll_WhenRunnerNotInstalled_SkipsRunnerRefreshAfterServerUpdate()
    {
        var tempRoot = "/mohist-tests/mohist-update-all-no-runner";
        var f = new UpdateTestFactory(tempRoot);
        f.SeedPackagedSkillAssets();

        var updater = f.BuildUpdater(new SequenceHttpHandler(HttpStatusCode.OK));

        var exitCode = await updater.UpdateAllAsync(tempRoot, dryRun: false, cliPath: "/home/user/.local/bin/mo", continueAfterCliUpdate: true);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain(f.Commands.ExecutedCommands, c =>
            c.FileName == "systemctl" && c.Args.SequenceEqual(["--user", "stop", "mohist-runner.service"]));
        Assert.Contains(f.Commands.ExecutedCommands, c =>
            c.FileName == "dotnet" && c.Args.Length > 0 && c.Args[0] == "publish");
        Assert.DoesNotContain(f.Commands.ExecutedCommands, c => c.FileName == "npm");
        var output = f.Stdout.ToString();
        Assert.Contains("Runner service is not installed; skipping managed runner candidate preparation.", output);
        Assert.Contains("Runner refresh skipped: runner service is not installed", output);
        Assert.Contains("runner-refresh-skipped(runner service is not installed)", output);
    }
}
