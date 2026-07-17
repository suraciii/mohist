using System.Net;
using Mohist.Cli;
using Xunit;

namespace Mohist.Cli.Tests.Update;

public class UpdateCliSpecs
{
    [Fact]
    public async Task UpdateCli_PublishesAndReplacesResolvedMoBinary()
    {
        var tempRoot = "/mohist-tests/mohist-update-cli";
        var f = new UpdateTestFactory(tempRoot);
        f.SeedPackagedSkillAssets();

        var updater = f.BuildUpdater(new SequenceHttpHandler(HttpStatusCode.OK), unitDir: UpdateTestFactory.UnitDir);

        var exitCode = await updater.UpdateCliAsync(tempRoot, dryRun: false);

        var managedCli = Path.Combine(tempRoot, ".local", "share", "mohist", "cli", "mo").Replace('\\', '/');
        var wrapper = Path.Combine(tempRoot, ".local", "bin", "mo").Replace('\\', '/');
        Assert.Equal(0, exitCode);
        Assert.Equal("dotnet", f.Commands.ExecutedCommands[0].FileName);
        Assert.Equal("publish", f.Commands.ExecutedCommands[0].Args[0]);
        Assert.Equal("cp", f.Commands.ExecutedCommands[1].FileName);
        Assert.Equal(managedCli + ".tmp", f.Commands.ExecutedCommands[1].Args[1]);
        Assert.Equal("chmod", f.Commands.ExecutedCommands[2].FileName);
        Assert.Equal("mv", f.Commands.ExecutedCommands[3].FileName);
        Assert.Equal(managedCli, f.Commands.ExecutedCommands[3].Args[1]);
        Assert.Equal("chmod", f.Commands.ExecutedCommands[4].FileName);
        Assert.Equal("+x", f.Commands.ExecutedCommands[4].Args[0]);
        Assert.Equal(wrapper + ".tmp", f.Commands.ExecutedCommands[4].Args[1]);
        Assert.Equal($"#!/bin/sh{Environment.NewLine}exec \"{managedCli}\" \"$@\"{Environment.NewLine}", f.Files.ReadAllText(wrapper));
        f.AssertManagedSkillAssetsSynced();
    }

    [Fact]
    public async Task UpdateCli_WhenPublishFails_PrintsCommandOutput()
    {
        var f = new UpdateTestFactory();
        f.Commands.SetNextResult(1, "publish stdout", "publish stderr");
        var updater = f.BuildUpdater();

        var exitCode = await updater.UpdateCliAsync("/repo", dryRun: false, cliPath: "/home/user/.local/bin/mo");

        Assert.Equal(1, exitCode);
        var output = f.Stderr.ToString();
        Assert.Contains("publish stdout", output);
        Assert.Contains("publish stderr", output);
        Assert.Contains("CLI publish failed", output);
    }
}
