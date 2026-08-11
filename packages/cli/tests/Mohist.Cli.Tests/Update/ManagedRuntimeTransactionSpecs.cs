using EnvironmentAbstractions.TestHelpers;
using Mohist.Cli;
using System.Text.Json;
using Xunit;

namespace Mohist.Cli.Tests.Update;

public sealed class ManagedRuntimeTransactionSpecs
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

    private static RuntimeIdentity Identity(string component, long generation) =>
        new(
            component,
            "0.0.0+commit",
            new string('a', 40),
            new string('b', 40),
            new string('c', 64),
            "mohist-server-commit",
            generation);

    private static RuntimeTargetSet Parse(string json) =>
        JsonSerializer.Deserialize<RuntimeTargetSet>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new InvalidOperationException("runtime target set was not persisted");

    private sealed class ManagedFixture
    {
        private const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string Tree = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        private ManagedFixture(
            FakeFileSystem files,
            FakeCommandExecutor commands,
            RecordingActivator activator,
            ManagedRuntimeTransaction transaction,
            string runtimeRoot)
        {
            Files = files;
            Commands = commands;
            Activator = activator;
            Transaction = transaction;
            RuntimeRoot = runtimeRoot;
        }

        public FakeFileSystem Files { get; }
        public FakeCommandExecutor Commands { get; }
        public RecordingActivator Activator { get; }
        public ManagedRuntimeTransaction Transaction { get; }
        public string RuntimeRoot { get; }
        public string ActivePath => Path.Combine(RuntimeRoot, "active.json").Replace('\\', '/');
        public string VerifiedPath => Path.Combine(RuntimeRoot, "verified.json").Replace('\\', '/');

        public static ManagedFixture Create(int activationCode)
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
                    if (source.EndsWith("/dist", StringComparison.Ordinal))
                    {
                        files.AddDirectory(target);
                        files.AddFile(Path.Combine(target, "cli.js"), "runner payload");
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
            var activator = new RecordingActivator(activationCode);
            var transaction = new ManagedRuntimeTransaction(
                TextWriter.Null,
                TextWriter.Null,
                commands,
                files,
                environment,
                resolver,
                activator);
            return new ManagedFixture(files, commands, activator, transaction, runtimeRoot);
        }
    }

    private sealed class RecordingActivator(int applyCode) : IManagedRuntimeActivator
    {
        public int ApplyCalls { get; private set; }
        public int RestoreCalls { get; private set; }

        public Task<int> ApplyManagedRuntimeAsync(
            RuntimeTargetSet targets,
            string scope,
            string? unitDir,
            CancellationToken cancellationToken = default)
        {
            ApplyCalls++;
            return Task.FromResult(applyCode);
        }

        public Task<int> RestoreManagedRuntimeAsync(
            RuntimeTargetSet? targets,
            string scope,
            string? unitDir,
            CancellationToken cancellationToken = default)
        {
            RestoreCalls++;
            return Task.FromResult(0);
        }
    }
}
