using System.Text.Json;
using Mohist.Cli;
using Xunit;

namespace Mohist.Cli.Tests.Update;

public sealed class ManagedCliLauncherSpecs
{
    private const string SourceRevision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task UpdateCli_DefaultStableLauncher_MigratesDirectBinaryAndCommitsCandidateIdentity()
    {
        var fixture = CreateFixture();
        var launcherPath = UpdateOperations.ResolveCliWrapperPath("/home/test");
        fixture.Files.AddFile(launcherPath, "ELF old CLI");
        ConfigureLauncherVersion(fixture, launcherPath, SourceRevision);

        var exitCode = await fixture.BuildManagedServerUpdater(new SequenceHttpHandler(System.Net.HttpStatusCode.OK))
            .UpdateCliAsync("/repo", dryRun: false);

        var verified = Parse(fixture.Files.Read(fixture.VerifiedPath));
        var cli = Assert.IsType<RuntimeTarget>(verified.Cli);
        var backupPath = BackupPath(fixture, verified.TransactionId);
        Assert.Equal(0, exitCode);
        Assert.Equal(SourceRevision, cli.Identity.SourceRevision);
        Assert.Equal(
            $"#!/bin/sh{Environment.NewLine}exec \"{cli.Entrypoint}\" \"$@\"{Environment.NewLine}",
            fixture.Files.Read(launcherPath));
        Assert.False(fixture.Files.HasFile(backupPath));
    }

    [Fact]
    public async Task UpdateCli_ExplicitDirectBinary_MigratesTheSpecifiedStableEntry()
    {
        var fixture = CreateFixture();
        const string launcherPath = "/home/test/legacy/mo";
        fixture.Files.AddFile(launcherPath, "ELF direct CLI");
        ConfigureLauncherVersion(fixture, launcherPath, SourceRevision);

        var exitCode = await fixture.BuildManagedServerUpdater(new SequenceHttpHandler(System.Net.HttpStatusCode.OK))
            .UpdateCliAsync("/repo", dryRun: false, cliPath: launcherPath);

        var verified = Parse(fixture.Files.Read(fixture.VerifiedPath));
        var cli = Assert.IsType<RuntimeTarget>(verified.Cli);
        Assert.Equal(0, exitCode);
        Assert.Equal(
            $"#!/bin/sh{Environment.NewLine}exec \"{cli.Entrypoint}\" \"$@\"{Environment.NewLine}",
            fixture.Files.Read(launcherPath));
        Assert.False(fixture.Files.HasFile(BackupPath(fixture, verified.TransactionId)));
    }

    [Theory]
    [InlineData("/home/test/custom/mo")]
    [InlineData("custom/mo")]
    public async Task UpdateCli_InvalidExplicitPath_FailsClosedBeforeCandidateMutation(string launcherPath)
    {
        var fixture = CreateFixture();
        var error = new StringWriter();

        var exitCode = await fixture.BuildManagedServerUpdater(
                new SequenceHttpHandler(System.Net.HttpStatusCode.OK),
                error: error)
            .UpdateCliAsync("/repo", dryRun: false, cliPath: launcherPath);

        Assert.Equal(1, exitCode);
        Assert.Contains("existing absolute mo entrypoint", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("bash scripts/install-mo.sh", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Commands.ExecutedCommands, command => command.FileName is "dotnet" or "npm" or "git");
        Assert.False(fixture.Files.HasFile(fixture.ActivePath));
        Assert.False(fixture.Files.HasFile(fixture.VerifiedPath));
    }

    [Fact]
    public async Task UpdateCli_WhenLauncherIdentityDoesNotMatch_RestoresDirectBinaryAndActivePointer()
    {
        var fixture = CreateFixture();
        const string launcherPath = "/home/test/legacy/mo";
        fixture.Files.AddFile(launcherPath, "ELF direct CLI");
        ConfigureLauncherVersion(fixture, launcherPath, "oldrevision");

        var exitCode = await fixture.BuildManagedServerUpdater(new SequenceHttpHandler(System.Net.HttpStatusCode.OK))
            .UpdateCliAsync("/repo", dryRun: false, cliPath: launcherPath);

        Assert.Equal(1, exitCode);
        Assert.Equal("ELF direct CLI", fixture.Files.Read(launcherPath));
        Assert.Equal("none", Parse(fixture.Files.Read(fixture.ActivePath)).Status);
        Assert.False(fixture.Files.HasFile(fixture.VerifiedPath));
    }

    [Fact]
    public async Task UpdateCli_WhenVerifiedPointerCommitFails_RestoresDirectBinaryAndActivePointer()
    {
        var fixture = CreateFixture();
        const string launcherPath = "/home/test/legacy/mo";
        fixture.Files.AddFile(launcherPath, "ELF direct CLI");
        ConfigureLauncherVersion(fixture, launcherPath, SourceRevision);
        fixture.Files.FailNextMoveTo = path => path.EndsWith("/verified.json", StringComparison.Ordinal);

        var exitCode = await fixture.BuildManagedServerUpdater(new SequenceHttpHandler(System.Net.HttpStatusCode.OK))
            .UpdateCliAsync("/repo", dryRun: false, cliPath: launcherPath);

        Assert.Equal(1, exitCode);
        Assert.Null(fixture.Files.FailNextMoveTo);
        Assert.Equal("ELF direct CLI", fixture.Files.Read(launcherPath));
        Assert.Equal("none", Parse(fixture.Files.Read(fixture.ActivePath)).Status);
        Assert.False(fixture.Files.HasFile(fixture.VerifiedPath));
        Assert.DoesNotContain(fixture.Files.Files.Keys, path => path.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpdateCli_WhenLauncherBackupFinalizeFails_RestoresDirectBinaryAndBothPointers()
    {
        var fixture = CreateFixture();
        const string launcherPath = "/home/test/legacy/mo";
        fixture.Files.AddFile(launcherPath, "ELF direct CLI");
        ConfigureLauncherVersion(fixture, launcherPath, SourceRevision);
        fixture.Files.FailNextDelete = path => path.EndsWith("/cli-launcher.previous", StringComparison.Ordinal);

        var exitCode = await fixture.BuildManagedServerUpdater(new SequenceHttpHandler(System.Net.HttpStatusCode.OK))
            .UpdateCliAsync("/repo", dryRun: false, cliPath: launcherPath);

        Assert.Equal(1, exitCode);
        Assert.Null(fixture.Files.FailNextDelete);
        Assert.Equal("ELF direct CLI", fixture.Files.Read(launcherPath));
        Assert.Equal("none", Parse(fixture.Files.Read(fixture.ActivePath)).Status);
        Assert.False(fixture.Files.HasFile(fixture.VerifiedPath));
    }

    [Fact]
    public async Task UpdateCli_WhenLauncherBackupFinalizeFails_RestoresPreviousVerifiedCliPointer()
    {
        var fixture = CreateFixture();
        const string launcherPath = "/home/test/legacy/mo";
        var previous = new RuntimeTargetSet(
            "verified",
            2,
            "tx-previous",
            new RuntimeTarget(
                "cli",
                "/managed/cli/Mohist.Cli",
                "/managed/cli",
                [],
                "linux-x64",
                new RuntimeIdentity(
                    "cli",
                    "0.0.0+previous",
                    "previous",
                    "previous-tree",
                    "previous-artifact",
                    "mohist-cli-previous",
                    2)),
            null,
            null,
            null);
        var previousJson = JsonSerializer.Serialize(previous, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        fixture.Files.AddFile(fixture.ActivePath, previousJson);
        fixture.Files.AddFile(fixture.VerifiedPath, previousJson);
        fixture.Files.AddFile(launcherPath, "ELF direct CLI");
        ConfigureLauncherVersion(fixture, launcherPath, SourceRevision);
        fixture.Files.FailNextDelete = path => path.EndsWith("/cli-launcher.previous", StringComparison.Ordinal);

        var exitCode = await fixture.BuildManagedServerUpdater(new SequenceHttpHandler(System.Net.HttpStatusCode.OK))
            .UpdateCliAsync("/repo", dryRun: false, cliPath: launcherPath);

        Assert.Equal(1, exitCode);
        Assert.Equal("ELF direct CLI", fixture.Files.Read(launcherPath));
        Assert.Equal(previous.Cli!.Identity, Parse(fixture.Files.Read(fixture.ActivePath)).Cli!.Identity);
        Assert.Equal(previous.Cli.Identity, Parse(fixture.Files.Read(fixture.VerifiedPath)).Cli!.Identity);
    }

    [Fact]
    public async Task PrepareCli_WhenLauncherActivationFails_RestoresDirectBinaryAndPointers()
    {
        var fixture = ManagedRuntimeTransactionSpecs.ManagedFixture.Create(activationCode: 0);
        const string launcherPath = "/home/test/legacy/mo";
        fixture.Files.AddFile(launcherPath, "ELF direct CLI");
        fixture.Commands.SetResultFor(
            "chmod",
            args => args.SequenceEqual(["+x", $"{launcherPath}.tmp"]),
            17,
            "",
            "chmod failed");

        var prepared = await fixture.Transaction.PrepareAsync(
            "/repo",
            "cli",
            "tx-cli-launcher-failed",
            launcherPath);

        Assert.Null(prepared.Session);
        Assert.Contains("chmod failed", prepared.Error);
        Assert.Equal("ELF direct CLI", fixture.Files.Read(launcherPath));
        Assert.Equal("none", Parse(fixture.Files.Read(fixture.ActivePath)).Status);
        Assert.False(fixture.Files.HasFile(fixture.VerifiedPath));
        Assert.Equal(1, fixture.Activator.RestoreCalls);
    }

    [Fact]
    public async Task LauncherActivation_SameCandidateIdentityIsIdempotent()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        var launcher = new ManagedCliLauncher(TextWriter.Null, TextWriter.Null, commands, files);
        var identity = new RuntimeIdentity(
            "cli",
            $"0.0.0+{SourceRevision}",
            SourceRevision,
            new string('b', 40),
            new string('c', 64),
            "mohist-cli-candidate",
            1);
        const string launcherPath = "/home/test/.local/bin/mo";
        const string candidatePath = "/home/test/.local/share/mohist/runtime/releases/cli/Mohist.Cli";
        files.AddFile(Path.Combine(Path.GetDirectoryName(candidatePath)!, "runtime-identity.json"), identity.ToJson());

        var first = await launcher.ActivateAsync(launcherPath, candidatePath, identity, "/home/test/runtime/first.previous");
        var second = await launcher.ActivateAsync(launcherPath, candidatePath, identity, "/home/test/runtime/second.previous");

        Assert.True(first.State!.Changed);
        Assert.False(second.State!.Changed);
        Assert.Single(commands.ExecutedCommands, command => command.FileName == "chmod");
        Assert.Contains(candidatePath, files.Read(launcherPath));
    }

    private static ManagedRuntimeTransactionSpecs.ManagedFixture CreateFixture() =>
        ManagedRuntimeTransactionSpecs.ManagedFixture.Create(
            activationCode: 0,
            useSystemd: true,
            unitDir: UpdateTestFactory.UnitDir);

    private static void ConfigureLauncherVersion(
        ManagedRuntimeTransactionSpecs.ManagedFixture fixture,
        string launcherPath,
        string sourceRevision)
    {
        fixture.Commands.SetResultFor(
            launcherPath,
            args => args.SequenceEqual(["--version"]),
            0,
            $"0.0.0+{sourceRevision}{Environment.NewLine}",
            "");
    }

    private static string BackupPath(
        ManagedRuntimeTransactionSpecs.ManagedFixture fixture,
        string transactionId) =>
        Path.Combine(fixture.RuntimeRoot, "transactions", transactionId, "cli-launcher.previous").Replace('\\', '/');

    private static RuntimeTargetSet Parse(string json) =>
        JsonSerializer.Deserialize<RuntimeTargetSet>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new InvalidOperationException("managed runtime target set was not persisted");
}
