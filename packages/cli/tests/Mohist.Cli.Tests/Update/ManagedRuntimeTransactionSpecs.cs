using System.Net;
using EnvironmentAbstractions.TestHelpers;
using Mohist.Cli;
using Mohist.Cli.Tests.Support;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Mohist.Cli.Tests.Update;

public sealed partial class ManagedRuntimeTransactionSpecs
{
    [Fact]
    public void RuntimeIdentity_MatchesArtifactAndGenerationExactly()
    {
        var expected = Identity("server", generation: 4);

        Assert.True(expected.Matches(expected));
        Assert.False(expected.Matches(expected with { ArtifactDigest = new string('f', 64) }));
        Assert.False(expected.Matches(expected with { Generation = 5 }));
        Assert.False(expected.Matches(expected with { ReleaseId = "other-release" }));
    }

    [Fact]
    public async Task PrepareAndCommit_PublishesCandidateBeforeVerifiedPointer()
    {
        var fixture = ManagedFixture.Create(activationCode: 0);

        var prepared = await fixture.Transaction.PrepareAsync(
            "/repo",
            "server",
            "tx-server",
            null);

        Assert.NotNull(prepared.Session);
        var session = prepared.Session!;
        Assert.False(fixture.Files.HasFile(fixture.VerifiedPath));
        Assert.Equal(1, fixture.Activator.ApplyCalls);
        Assert.Equal("server", session.Targets.Server!.Component);
        Assert.StartsWith("0.0.0+", session.Targets.Server.Identity.Version, StringComparison.Ordinal);
        Assert.DoesNotContain("/repo", session.ReleaseRoot, StringComparison.Ordinal);

        Assert.Equal(0, await fixture.Transaction.CommitAsync(session));
        var verified = Parse(fixture.Files.Read(fixture.VerifiedPath));
        Assert.Equal("verified", verified.Status);
        Assert.Equal(session.Targets.Generation, verified.Generation);
    }

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

    [Fact]
    public async Task Prepare_BuildsFromWritableWorkspaceWithoutWritingToReadOnlySnapshot()
    {
        var fixture = ManagedFixture.Create(activationCode: 0);

        var prepared = await fixture.Transaction.PrepareAsync(
            "/repo",
            "server",
            "tx-read-only-snapshot",
            null);

        Assert.NotNull(prepared.Session);
        var publish = Assert.Single(fixture.Commands.ExecutedCommands, command =>
            command.FileName == "dotnet" && command.Args.Contains("publish"));
        var snapshotRoot = Path.Combine(
            fixture.RuntimeRoot,
            "transactions",
            "tx-read-only-snapshot",
            "snapshot").Replace('\\', '/');
        var buildRoot = Path.Combine(
            fixture.RuntimeRoot,
            "transactions",
            "tx-read-only-snapshot",
            "build",
            "source").Replace('\\', '/');

        Assert.StartsWith(buildRoot + "/", publish.Args[1], StringComparison.Ordinal);
        Assert.Equal(buildRoot, publish.WorkingDirectory);
        Assert.False(fixture.Files.DirectoryExists(Path.Combine(snapshotRoot, "packages", "server", "src", "Mohist.Server", "obj")));
        Assert.True(fixture.Files.DirectoryExists(Path.Combine(buildRoot, "packages", "server", "src", "Mohist.Server", "obj")));
    }

    [Fact]
    public async Task PrepareServer_PreparesNodeDependenciesOnceBeforePublish()
    {
        var fixture = ManagedFixture.Create(activationCode: 0);

        var prepared = await fixture.Transaction.PrepareAsync(
            "/repo",
            "server",
            "tx-server-node-deps",
            null);

        Assert.NotNull(prepared.Session);
        var npm = Assert.Single(fixture.Commands.ExecutedCommands, command => command.FileName == "npm");
        Assert.Equal(["ci", "--include=dev"], npm.Args);
        Assert.Equal(
            Path.Combine(fixture.RuntimeRoot, "transactions", "tx-server-node-deps", "build", "source").Replace('\\', '/'),
            npm.WorkingDirectory);

        var npmIndex = fixture.Commands.ExecutedCommands.IndexOf(npm);
        var publishIndex = fixture.Commands.ExecutedCommands.FindIndex(command =>
            command.FileName == "dotnet" && command.Args.Contains("publish"));
        Assert.True(npmIndex >= 0 && npmIndex < publishIndex);
    }

    [Fact]
    public async Task PrepareFull_PreparesNodeDependenciesOnceBeforeEveryBuild()
    {
        var fixture = ManagedFixture.Create(activationCode: 0);

        var prepared = await fixture.Transaction.PrepareAsync(
            "/repo",
            "full",
            "tx-full-node-deps",
            null);

        Assert.NotNull(prepared.Session);
        var npmCi = Assert.Single(fixture.Commands.ExecutedCommands, command =>
            command.FileName == "npm" && command.Args.SequenceEqual(["ci", "--include=dev"]));
        var npmCiIndex = fixture.Commands.ExecutedCommands.IndexOf(npmCi);
        var cliPublishIndex = fixture.Commands.ExecutedCommands.FindIndex(command =>
            command.FileName == "dotnet"
            && command.Args.Any(argument => argument.EndsWith(
                Path.Combine("packages", "cli", "Mohist.Cli", "Mohist.Cli.csproj"),
                StringComparison.Ordinal)));
        var serverPublishIndex = fixture.Commands.ExecutedCommands.FindIndex(command =>
            command.FileName == "dotnet"
            && command.Args.Any(argument => argument.EndsWith(
                Path.Combine("packages", "server", "src", "Mohist.Server", "Mohist.Server.csproj"),
                StringComparison.Ordinal)));
        var runnerBuildIndex = fixture.Commands.ExecutedCommands.FindIndex(command =>
            command.FileName == "npm"
            && command.Args.SequenceEqual(["run", "build", "-w", "packages/runner"]));

        Assert.True(npmCiIndex >= 0);
        Assert.True(cliPublishIndex > npmCiIndex);
        Assert.True(serverPublishIndex > npmCiIndex);
        Assert.True(runnerBuildIndex > npmCiIndex);
        Assert.Equal(RuntimeLaunchMode.Node, fixture.LastPreparedTargets!.Runner!.LaunchMode);
        Assert.Equal(1, fixture.Activator.ApplyCalls);
    }

    [Fact]
    public async Task PrepareRunner_PreparesNodeDependenciesOnceBeforeBuild()
    {
        var fixture = ManagedFixture.Create(activationCode: 0);

        var prepared = await fixture.Transaction.PrepareAsync(
            "/repo",
            "runner",
            "tx-runner-node-deps",
            null);

        Assert.NotNull(prepared.Session);
        var npmCi = Assert.Single(fixture.Commands.ExecutedCommands, command =>
            command.FileName == "npm" && command.Args.SequenceEqual(["ci", "--include=dev"]));
        var runnerBuild = Assert.Single(fixture.Commands.ExecutedCommands, command =>
            command.FileName == "npm" && command.Args.SequenceEqual(["run", "build", "-w", "packages/runner"]));
        Assert.Equal(2, fixture.Commands.ExecutedCommands.Count(command => command.FileName == "npm"));
        Assert.True(
            fixture.Commands.ExecutedCommands.IndexOf(npmCi)
            < fixture.Commands.ExecutedCommands.IndexOf(runnerBuild));
        Assert.DoesNotContain(fixture.Commands.ExecutedCommands, command => command.FileName == "dotnet");
    }

    [Fact]
    public async Task PrepareRunner_PublishesSingleLayerCanonicalEntrypointAndAbsoluteNodeTarget()
    {
        var fixture = ManagedFixture.Create(activationCode: 0);

        var prepared = await fixture.Transaction.PrepareAsync(
            "/repo",
            "runner",
            "tx-runner-layout",
            null);

        Assert.NotNull(prepared.Session);
        var session = prepared.Session!;
        var runner = session.Targets.Runner!;
        var distCopy = Assert.Single(fixture.Commands.ExecutedCommands, command =>
            command.FileName == "cp" && command.Args[^1].EndsWith("/runner/dist", StringComparison.Ordinal));
        Assert.Equal(["-RL", distCopy.Args[1], distCopy.Args[2]], distCopy.Args);
        Assert.EndsWith("/packages/runner/dist/.", distCopy.Args[1], StringComparison.Ordinal);
        Assert.Equal(
            Path.Combine(session.ReleaseRoot, "runner", ManagedRuntimeLayout.RunnerEntrypoint).Replace('\\', '/'),
            runner.Entrypoint);
        Assert.True(runner.IsAbsoluteTarget);
        Assert.True(runner.UsesCanonicalEntrypoint);
        Assert.Equal(RuntimeLaunchMode.Node, runner.LaunchMode);
        Assert.Equal(runner.Identity.SourceRevision, runner.Identity.BuildGitHash);
        Assert.True(Path.IsPathRooted(runner.NodeExecutable!));
        Assert.True(fixture.Files.HasFile(runner.Entrypoint));
        Assert.True(fixture.Files.HasFile(
            Path.Combine(session.ReleaseRoot, "runner", ManagedRuntimeLayout.RunnerBuildInfo).Replace('\\', '/')));
        Assert.DoesNotContain(fixture.Files.Files.Keys, path => path.Contains("/dist/dist/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, "runner-pluto")]
    [InlineData("runner-custom", "runner-custom")]
    public async Task PrepareManagedRunner_UsesCurrentLaunchIdentityForCandidateAndUnit(
        string? configuredRunnerId,
        string expectedRunnerId)
    {
        var fixture = ManagedFixture.Create(activationCode: 0, useSystemd: true, unitDir: UpdateTestFactory.UnitDir);
        SeedSourceRunner(fixture, configuredRunnerId);

        var prepared = await fixture.Transaction.PrepareAsync(
            "/repo",
            "runner",
            "tx-runner-instance-identity",
            null);

        var session = Assert.IsType<ManagedUpdateSession>(prepared.Session);
        var runner = Assert.IsType<RuntimeTarget>(session.Targets.Runner);
        Assert.Equal(expectedRunnerId, runner.Identity.RunnerId);
        var identityPath = Path.Combine(runner.WorkingDirectory, "runtime-identity.json").Replace('\\', '/');
        Assert.Equal(expectedRunnerId, RuntimeIdentity.Read(fixture.Files.Read(identityPath))!.RunnerId);
        Assert.Contains(
            $"Environment=\"RUNNER_ID={expectedRunnerId}\"",
            fixture.Files.Read(Path.Combine(UpdateTestFactory.UnitDir, "mohist-runner.service")),
            StringComparison.Ordinal);
        Assert.Equal(0, await fixture.Transaction.CommitAsync(session));
        var verified = Parse(fixture.Files.Read(fixture.VerifiedPath));
        Assert.Equal(expectedRunnerId, verified.Runner!.Identity.RunnerId);
    }

    [Fact]
    public async Task PrepareManagedRunner_UsesSourceLaunchIdentityInsteadOfPreviousManagedTarget()
    {
        var fixture = ManagedFixture.Create(activationCode: 0, useSystemd: true, unitDir: UpdateTestFactory.UnitDir);
        SeedSourceRunner(fixture, "runner-pluto");
        var previous = new RuntimeTargetSet(
            "verified",
            3,
            "previous-runner",
            null,
            null,
            new RuntimeTarget(
                "runner",
                "/managed/runner/dist/cli.js",
                "/managed/runner",
                [],
                "linux-x64",
                Identity("runner", 3, "runner-other"),
                "/usr/bin/node",
                "/managed/runner",
                RuntimeLaunchMode.Node),
            null);
        fixture.Files.AddFile(
            fixture.VerifiedPath,
            JsonSerializer.Serialize(previous, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var prepared = await fixture.Transaction.PrepareAsync(
            "/repo",
            "runner",
            "tx-runner-source-identity",
            null);

        var session = Assert.IsType<ManagedUpdateSession>(prepared.Session);
        Assert.Equal(4, session.Targets.Generation);
        Assert.Equal("runner-pluto", session.Targets.Runner!.Identity.RunnerId);
    }

    [Fact]
    public async Task RollbackManagedRunner_RestoresSourceUnitAndPreservesVerifiedCliAndServer()
    {
        var fixture = ManagedFixture.Create(activationCode: 0, useSystemd: true, unitDir: UpdateTestFactory.UnitDir);
        SeedSourceRunner(fixture, "runner-pluto");
        var sourceUnit = fixture.Files.Read(Path.Combine(UpdateTestFactory.UnitDir, "mohist-runner.service"));
        var previous = new RuntimeTargetSet(
            "verified",
            3,
            "previous-runtime",
            new RuntimeTarget("cli", "/managed/cli/Mohist.Cli", "/managed/cli", [], "linux-x64", Identity("cli", 3)),
            new RuntimeTarget("server", "/managed/server/Mohist.Server", "/managed/server", [], "linux-x64", Identity("server", 3)),
            null,
            null);
        var verifiedJson = JsonSerializer.Serialize(previous, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        fixture.Files.AddFile(fixture.ActivePath, verifiedJson);
        fixture.Files.AddFile(fixture.VerifiedPath, verifiedJson);

        var prepared = await fixture.Transaction.PrepareAsync(
            "/repo",
            "runner",
            "tx-runner-rollback",
            null);

        var session = Assert.IsType<ManagedUpdateSession>(prepared.Session);
        Assert.Equal(0, await fixture.Transaction.RollbackAsync(session, "candidate identity differs"));
        Assert.Equal(sourceUnit, fixture.Files.Read(Path.Combine(UpdateTestFactory.UnitDir, "mohist-runner.service")));
        Assert.Equal(verifiedJson, fixture.Files.Read(fixture.VerifiedPath));
        var active = Parse(fixture.Files.Read(fixture.ActivePath));
        Assert.Equal("verified", active.Status);
        Assert.Equal(previous.Cli!.Identity, active.Cli!.Identity);
        Assert.Equal(previous.Server!.Identity, active.Server!.Identity);
        Assert.Null(active.Runner);
    }

    [Theory]
    [InlineData(null, "configuration is unavailable")]
    [InlineData("[Service]\nEnvironment=\"RUNNER_ID=\"\n", "identity is empty")]
    [InlineData("[Service]\nEnvironment=\"RUNNER_ID=runner-a\" \"RunnerId=runner-b\"\n", "identity is ambiguous")]
    public async Task PrepareManagedRunner_WhenLaunchIdentityIsUnresolved_FailsBeforeBuild(
        string? sourceUnit,
        string expectedError)
    {
        var fixture = ManagedFixture.Create(activationCode: 0, useSystemd: true, unitDir: UpdateTestFactory.UnitDir);
        if (sourceUnit is not null)
            fixture.Files.AddFile(Path.Combine(UpdateTestFactory.UnitDir, "mohist-runner.service"), sourceUnit);

        var prepared = await fixture.Transaction.PrepareAsync(
            "/repo",
            "runner",
            "tx-runner-unresolved-identity",
            null);

        Assert.Null(prepared.Session);
        Assert.Contains(expectedError, prepared.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Commands.ExecutedCommands, command => command.FileName == "npm");
        Assert.Equal(0, fixture.Activator.ApplyCalls);
        Assert.False(fixture.Files.HasFile(fixture.ActivePath));
    }

    [Fact]
    public async Task PrepareRunner_WhenCandidateExistsForRetry_RemovesStaleNestedPayload()
    {
        var fixture = ManagedFixture.Create(activationCode: 0);
        var runnerRoot = Path.Combine(
            fixture.RuntimeRoot,
            "transactions",
            "tx-runner-retry",
            "candidate",
            "runner").Replace('\\', '/');
        fixture.Files.AddDirectory(runnerRoot);
        fixture.Files.AddFile(Path.Combine(runnerRoot, "dist", "stale.js"), "stale");
        fixture.Files.AddFile(Path.Combine(runnerRoot, "dist", "dist", "cli.js"), "nested stale");

        var prepared = await fixture.Transaction.PrepareAsync(
            "/repo",
            "runner",
            "tx-runner-retry",
            null);

        Assert.NotNull(prepared.Session);
        Assert.DoesNotContain(fixture.Files.Files.Keys, path => path.EndsWith("/stale.js", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Files.Files.Keys, path => path.Contains("/dist/dist/", StringComparison.Ordinal));
        Assert.True(fixture.Files.HasFile(prepared.Session!.Targets.Runner!.Entrypoint));
    }

    [Fact]
    public async Task PrepareRunner_WhenEntrypointIsMissing_FailsBeforeActivation()
    {
        var fixture = ManagedFixture.Create(activationCode: 0);
        fixture.Commands.RunnerCopyCreatesEntrypoint = false;

        var prepared = await fixture.Transaction.PrepareAsync(
            "/repo",
            "runner",
            "tx-runner-missing-entrypoint",
            null);

        Assert.Null(prepared.Session);
        Assert.Contains("Runner candidate", prepared.Error, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Activator.ApplyCalls);
        Assert.DoesNotContain(fixture.Files.Files.Keys, path => path.Contains("/dist/dist/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PrepareRunner_WhenPublisherCreatesNestedDist_FailsClosed()
    {
        var fixture = ManagedFixture.Create(activationCode: 0);
        fixture.Commands.RunnerCopyCreatesNestedEntrypoint = true;

        var prepared = await fixture.Transaction.PrepareAsync(
            "/repo",
            "runner",
            "tx-runner-nested-dist",
            null);

        Assert.Null(prepared.Session);
        Assert.Contains("Runner candidate", prepared.Error, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Activator.ApplyCalls);
        Assert.Contains(
            fixture.Files.Files.Keys,
            path => path.EndsWith("/dist/dist/cli.js", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PrepareFull_WhenNodeDependenciesFail_StopsBeforeBuildAndActivation()
    {
        var fixture = ManagedFixture.Create(activationCode: 0);
        fixture.Commands.SetResultFor(
            "npm",
            args => args.SequenceEqual(["ci", "--include=dev"]),
            127,
            "",
            "sh: tsc: not found");

        var prepared = await fixture.Transaction.PrepareAsync(
            "/repo",
            "full",
            "tx-full-node-deps-failure",
            null);

        Assert.Null(prepared.Session);
        Assert.Contains("Node dependencies", prepared.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Single(fixture.Commands.ExecutedCommands, command => command.FileName == "npm");
        Assert.DoesNotContain(fixture.Commands.ExecutedCommands, command =>
            command.FileName == "dotnet" || command.Args.SequenceEqual(["run", "build", "-w", "packages/runner"]));
        Assert.Equal(0, fixture.Activator.ApplyCalls);
        Assert.False(fixture.Files.HasFile(fixture.ActivePath));
        Assert.False(fixture.Files.HasFile(fixture.VerifiedPath));
        Assert.False(fixture.Files.DirectoryExists(Path.Combine(
            fixture.RuntimeRoot,
            "transactions",
            "tx-full-node-deps-failure",
            "snapshot",
            "packages",
            "server",
            "src",
            "Mohist.Server",
            "obj")));
    }

    [Fact]
    public async Task Prepare_WhenActivationFails_RestoresFailClosedState()
    {
        var fixture = ManagedFixture.Create(activationCode: 17);

        var prepared = await fixture.Transaction.PrepareAsync(
            "/repo",
            "server",
            "tx-failed",
            null);

        Assert.Null(prepared.Session);
        Assert.Contains("activation failed", prepared.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, fixture.Activator.ApplyCalls);
        Assert.Equal(1, fixture.Activator.RestoreCalls);
        var active = Parse(fixture.Files.Read(fixture.ActivePath));
        Assert.Equal("none", active.Status);
        Assert.False(fixture.Files.HasFile(fixture.VerifiedPath));
    }

    [Fact]
    public async Task SystemdManagedUnits_UseArtifactLaunchSemantics()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        var installer = new SystemdServiceInstaller(
            TextWriter.Null,
            TextWriter.Null,
            files,
            commands,
            new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false));
        var server = new RuntimeTarget(
            "server",
            "/managed/server/Mohist.Server",
            "/managed/server",
            [],
            "linux-x64",
            Identity("server", 4));
        var runner = new RuntimeTarget(
            "runner",
            "/managed/runner/dist/cli.js",
            "/managed/runner",
            [],
            "linux-x64",
            Identity("runner", 4, "runner-1"),
            "/usr/bin/node",
            "/managed/runner",
            RuntimeLaunchMode.Node);

        var result = await installer.ApplyManagedRuntimeAsync(
            new RuntimeTargetSet("candidate-staged", 4, "tx-units", null, server, runner, null),
            "full",
            "/units");

        Assert.Equal(0, result);
        var serverUnit = files.Read("/units/mohist.service");
        var runnerUnit = files.Read("/units/mohist-runner.service");
        Assert.Contains("ExecStart=/managed/server/Mohist.Server\n", serverUnit, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet", serverUnit, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "Environment=\"MOHIST_RUNTIME_IDENTITY_PATH=/managed/server/runtime-identity.json\"",
            serverUnit,
            StringComparison.Ordinal);
        Assert.Contains("ExecStart=/usr/bin/node /managed/runner/dist/cli.js\n", runnerUnit, StringComparison.Ordinal);
        Assert.Contains(
            "Environment=\"MOHIST_RUNTIME_IDENTITY_PATH=/managed/runner/runtime-identity.json\"",
            runnerUnit,
            StringComparison.Ordinal);
        Assert.Contains("Environment=\"RUNNER_ID=runner-1\"", runnerUnit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManagedServerUpdate_WhenReportedIdentityMatchesCandidate_Commits()
    {
        var fixture = ManagedFixture.Create(0, useSystemd: true, unitDir: UpdateTestFactory.UnitDir);
        var sourceUnit = SeedSourceServerAndVerifiedCli(fixture);
        var updater = fixture.BuildManagedServerUpdater(BuildManagedServerHandler(fixture, identityMode: "matching"));

        var result = await updater.UpdateServerAsync("/repo", dryRun: false);

        Assert.Equal(0, result);
        var verified = Parse(fixture.Files.Read(fixture.VerifiedPath));
        Assert.Equal("verified", verified.Status);
        Assert.NotNull(verified.Cli);
        Assert.NotNull(verified.Server);
        Assert.Equal(4, verified.Generation);
        Assert.Null(verified.SourceSnapshot);
        var managedUnit = fixture.Files.Read(Path.Combine(UpdateTestFactory.UnitDir, "mohist.service"));
        Assert.NotEqual(sourceUnit, managedUnit);
        Assert.Contains(
            $"Environment=\"MOHIST_RUNTIME_IDENTITY_PATH={verified.Server!.WorkingDirectory}/runtime-identity.json\"",
            managedUnit,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing-component")]
    [InlineData("wrong-generation")]
    public async Task ManagedServerUpdate_WhenReportedIdentityIsInvalid_RollsBackSourceAndKeepsCli(string identityMode)
    {
        var fixture = ManagedFixture.Create(0, useSystemd: true, unitDir: UpdateTestFactory.UnitDir);
        var sourceUnit = SeedSourceServerAndVerifiedCli(fixture);
        var verifiedBefore = fixture.Files.Read(fixture.VerifiedPath);
        var updater = fixture.BuildManagedServerUpdater(BuildManagedServerHandler(fixture, identityMode));

        var result = await updater.UpdateServerAsync("/repo", dryRun: false);

        Assert.Equal(1, result);
        Assert.Equal(sourceUnit, fixture.Files.Read(Path.Combine(UpdateTestFactory.UnitDir, "mohist.service")));
        Assert.Equal(verifiedBefore, fixture.Files.Read(fixture.VerifiedPath));
        var active = Parse(fixture.Files.Read(fixture.ActivePath));
        Assert.Equal("verified", active.Status);
        Assert.NotNull(active.Cli);
        Assert.Null(active.Server);
        Assert.Contains(fixture.Commands.ExecutedCommands, command =>
            command.FileName == "systemctl"
            && command.Args.SequenceEqual(["--user", "daemon-reload"]));
        Assert.Contains(fixture.Commands.ExecutedCommands, command =>
            command.FileName == "systemctl"
            && command.Args.SequenceEqual(["--user", "restart", "mohist.service"]));
    }

    [Fact]
    public async Task Prepare_WhenFirstManagedServerActivationFails_RestoresSourceUnitAndCliPointer()
    {
        var fixture = ManagedFixture.Create(0, useSystemd: true, unitDir: UpdateTestFactory.UnitDir);
        const string sourceUnit = "[Unit]\nDescription=Source Server\n\n[Service]\nExecStart=/repo/server\n\n[Install]\nWantedBy=default.target\n";
        fixture.Files.AddFile(Path.Combine(UpdateTestFactory.UnitDir, "mohist.service"), sourceUnit);
        fixture.Commands.SetResultFor(
            "systemctl",
            args => args.SequenceEqual(["--user", "is-active", "mohist.service"]),
            0,
            "active\n",
            "");
        fixture.Commands.SetResultFor(
            "systemctl",
            args => args.SequenceEqual(["--user", "is-enabled", "mohist.service"]),
            0,
            "enabled\n",
            "");
        fixture.Commands.QueueResultFor(
            "systemctl",
            args => args.SequenceEqual(["--user", "restart", "mohist.service"]),
            17,
            "",
            "candidate failed");
        fixture.Commands.QueueResultFor(
            "systemctl",
            args => args.SequenceEqual(["--user", "restart", "mohist.service"]),
            0,
            "",
            "");
        var previous = new RuntimeTargetSet(
            "verified",
            2,
            "tx-cli",
            new RuntimeTarget("cli", "/managed/cli/mo", "/managed/cli", [], "linux-x64", Identity("cli", 2)),
            null,
            null,
            null);
        fixture.Files.AddFile(fixture.VerifiedPath, JsonSerializer.Serialize(previous, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var prepared = await fixture.Transaction.PrepareAsync("/repo", "server", "tx-source-failure", null);

        Assert.Null(prepared.Session);
        Assert.Equal(sourceUnit, fixture.Files.Read(Path.Combine(UpdateTestFactory.UnitDir, "mohist.service")));
        Assert.Contains(fixture.Commands.ExecutedCommands, command => command.Args.SequenceEqual(["--user", "daemon-reload"]));
        Assert.Contains(fixture.Commands.ExecutedCommands, command => command.Args.SequenceEqual(["--user", "enable", "mohist.service"]));
        Assert.Equal(2, fixture.Commands.ExecutedCommands.Count(command => command.Args.SequenceEqual(["--user", "restart", "mohist.service"])));
        var transaction = Parse(fixture.Files.Read(Path.Combine(
            fixture.RuntimeRoot,
            "transactions",
            "tx-source-failure",
            "state.json")));
        Assert.NotNull(transaction.SourceSnapshot?.Server);
        Assert.True(transaction.SourceSnapshot!.Server!.Exists);
        Assert.Equal(Encoding.UTF8.GetBytes(sourceUnit), transaction.SourceSnapshot.Server.Contents);
        Assert.True(transaction.SourceSnapshot.Server.WasActive);
        Assert.True(transaction.SourceSnapshot.Server.WasEnabled);
        var active = Parse(fixture.Files.Read(fixture.ActivePath));
        var verified = Parse(fixture.Files.Read(fixture.VerifiedPath));
        Assert.Equal("verified", active.Status);
        Assert.NotNull(active.Cli);
        Assert.Null(active.Server);
        Assert.Equal(previous.Cli!.Identity, active.Cli!.Identity);
        Assert.Equal(previous.Cli.Identity, verified.Cli!.Identity);
    }

    [Fact]
    public async Task Prepare_WhenSourceProbeIsUnrecognized_FailsBeforeUnitOverwrite()
    {
        var fixture = ManagedFixture.Create(0, useSystemd: true, unitDir: UpdateTestFactory.UnitDir);
        const string sourceUnit = "[Service]\nExecStart=/repo/server\n";
        var unitPath = Path.Combine(UpdateTestFactory.UnitDir, "mohist.service");
        fixture.Files.AddFile(unitPath, sourceUnit);
        fixture.Commands.SetResultFor(
            "systemctl",
            args => args.SequenceEqual(["--user", "is-active", "mohist.service"]),
            1,
            "state-not-recognized\n",
            "probe failed");

        var prepared = await fixture.Transaction.PrepareAsync("/repo", "server", "tx-invalid-probe", null);

        Assert.Null(prepared.Session);
        Assert.Contains("staging failed", prepared.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.Activator.ApplyCalls);
        Assert.Equal(sourceUnit, fixture.Files.Read(unitPath));
    }

    [Fact]
    public async Task Rollback_WhenServerRestoreFails_RetainsPreviousCliAndLeavesVerifiedUnchanged()
    {
        var fixture = ManagedFixture.Create(0, useSystemd: true, unitDir: UpdateTestFactory.UnitDir);
        const string sourceUnit = "[Service]\nExecStart=/repo/server\n";
        var unitPath = Path.Combine(UpdateTestFactory.UnitDir, "mohist.service");
        fixture.Files.AddFile(unitPath, sourceUnit);
        fixture.Commands.SetResultFor(
            "systemctl",
            args => args.Length == 3 && args[1] == "is-active",
            0,
            "active\n",
            "");
        fixture.Commands.SetResultFor(
            "systemctl",
            args => args.Length == 3 && args[1] == "is-enabled",
            0,
            "enabled\n",
            "");
        var previous = new RuntimeTargetSet(
            "verified",
            2,
            "tx-cli",
            new RuntimeTarget("cli", "/managed/cli/mo", "/managed/cli", [], "linux-x64", Identity("cli", 2)),
            null,
            null,
            null);
        var verifiedJson = JsonSerializer.Serialize(previous, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        fixture.Files.AddFile(fixture.VerifiedPath, verifiedJson);

        var prepared = await fixture.Transaction.PrepareAsync("/repo", "server", "tx-rollback-failure", null);
        Assert.NotNull(prepared.Session);
        fixture.Commands.QueueResultFor(
            "systemctl",
            args => args.SequenceEqual(["--user", "restart", "mohist.service"]),
            23,
            "",
            "restore failed");

        var rollback = await fixture.Transaction.RollbackAsync(prepared.Session!, "readiness failed");

        Assert.Equal(23, rollback);
        var active = Parse(fixture.Files.Read(fixture.ActivePath));
        Assert.Equal("recovery-failed", active.Status);
        Assert.NotNull(active.Cli);
        Assert.Null(active.Server);
        Assert.Equal(previous.Cli!.Identity, active.Cli!.Identity);
        Assert.Equal(nameof(ManagedRuntimeRestoreState.Failed), active.Recovery!.Server);
        Assert.Equal(nameof(ManagedRuntimeRestoreState.NotAttempted), active.Recovery.Runner);
        Assert.Equal(verifiedJson, fixture.Files.Read(fixture.VerifiedPath));
        var transaction = Parse(fixture.Files.Read(Path.Combine(
            fixture.RuntimeRoot,
            "transactions",
            "tx-rollback-failure",
            "state.json")));
        Assert.Equal("recovery-failed", transaction.Status);
        Assert.Contains("affected scope=server", transaction.RecoveryDiagnostic, StringComparison.Ordinal);
        Assert.Equal(sourceUnit, fixture.Files.Read(unitPath));
    }

    [Fact]
    public async Task RollbackFull_WhenOneSourceRestoreFails_AttemptsOtherUnitAndPersistsDiagnostics()
    {
        var fixture = ManagedFixture.Create(0, useSystemd: true, unitDir: UpdateTestFactory.UnitDir);
        fixture.Files.AddFile(Path.Combine(UpdateTestFactory.UnitDir, "mohist.service"), "[Service]\nExecStart=/repo/server\n");
        fixture.Files.AddFile(Path.Combine(UpdateTestFactory.UnitDir, "mohist-runner.service"), "[Service]\nExecStart=/repo/runner\n");
        fixture.Commands.SetResultFor(
            "systemctl",
            args => args.Length == 3 && args[1] == "is-active",
            0,
            "active\n",
            "");
        fixture.Commands.SetResultFor(
            "systemctl",
            args => args.Length == 3 && args[1] == "is-enabled",
            0,
            "enabled\n",
            "");
        var previous = new RuntimeTargetSet(
            "verified",
            2,
            "tx-full",
            new RuntimeTarget("cli", "/managed/cli/mo", "/managed/cli", [], "linux-x64", Identity("cli", 2)),
            new RuntimeTarget("server", "/managed/server/Mohist.Server", "/managed/server", [], "linux-x64", Identity("server", 2)),
            new RuntimeTarget("runner", "/managed/runner/dist/cli.js", "/managed/runner", [], "linux-x64", Identity("runner", 2, "runner-1"), "/usr/bin/node", "/managed/runner", RuntimeLaunchMode.Node),
            null);
        var verifiedJson = JsonSerializer.Serialize(previous, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        fixture.Files.AddFile(fixture.VerifiedPath, verifiedJson);

        var prepared = await fixture.Transaction.PrepareAsync("/repo", "full", "tx-full-rollback-failure", null);
        Assert.NotNull(prepared.Session);
        fixture.Commands.QueueResultFor(
            "systemctl",
            args => args.SequenceEqual(["--user", "restart", "mohist.service"]),
            0,
            "",
            "");
        fixture.Commands.QueueResultFor(
            "systemctl",
            args => args.SequenceEqual(["--user", "restart", "mohist-runner.service"]),
            29,
            "",
            "runner restore failed");

        var rollback = await fixture.Transaction.RollbackAsync(prepared.Session!, "identity failed");

        Assert.Equal(29, rollback);
        Assert.Equal(2, fixture.Commands.ExecutedCommands.Count(command =>
            command.Args.SequenceEqual(["--user", "restart", "mohist-runner.service"])));
        var active = Parse(fixture.Files.Read(fixture.ActivePath));
        Assert.Equal("recovery-failed", active.Status);
        Assert.NotNull(active.Cli);
        Assert.NotNull(active.Server);
        Assert.Null(active.Runner);
        Assert.Equal(previous.Cli!.Identity, active.Cli!.Identity);
        Assert.Equal(previous.Server!.Identity, active.Server!.Identity);
        Assert.Equal(nameof(ManagedRuntimeRestoreState.Restored), active.Recovery!.Server);
        Assert.Equal(nameof(ManagedRuntimeRestoreState.Failed), active.Recovery.Runner);
        Assert.Equal(verifiedJson, fixture.Files.Read(fixture.VerifiedPath));
        var transaction = Parse(fixture.Files.Read(Path.Combine(
            fixture.RuntimeRoot,
            "transactions",
            "tx-full-rollback-failure",
            "state.json")));
        Assert.Equal("recovery-failed", transaction.Status);
        Assert.Contains("affected scope=full", transaction.RecoveryDiagnostic, StringComparison.Ordinal);
        Assert.Equal(nameof(ManagedRuntimeRestoreState.Restored), transaction.Recovery!.Server);
        Assert.Equal(nameof(ManagedRuntimeRestoreState.Failed), transaction.Recovery.Runner);
    }

    private static RuntimeIdentity Identity(string component, long generation, string? runnerId = null) =>
        new(
            component,
            "0.0.0+commit",
            new string('a', 40),
            new string('b', 40),
            new string('c', 64),
            "mohist-server-commit",
            generation,
            runnerId);

    private static string SeedSourceServerAndVerifiedCli(ManagedFixture fixture)
    {
        const string sourceUnit = "[Unit]\nDescription=Source Server\n\n[Service]\nExecStart=/repo/server\n\n[Install]\nWantedBy=default.target\n";
        fixture.Files.AddFile(Path.Combine(UpdateTestFactory.UnitDir, "mohist.service"), sourceUnit);
        fixture.Commands.SetResultFor(
            "systemctl",
            args => args.SequenceEqual(["--user", "is-active", "mohist.service"]),
            0,
            "active\n",
            "");
        fixture.Commands.SetResultFor(
            "systemctl",
            args => args.SequenceEqual(["--user", "is-enabled", "mohist.service"]),
            0,
            "enabled\n",
            "");

        var cliIdentity = Identity("cli", 3) with { ReleaseId = "mohist-cli-commit" };
        var previous = new RuntimeTargetSet(
            "verified",
            3,
            "previous-cli",
            new RuntimeTarget("cli", "/managed/cli/mo", "/managed/cli", [], "linux-x64", cliIdentity),
            null,
            null,
            null);
        var json = JsonSerializer.Serialize(previous, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        fixture.Files.AddFile(fixture.ActivePath, json);
        fixture.Files.AddFile(fixture.VerifiedPath, json);
        return sourceUnit;
    }

    private static void SeedSourceRunner(ManagedFixture fixture, string? runnerId)
    {
        var environment = runnerId is null ? string.Empty : $"Environment=\"RUNNER_ID={runnerId}\"\n";
        fixture.Files.AddFile(
            Path.Combine(UpdateTestFactory.UnitDir, "mohist-runner.service"),
            $"[Service]\n{environment}ExecStart=/repo/runner\n");
        fixture.Commands.SetResultFor(
            "systemctl",
            args => args.SequenceEqual(["--user", "is-active", "mohist-runner.service"]),
            0,
            "active\n",
            "");
        fixture.Commands.SetResultFor(
            "systemctl",
            args => args.SequenceEqual(["--user", "is-enabled", "mohist-runner.service"]),
            0,
            "enabled\n",
            "");
    }

    private static HttpMessageHandler BuildManagedServerHandler(ManagedFixture fixture, string identityMode)
    {
        return new RecordingHttpHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/health")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            if (path == "/")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "<html><script src=\"/assets/app.js\"></script></html>",
                        Encoding.UTF8,
                        "text/html"),
                });
            }
            if (path == "/assets/app.js")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

            Assert.Equal("/api/system/info", path);
            var active = Parse(fixture.Files.Read(fixture.ActivePath));
            var server = Assert.IsType<RuntimeTarget>(active.Server);
            var manifestPath = Path.Combine(server.WorkingDirectory, "runtime-identity.json").Replace('\\', '/');
            var identity = RuntimeIdentity.Read(fixture.Files.Read(manifestPath));
            Assert.NotNull(identity);
            var running = new Dictionary<string, object?>
            {
                ["component"] = identity.Component,
                ["version"] = identity.Version,
                ["sourceRevision"] = identity.SourceRevision,
                ["gitHash"] = identity.SourceRevision,
                ["treeHash"] = identity.TreeHash,
                ["artifactDigest"] = identity.ArtifactDigest,
                ["releaseId"] = identity.ReleaseId,
                ["generation"] = identity.Generation,
            };
            if (identityMode == "missing-component")
                running.Remove("component");
            else if (identityMode == "wrong-generation")
                running["generation"] = identity.Generation - 1;

            var body = JsonSerializer.Serialize(new { success = true, data = new { running } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        });
    }

    private static RuntimeTargetSet Parse(string json) =>
        JsonSerializer.Deserialize<RuntimeTargetSet>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new InvalidOperationException("runtime target set was not persisted");

    internal sealed class ManagedFixture
    {
        private const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string Tree = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        private ManagedFixture(
            FakeFileSystem files,
            FakeCommandExecutor commands,
            RecordingActivator activator,
            ManagedRuntimeTransaction transaction,
            string runtimeRoot,
            MockEnvironmentVariableProvider environment,
            SystemdServiceInstaller? systemd)
        {
            Files = files;
            Commands = commands;
            Activator = activator;
            Transaction = transaction;
            RuntimeRoot = runtimeRoot;
            Environment = environment;
            Systemd = systemd;
        }

        public FakeFileSystem Files { get; }
        public FakeCommandExecutor Commands { get; }
        public RecordingActivator Activator { get; }
        public ManagedRuntimeTransaction Transaction { get; }
        public string RuntimeRoot { get; }
        public MockEnvironmentVariableProvider Environment { get; }
        public SystemdServiceInstaller? Systemd { get; }
        public RuntimeTargetSet? LastPreparedTargets => Activator.LastTargets;
        public string ActivePath => Path.Combine(RuntimeRoot, "active.json").Replace('\\', '/');
        public string VerifiedPath => Path.Combine(RuntimeRoot, "verified.json").Replace('\\', '/');

        public SourceCodeUpdater BuildManagedServerUpdater(HttpMessageHandler handler)
        {
            var systemd = Systemd ?? throw new InvalidOperationException("managed updater requires systemd");
            return SourceCodeUpdater.CreateWithDefaults(
                TextWriter.Null,
                TextWriter.Null,
                systemd,
                Commands,
                Files,
                Environment,
                new HttpClient(handler) { BaseAddress = new Uri(UpdateTestFactory.ServerAddress) },
                serverReadyTimeout: TimeSpan.FromSeconds(1),
                getUserHome: () => "/home/test",
                unitDir: UpdateTestFactory.UnitDir,
                managedUpdatesEnabled: true);
        }

        public static ManagedFixture Create(int activationCode, bool useSystemd = false, string? unitDir = null)
        {
            var files = new FakeFileSystem();
            files.AddFile("/repo/Mohist.sln", "solution");
            var commands = new FakeCommandExecutor();
            commands.SetResultFor(
                "git",
                args => args.SequenceEqual(["rev-parse", "HEAD"]),
                0,
                Commit + "\n",
                "");
            commands.SetResultFor(
                "git",
                args => args.SequenceEqual(["rev-parse", "HEAD^{tree}"]),
                0,
                Tree + "\n",
                "");
            commands.OnExecute = (fileName, args) =>
            {
                if (fileName == "tar")
                {
                    var extractIndex = Array.IndexOf(args, "-C");
                    if (extractIndex >= 0 && extractIndex + 1 < args.Length)
                    {
                        var extractRoot = args[extractIndex + 1];
                        files.AddFile(Path.Combine(extractRoot, "Mohist.sln"), "solution");
                        files.AddFile(
                            Path.Combine(extractRoot, "packages", "server", "src", "Mohist.Server", "Mohist.Server.csproj"),
                            "project");
                    }
                    return;
                }

                if (fileName == "chmod"
                    && args.Length == 3
                    && args[0] == "-R"
                    && args[1] == "a-w")
                {
                    files.MarkReadOnly(args[2]);
                    return;
                }

                if (fileName == "cp" && args.Length == 3 && args[0] == "-RL")
                {
                    var source = args[1];
                    var target = args[2];
                    if (source.EndsWith("/dist/.", StringComparison.Ordinal))
                    {
                        files.AddDirectory(target);
                        if (commands.RunnerCopyCreatesNestedEntrypoint)
                        {
                            files.AddFile(Path.Combine(target, "dist", "cli.js"), "nested runner payload");
                        }
                        else if (commands.RunnerCopyCreatesEntrypoint)
                        {
                            files.AddFile(Path.Combine(target, "cli.js"), "runner payload");
                        }
                    }
                    else if (source.EndsWith("/package.json", StringComparison.Ordinal))
                    {
                        files.AddFile(target, "{}");
                    }
                    else if (source.EndsWith("/node_modules", StringComparison.Ordinal))
                    {
                        files.AddDirectory(target);
                        files.AddFile(Path.Combine(target, "typescript", "bin", "tsc"), "tsc");
                    }
                    return;
                }

                if (fileName != "dotnet" || !args.Contains("publish"))
                    return;

                var project = args[1];
                files.CreateDirectory(Path.Combine(Path.GetDirectoryName(project)!, "obj"));
                var outputIndex = Array.IndexOf(args, "-o");
                if (outputIndex < 0 || outputIndex + 1 >= args.Length)
                    return;
                var output = args[outputIndex + 1];
                files.AddDirectory(output);
                var entryName = project.Contains("Mohist.Cli", StringComparison.Ordinal)
                    ? "Mohist.Cli"
                    : "Mohist.Server";
                files.AddFile(Path.Combine(output, entryName), $"immutable {entryName} payload");
            };

            var environment = new MockEnvironmentVariableProvider(addExistingEnvironmentVariables: false);
            var runtimeRoot = UpdateSourceResolver.ResolveRuntimeRoot("/home/test");
            var resolver = new UpdateSourceResolver(commands, files, () => "/home/test");
            var systemd = useSystemd
                ? new SystemdServiceInstaller(
                    TextWriter.Null,
                    TextWriter.Null,
                    files,
                    commands,
                    environment,
                    getLocalHostname: () => "pluto")
                : null;
            var activator = new RecordingActivator(activationCode, systemd);
            var transaction = new ManagedRuntimeTransaction(
                TextWriter.Null,
                TextWriter.Null,
                commands,
                files,
                environment,
                resolver,
                activator,
                unitDir);
            return new ManagedFixture(files, commands, activator, transaction, runtimeRoot, environment, systemd);
        }
    }

    internal sealed class RecordingActivator(int applyCode, IManagedRuntimeActivator? inner = null) : IManagedRuntimeActivator
    {
        public int ApplyCalls { get; private set; }
        public int RestoreCalls { get; private set; }
        public int RestoreCode { get; set; }
        public RuntimeTargetSet? LastTargets { get; private set; }

        public Task<(RunnerLaunchIdentity? Identity, string? Error)> ResolveRunnerLaunchIdentityAsync(
            string? unitDir,
            CancellationToken cancellationToken = default) =>
            inner?.ResolveRunnerLaunchIdentityAsync(unitDir, cancellationToken)
            ?? Task.FromResult<(RunnerLaunchIdentity?, string?)>((new RunnerLaunchIdentity("runner-test"), null));

        public Task<ManagedRuntimeSnapshot?> CaptureManagedRuntimeSnapshotAsync(
            string scope,
            string? unitDir,
            CancellationToken cancellationToken = default) =>
            inner?.CaptureManagedRuntimeSnapshotAsync(scope, unitDir, cancellationToken)
            ?? Task.FromResult<ManagedRuntimeSnapshot?>(null);

        public Task<int> ApplyManagedRuntimeAsync(
            RuntimeTargetSet targets,
            string scope,
            string? unitDir,
            CancellationToken cancellationToken = default,
            ManagedRuntimeSnapshot? snapshot = null)
        {
            ApplyCalls++;
            LastTargets = targets;
            return inner is null
                ? Task.FromResult(applyCode)
                : inner.ApplyManagedRuntimeAsync(targets, scope, unitDir, cancellationToken, snapshot);
        }

        public Task<ManagedRuntimeRestoreResult> RestoreManagedRuntimeAsync(
            RuntimeTargetSet? targets,
            string scope,
            string? unitDir,
            CancellationToken cancellationToken = default,
            ManagedRuntimeSnapshot? snapshot = null)
        {
            RestoreCalls++;
            return inner?.RestoreManagedRuntimeAsync(targets, scope, unitDir, cancellationToken, snapshot)
                ?? Task.FromResult(ManagedRuntimeRestoreResult.FromExitCode(RestoreCode, scope));
        }
    }
}
