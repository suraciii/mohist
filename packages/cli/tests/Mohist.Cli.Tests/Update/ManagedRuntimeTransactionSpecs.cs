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
                if (fileName != "dotnet" || !args.Contains("publish"))
                    return;

                var outputIndex = Array.IndexOf(args, "-o");
                if (outputIndex < 0 || outputIndex + 1 >= args.Length)
                    return;
                var output = args[outputIndex + 1];
                files.AddDirectory(output);
                files.AddFile(Path.Combine(output, "Mohist.Server"), "immutable server payload");
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
