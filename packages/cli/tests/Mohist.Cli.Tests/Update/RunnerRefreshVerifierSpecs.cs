using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests.Update;

public sealed class RunnerRefreshVerifierSpecs
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task VerifyRunnerRuntimeAsync_IgnoresOldConnectionBeforeCandidateWithSameRunnerId()
    {
        var expected = CandidateIdentity();
        var old = expected with
        {
            SourceRevision = "old-source",
            TreeHash = "old-tree",
            ArtifactDigest = "old-artifact",
            ReleaseId = "old-release",
            Generation = expected.Generation - 1,
            BuildGitHash = "old-source",
            ConnectionGeneration = "server:1",
        };
        var time = NewTimeProvider();
        var handler = IdentitySequenceHandler(old, expected);
        var verifier = BuildVerifier(handler, time);

        var verification = verifier.VerifyRunnerRuntimeAsync(expected);
        await handler.WaitForRequestCountAsync(1);
        time.Advance(PollInterval);
        await handler.WaitForRequestCountAsync(2);

        Assert.IsType<RunnerRefreshOutcome.Current>(await verification);
    }

    [Fact]
    public async Task VerifyRunnerRuntimeAsync_WaitsForStaleProjectionToReachCandidateGeneration()
    {
        var expected = CandidateIdentity();
        var staleProjection = expected with
        {
            SourceRevision = "old-source",
            TreeHash = "old-tree",
            ArtifactDigest = "old-artifact",
            ReleaseId = "old-release",
            Generation = expected.Generation - 1,
            BuildGitHash = "old-source",
        };
        var time = NewTimeProvider();
        var handler = IdentitySequenceHandler(staleProjection, expected);
        var verifier = BuildVerifier(handler, time);

        var verification = verifier.VerifyRunnerRuntimeAsync(expected);
        await handler.WaitForRequestCountAsync(1);
        time.Advance(PollInterval);
        await handler.WaitForRequestCountAsync(2);

        Assert.IsType<RunnerRefreshOutcome.Current>(await verification);
    }

    [Theory]
    [InlineData("buildGitHash")]
    [InlineData("sourceRevision")]
    [InlineData("treeHash")]
    [InlineData("artifactDigest")]
    [InlineData("releaseId")]
    [InlineData("generation")]
    [InlineData("runnerId")]
    public async Task VerifyRunnerRuntimeAsync_ReportsOnlyTheMismatchingIdentityField(string field)
    {
        var expected = CandidateIdentity();
        var actual = WithMismatch(expected, field);
        var time = NewTimeProvider();
        var handler = IdentitySequenceHandler(actual);
        var verifier = BuildVerifier(handler, time);

        var verification = verifier.VerifyRunnerRuntimeAsync(expected);
        await handler.WaitForRequestCountAsync(1);
        time.Advance(Timeout);

        var stale = Assert.IsType<RunnerRefreshOutcome.StaleRunnerRuntime>(await verification);
        var difference = Assert.Single(stale.Differences!);
        Assert.Equal(field, difference.Field);
    }

    [Fact]
    public async Task VerifyRunnerRuntimeAsync_DiagnosticDoesNotCallEqualBuildHashesMismatched()
    {
        var expected = CandidateIdentity();
        var actual = expected with { ArtifactDigest = "wrong-artifact" };
        var time = NewTimeProvider();
        var handler = IdentitySequenceHandler(actual);
        var verifier = BuildVerifier(handler, time);

        var verification = verifier.VerifyRunnerRuntimeAsync(expected);
        await handler.WaitForRequestCountAsync(1);
        time.Advance(Timeout);

        var stale = Assert.IsType<RunnerRefreshOutcome.StaleRunnerRuntime>(await verification);
        using var output = new StringWriter();
        using var error = new StringWriter();
        stale.WriteSummary(output, error);

        var diagnostic = error.ToString();
        Assert.Contains("artifactDigest expected='artifact' actual='wrong-artifact'", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("runner buildGitHash", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("expected='source' actual='source'", diagnostic, StringComparison.Ordinal);
    }

    private static RunnerRefreshVerifier BuildVerifier(RecordingHttpHandler handler, TimeProvider time) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost:0") },
            new UpdateTestFactory().Commands,
            new FakeFileSystem(),
            getLocalHostname: () => "test-host",
            runnerIdentityTimeout: Timeout,
            runnerIdentityPollInterval: PollInterval,
            timeProvider: time);

    private static RecordingHttpHandler IdentitySequenceHandler(params RuntimeIdentity[] identities)
    {
        var index = 0;
        return new RecordingHttpHandler((request, _) =>
        {
            Assert.Equal("/api/runner/identity", request.RequestUri!.AbsolutePath);
            var identity = identities[Math.Min(index++, identities.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    IdentityJson(identity),
                    Encoding.UTF8,
                    "application/json"),
            });
        });
    }

    private static string IdentityJson(RuntimeIdentity identity) =>
        JsonSerializer.Serialize(new
        {
            success = true,
            data = new
            {
                runnerId = identity.RunnerId,
                hostname = "test-host",
                buildGitHash = identity.BuildGitHash,
                component = identity.Component,
                version = identity.Version,
                sourceRevision = identity.SourceRevision,
                treeHash = identity.TreeHash,
                artifactDigest = identity.ArtifactDigest,
                releaseId = identity.ReleaseId,
                generation = identity.Generation,
                status = "online",
                connectionState = "connected",
                connectionGeneration = identity.ConnectionGeneration ?? "server:2",
            },
        });

    private static RuntimeIdentity CandidateIdentity() =>
        new(
            "runner",
            "0.0.0+candidate",
            "source",
            "tree",
            "artifact",
            "release",
            7,
            "runner-1",
            null,
            "source");

    private static RuntimeIdentity WithMismatch(RuntimeIdentity identity, string field) => field switch
    {
        "buildGitHash" => identity with { BuildGitHash = "wrong-build" },
        "sourceRevision" => identity with { SourceRevision = "wrong-source" },
        "treeHash" => identity with { TreeHash = "wrong-tree" },
        "artifactDigest" => identity with { ArtifactDigest = "wrong-artifact" },
        "releaseId" => identity with { ReleaseId = "wrong-release" },
        "generation" => identity with { Generation = identity.Generation - 1 },
        "runnerId" => identity with { RunnerId = "runner-2" },
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
    };

    private static FakeTimeProvider NewTimeProvider() =>
        new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
}
