using System.Net;
using System.Text.Json;

namespace Mohist.Cli;

internal sealed record RunnerIdentityExpectation(
    string RunnerId,
    string RuntimeGeneration,
    string SourceHash,
    string ArtifactDigest);

internal sealed record RunnerRuntimeIdentity(
    string RunnerId,
    string? RuntimeGeneration,
    string? BuildGitHash,
    string? ArtifactDigest,
    string Status,
    string ConnectionState);

/// <summary>
/// An awaitable identity transition source. The HTTP implementation consumes the
/// Server's event-backed endpoint; test implementations complete an explicit
/// signal. Neither implementation polls or sleeps.
/// </summary>
internal interface IRunnerRuntimeReadinessSignal
{
    Task<RunnerRuntimeIdentity?> WaitForIdentityAsync(
        RunnerIdentityExpectation expected,
        CancellationToken cancellationToken);
}

internal sealed class HttpRunnerRuntimeReadinessSignal(HttpClient http) : IRunnerRuntimeReadinessSignal
{
    public async Task<RunnerRuntimeIdentity?> WaitForIdentityAsync(
        RunnerIdentityExpectation expected,
        CancellationToken cancellationToken)
    {
        var uri = "/api/runner/identity?runnerId=" + Uri.EscapeDataString(expected.RunnerId)
            + "&generation=" + Uri.EscapeDataString(expected.RuntimeGeneration)
            + "&wait=true";
        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound || !response.IsSuccessStatusCode)
                return null;
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (root.TryGetProperty("data", out var data))
                root = data;
            return ReadIdentity(root);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static RunnerRuntimeIdentity ReadIdentity(JsonElement data)
    {
        static string? GetString(JsonElement data, string property)
        {
            if (!data.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
                return null;
            return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
        }

        return new RunnerRuntimeIdentity(
            GetString(data, "runnerId") ?? string.Empty,
            GetString(data, "runtimeGeneration"),
            GetString(data, "buildGitHash"),
            GetString(data, "artifactDigest"),
            GetString(data, "status") ?? "offline",
            GetString(data, "connectionState") ?? "disconnected");
    }
}

internal abstract record RunnerRefreshOutcome
{
    private RunnerRefreshOutcome() { }

    public int ExitCode => this switch
    {
        Current => 0,
        Skipped => 0,
        _ => 1,
    };

    public abstract void WriteSummary(TextWriter output, TextWriter error);

    /// <summary>
    /// The runtime identity matches. This is intentionally not an activation
    /// success: the update transaction still must read back the service target
    /// and commit the verified link.
    /// </summary>
    public sealed record Current(
        RunnerIdentityExpectation Expected,
        RunnerRuntimeIdentity Actual) : RunnerRefreshOutcome
    {
        public override void WriteSummary(TextWriter output, TextWriter error) =>
            output.WriteLine($"Runner runtime identity matched for {Expected.RunnerId} generation {Expected.RuntimeGeneration}; committing verified target is pending.");
    }

    public sealed record UnknownIdentity(string Reason) : RunnerRefreshOutcome
    {
        public override void WriteSummary(TextWriter output, TextWriter error) =>
            error.WriteLine($"Runner runtime verification: unknown-identity ({Reason}).");
    }

    public sealed record NotReconnected(string RunnerId, string RuntimeGeneration) : RunnerRefreshOutcome
    {
        public override void WriteSummary(TextWriter output, TextWriter error) =>
            error.WriteLine($"Runner runtime verification: runner-not-reconnected ({RunnerId} generation {RuntimeGeneration} did not become connected and online).");
    }

    public sealed record StaleRunnerRuntime(
        string? ReportedHash,
        string ExpectedHash,
        string Reason,
        string? ReportedArtifactDigest = null,
        string? ExpectedArtifactDigest = null) : RunnerRefreshOutcome
    {
        public override void WriteSummary(TextWriter output, TextWriter error)
        {
            error.WriteLine($"Runner runtime verification: stale-runner-runtime (expected {ExpectedHash}, actual {ReportedHash ?? "<null>"}; {Reason}).");
            if (!string.IsNullOrWhiteSpace(ExpectedArtifactDigest))
            {
                error.WriteLine($"Runner artifact identity: expected {ExpectedArtifactDigest}, actual {ReportedArtifactDigest ?? "<null>"}.");
            }
        }
    }

    public sealed record Skipped(string Reason) : RunnerRefreshOutcome
    {
        public override void WriteSummary(TextWriter output, TextWriter error) =>
            output.WriteLine($"Runner runtime verification: runner-refresh-skipped({Reason}).");
    }
}

/// <summary>
/// Binds readiness to the exact installed Runner instance. Timeout is driven by
/// the injected TimeProvider and only cancels one awaitable signal; it never
/// performs a wall-clock poll or chooses a runner by hostname.
/// </summary>
internal sealed class RunnerRefreshVerifier
{
    private static readonly TimeSpan DefaultRunnerIdentityTimeout = TimeSpan.FromSeconds(30);

    private readonly IRunnerRuntimeReadinessSignal _readinessSignal;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _runnerIdentityTimeout;

    public RunnerRefreshVerifier(
        HttpClient http,
        ICommandExecutor commandExecutor,
        IFileSystem fileSystem,
        TimeSpan? runnerIdentityTimeout = null,
        TimeProvider? timeProvider = null,
        IRunnerRuntimeReadinessSignal? readinessSignal = null)
    {
        _ = commandExecutor;
        _ = fileSystem;
        _readinessSignal = readinessSignal ?? new HttpRunnerRuntimeReadinessSignal(http);
        _runnerIdentityTimeout = runnerIdentityTimeout ?? DefaultRunnerIdentityTimeout;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public void WriteSkippedSummary(string reason, TextWriter output, TextWriter error) =>
        new RunnerRefreshOutcome.Skipped(reason).WriteSummary(output, error);

    public async Task<RunnerRefreshOutcome> VerifyRunnerRuntimeAsync(
        RunnerIdentityExpectation expected,
        CancellationToken cancellationToken = default)
    {
        RunnerRuntimeIdentity? identity;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var timer = StartTimeoutTimer(timeoutCts, _runnerIdentityTimeout);
        try
        {
            identity = await _readinessSignal.WaitForIdentityAsync(expected, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new RunnerRefreshOutcome.NotReconnected(expected.RunnerId, expected.RuntimeGeneration);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }

        if (identity is null)
            return new RunnerRefreshOutcome.NotReconnected(expected.RunnerId, expected.RuntimeGeneration);

        if (!string.Equals(identity.RunnerId, expected.RunnerId, StringComparison.Ordinal)
            || !string.Equals(identity.RuntimeGeneration, expected.RuntimeGeneration, StringComparison.Ordinal))
        {
            return new RunnerRefreshOutcome.UnknownIdentity(
                "readiness signal returned a different runner instance");
        }

        if (!string.Equals(identity.Status, "online", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(identity.ConnectionState, "connected", StringComparison.OrdinalIgnoreCase))
        {
            return new RunnerRefreshOutcome.NotReconnected(expected.RunnerId, expected.RuntimeGeneration);
        }

        if (string.IsNullOrWhiteSpace(identity.BuildGitHash))
            return new RunnerRefreshOutcome.UnknownIdentity("runner did not report a buildGitHash");
        if (string.IsNullOrWhiteSpace(identity.ArtifactDigest))
            return new RunnerRefreshOutcome.UnknownIdentity("runner did not report an artifactDigest");

        if (!string.Equals(identity.BuildGitHash, expected.SourceHash, StringComparison.Ordinal)
            || !string.Equals(identity.ArtifactDigest, expected.ArtifactDigest, StringComparison.Ordinal))
        {
            return new RunnerRefreshOutcome.StaleRunnerRuntime(
                identity.BuildGitHash,
                expected.SourceHash,
                "reported source or artifact identity differs from the installed candidate",
                identity.ArtifactDigest,
                expected.ArtifactDigest);
        }

        return new RunnerRefreshOutcome.Current(expected, identity);
    }

    private ITimer? StartTimeoutTimer(CancellationTokenSource cts, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            cts.Cancel();
            return null;
        }

        return _timeProvider.CreateTimer(
            static state =>
            {
                try
                {
                    ((CancellationTokenSource)state!).Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            },
            cts,
            timeout,
            Timeout.InfiniteTimeSpan);
    }
}
