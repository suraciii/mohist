using System.Net;
using Microsoft.Extensions.Time.Testing;
using Mohist.Cli;
using Xunit;

namespace Mohist.Cli.Tests.Update;

public class RunnerRefreshVerifierSpecs
{
    [Fact]
    public async Task VerifyRunnerRuntimeAsync_WaitsForExplicitReadinessThenAcceptsExactInstance()
    {
        var signal = new DeferredRunnerReadinessSignal();
        var verifier = CreateVerifier(signal);
        var expected = Expected();

        var verification = verifier.VerifyRunnerRuntimeAsync(expected);
        await signal.Waiting;
        signal.Publish(new RunnerRuntimeIdentity(
            expected.RunnerId,
            expected.RuntimeGeneration,
            expected.SourceHash,
            expected.ArtifactDigest,
            "online",
            "connected"));

        var outcome = await verification;

        Assert.IsType<RunnerRefreshOutcome.Current>(outcome);
    }

    [Fact]
    public async Task VerifyRunnerRuntimeAsync_RejectsDifferentRunnerIdEvenWhenIdentityOtherwiseMatches()
    {
        var signal = new DeferredRunnerReadinessSignal();
        var verifier = CreateVerifier(signal);
        var expected = Expected();

        var verification = verifier.VerifyRunnerRuntimeAsync(expected);
        await signal.Waiting;
        signal.Publish(new RunnerRuntimeIdentity(
            "runner-other",
            expected.RuntimeGeneration,
            expected.SourceHash,
            expected.ArtifactDigest,
            "online",
            "connected"));

        var outcome = await verification;

        Assert.IsType<RunnerRefreshOutcome.UnknownIdentity>(outcome);
    }

    [Fact]
    public async Task VerifyRunnerRuntimeAsync_RequiresConnectedAsWellAsOnline()
    {
        var signal = new DeferredRunnerReadinessSignal();
        var verifier = CreateVerifier(signal);
        var expected = Expected();

        var verification = verifier.VerifyRunnerRuntimeAsync(expected);
        await signal.Waiting;
        signal.Publish(new RunnerRuntimeIdentity(
            expected.RunnerId,
            expected.RuntimeGeneration,
            expected.SourceHash,
            expected.ArtifactDigest,
            "online",
            "disconnected"));

        var outcome = await verification;

        Assert.IsType<RunnerRefreshOutcome.NotReconnected>(outcome);
    }

    [Fact]
    public async Task VerifyRunnerRuntimeAsync_ReportsArtifactMismatchAfterExactInstanceConnects()
    {
        var signal = new DeferredRunnerReadinessSignal();
        var verifier = CreateVerifier(signal);
        var expected = Expected();
        const string staleDigest = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

        var verification = verifier.VerifyRunnerRuntimeAsync(expected);
        await signal.Waiting;
        signal.Publish(new RunnerRuntimeIdentity(
            expected.RunnerId,
            expected.RuntimeGeneration,
            expected.SourceHash,
            staleDigest,
            "online",
            "connected"));

        var outcome = await verification;

        var stale = Assert.IsType<RunnerRefreshOutcome.StaleRunnerRuntime>(outcome);
        Assert.Equal(expected.SourceHash, stale.ReportedHash);
        Assert.Equal(staleDigest, stale.ReportedArtifactDigest);
        Assert.Equal(expected.ArtifactDigest, stale.ExpectedArtifactDigest);
    }

    [Fact]
    public async Task VerifyRunnerRuntimeAsync_RejectsOldRuntimeAfterExactInstanceConnects()
    {
        var signal = new DeferredRunnerReadinessSignal();
        var verifier = CreateVerifier(signal);
        var expected = Expected();
        const string oldHash = "fedcba9876543210fedcba9876543210fedcba98";

        var verification = verifier.VerifyRunnerRuntimeAsync(expected);
        await signal.Waiting;
        signal.Publish(new RunnerRuntimeIdentity(
            expected.RunnerId,
            expected.RuntimeGeneration,
            oldHash,
            expected.ArtifactDigest,
            "online",
            "connected"));

        var outcome = await verification;

        var stale = Assert.IsType<RunnerRefreshOutcome.StaleRunnerRuntime>(outcome);
        Assert.Equal(oldHash, stale.ReportedHash);
        Assert.Equal(expected.SourceHash, stale.ExpectedHash);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task VerifyRunnerRuntimeAsync_RejectsMissingRequiredIdentity(bool missingHash)
    {
        var signal = new DeferredRunnerReadinessSignal();
        var verifier = CreateVerifier(signal);
        var expected = Expected();

        var verification = verifier.VerifyRunnerRuntimeAsync(expected);
        await signal.Waiting;
        signal.Publish(new RunnerRuntimeIdentity(
            expected.RunnerId,
            expected.RuntimeGeneration,
            missingHash ? null : expected.SourceHash,
            missingHash ? expected.ArtifactDigest : null,
            "online",
            "connected"));

        var outcome = await verification;

        Assert.IsType<RunnerRefreshOutcome.UnknownIdentity>(outcome);
    }

    [Fact]
    public async Task VerifyRunnerRuntimeAsync_UsesInjectedDeadlineForOnePendingReadinessSignal()
    {
        var signal = new DeferredRunnerReadinessSignal();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var verifier = CreateVerifier(signal, time, TimeSpan.FromSeconds(5));

        var verification = verifier.VerifyRunnerRuntimeAsync(Expected());
        await signal.Waiting;
        Assert.False(verification.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(5));
        var outcome = await verification;

        Assert.IsType<RunnerRefreshOutcome.NotReconnected>(outcome);
    }

    private static RunnerRefreshVerifier CreateVerifier(
        IRunnerRuntimeReadinessSignal signal,
        TimeProvider? timeProvider = null,
        TimeSpan? timeout = null) =>
        new(
            new HttpClient(new NoNetworkHandler()) { BaseAddress = new Uri("http://localhost:3456") },
            new FakeCommandExecutor(),
            new FakeFileSystem(),
            runnerIdentityTimeout: timeout,
            timeProvider: timeProvider,
            readinessSignal: signal);

    private static RunnerIdentityExpectation Expected() => new(
        "runner-managed",
        "generation-42",
        "0123456789abcdef0123456789abcdef01234567",
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");

    private sealed class DeferredRunnerReadinessSignal : IRunnerRuntimeReadinessSignal
    {
        private readonly TaskCompletionSource<RunnerRuntimeIdentity?> _identity = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Waiting => _waiting.Task;

        public Task<RunnerRuntimeIdentity?> WaitForIdentityAsync(
            RunnerIdentityExpectation expected,
            CancellationToken cancellationToken)
        {
            _waiting.TrySetResult();
            return _identity.Task.WaitAsync(cancellationToken);
        }

        public void Publish(RunnerRuntimeIdentity identity) => _identity.TrySetResult(identity);
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The injected readiness signal must handle runner verification.");
    }
}
