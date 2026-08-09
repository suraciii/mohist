using System.Net;
using Mohist.Cli;
using Xunit;

namespace Mohist.Cli.Tests.Update;

public class UpdateRunnerSpecs
{
    [Fact]
    public async Task UpdateRunner_GeneratesAndPersistsOpaqueRunnerId()
    {
        var f = new UpdateTestFactory(root: "/home/test");
        f.SeedRunnerUnit();
        var updater = f.BuildUpdater(
            new SequenceHttpHandler(HttpStatusCode.OK),
            unitDir: UpdateTestFactory.UnitDir);

        Assert.Equal(0, await updater.UpdateRunnerAsync("/clean", dryRun: false));
        var firstUnit = f.Files.Read(Path.Combine(UpdateTestFactory.UnitDir, "mohist-runner.service"));
        var first = RunnerId(firstUnit);
        var firstGeneration = EnvironmentValue(firstUnit, "MOHIST_RUNTIME_GENERATION");
        var firstSessionToken = EnvironmentValue(firstUnit, "MOHIST_RUNTIME_SESSION_TOKEN");

        Assert.Equal(0, await updater.UpdateRunnerAsync("/clean", dryRun: false));
        var secondUnit = f.Files.Read(Path.Combine(UpdateTestFactory.UnitDir, "mohist-runner.service"));
        var second = RunnerId(secondUnit);
        var secondGeneration = EnvironmentValue(secondUnit, "MOHIST_RUNTIME_GENERATION");
        var secondSessionToken = EnvironmentValue(secondUnit, "MOHIST_RUNTIME_SESSION_TOKEN");

        Assert.Matches("^runner-[0-9a-f]{32}$", first);
        Assert.Equal(first, second);
        Assert.Equal("1", firstGeneration);
        Assert.Equal("2", secondGeneration);
        Assert.NotEqual(firstSessionToken, secondSessionToken);
    }

    [Fact]
    public async Task UpdateRunner_InstallsSourceHashUnderStableRootAndUsesAbsoluteServiceTarget()
    {
        var f = new UpdateTestFactory(root: "/home/test");
        f.SeedRunnerUnit();
        f.Files.AddFile(
            Path.Combine(UpdateTestFactory.UnitDir, "mohist-runner.service"),
            "[Service]\nEnvironment=\"SERVER_URL=http://127.0.0.1:4567\"\nEnvironment=\"RUNNER_ROOT=/runner-data\"\nExecStart=node packages/runner/dist/cli.js\n");
        var updater = f.BuildUpdater(
            new SequenceHttpHandler(HttpStatusCode.OK),
            unitDir: UpdateTestFactory.UnitDir);

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
        Assert.Contains("Environment=\"RUNNER_ID=", unit);
        Assert.Contains("Environment=\"MOHIST_RUNTIME_GENERATION=", unit);
        Assert.Contains("Environment=\"MOHIST_RUNTIME_SESSION_TOKEN=", unit);
        Assert.Contains("Environment=\"MOHIST_ARTIFACT_DIGEST=", unit);
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
            new SequenceHttpHandler(HttpStatusCode.OK),
            unitDir: UpdateTestFactory.UnitDir);
        Assert.Equal(0, await initial.UpdateRunnerAsync("/clean", dryRun: false));
        f.Runtime.FreezeRunnerIdentity();
        f.ClearOutput();

        const string candidate = "0123456789abcdef0123456789abcdef01234567";
        f.Commands.SetResultFor("git", args => args.SequenceEqual(["rev-parse", "HEAD"]), 0, candidate + "\n", "");
        var stale = f.BuildUpdater(
            new SequenceHttpHandler(HttpStatusCode.OK),
            unitDir: UpdateTestFactory.UnitDir);

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
    public async Task UpdateRunner_WithoutVerifiedVersion_RestoresLegacyLocalSourceUnitAndDoesNotReportSuccess()
    {
        var f = new UpdateTestFactory(root: "/home/test");
        f.SeedRunnerUnit();
        const string source = "0123456789abcdef0123456789abcdef01234567";
        const string stale = "fedcba9876543210fedcba9876543210fedcba98";
        f.Commands.SetResultFor("git", args => args.SequenceEqual(["rev-parse", "HEAD"]), 0, source + "\n", "");
        f.Runtime.RunnerIdentityTransform = runtime => runtime with { BuildGitHash = stale };
        var updater = f.BuildUpdater(
            new SequenceHttpHandler(HttpStatusCode.OK),
            unitDir: UpdateTestFactory.UnitDir);

        var exitCode = await updater.UpdateRunnerAsync("/clean", dryRun: false);

        Assert.Equal(1, exitCode);
        Assert.Null(f.Files.ReadDirectorySymbolicLink("/home/test/.local/share/mohist/runtime/runner/current"));
        Assert.Contains(f.Commands.ExecutedCommands, command =>
            command.FileName == "systemctl" && command.Args.SequenceEqual(["--user", "restart", "mohist-runner.service"]));
        Assert.Equal(
            "[Unit]\nDescription=Mohist Runner\n\n[Service]\nExecStart=node packages/runner/dist/cli.js\n\n[Install]\nWantedBy=default.target\n",
            f.Files.Read(Path.Combine(UpdateTestFactory.UnitDir, "mohist-runner.service")));
        Assert.Contains("Recovery: restored prior local-source service target with no verified runtime version", f.Stderr.ToString());
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

    private static string RunnerId(string unit)
    {
        return EnvironmentValue(unit, "RUNNER_ID");
    }

    private static string EnvironmentValue(string unit, string name)
    {
        var prefix = $"Environment=\"{name}=";
        var line = unit.Split('\n').Single(candidate => candidate.StartsWith(prefix, StringComparison.Ordinal));
        return line[prefix.Length..].TrimEnd('\"');
    }
}
