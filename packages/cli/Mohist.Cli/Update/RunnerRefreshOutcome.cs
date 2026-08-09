using System.Net;
using System.Text.Json;

namespace Mohist.Cli;

internal abstract record RunnerRefreshOutcome
{
    private RunnerRefreshOutcome() { }

    public int ExitCode => this switch
    {
        UnknownIdentity => 1,
        StaleRunnerRuntime => 1,
        NotReconnected => 1,
        _ => 0,
    };

    /// <summary>
    /// Emits the user-facing summary of this outcome. Lives on the type so the facade does not
    /// pattern-match on each subtype — keeping the runner refresh presentation knowledge in
    /// the file that owns the record hierarchy.
    /// </summary>
    public abstract void WriteSummary(TextWriter output, TextWriter error);

    /// <summary>Reported build identity matches the repo HEAD.</summary>
    public sealed record Current : RunnerRefreshOutcome
    {
        public static readonly Current Instance = new();
        private Current() { }

        public override void WriteSummary(TextWriter output, TextWriter error)
            => output.WriteLine("Runner runtime verification: current (matches expected source hash).");
    }

    /// <summary>Runner runtime identity could not be determined.</summary>
    public sealed record UnknownIdentity(string Reason) : RunnerRefreshOutcome
    {
        public override void WriteSummary(TextWriter output, TextWriter error)
            => error.WriteLine($"Runner runtime verification: unknown-identity ({Reason}).");
    }

    /// <summary>Runner did not report a fresh build identity after activation.</summary>
    public sealed record NotReconnected : RunnerRefreshOutcome
    {
        public static readonly NotReconnected Instance = new();
        private NotReconnected() { }

        public override void WriteSummary(TextWriter output, TextWriter error)
            => error.WriteLine("Runner runtime verification: runner-not-reconnected (runner did not report a build identity after activation).");
    }

    /// <summary>Runner is online but reported build identity differs from the expected source.</summary>
    public sealed record StaleRunnerRuntime(string? ReportedHash, string? ExpectedHash, string Reason) : RunnerRefreshOutcome
    {
        public override void WriteSummary(TextWriter output, TextWriter error)
            => error.WriteLine($"Runner runtime verification: stale-runner-runtime (expected {ExpectedHash ?? "<unavailable>"}, actual {ReportedHash ?? "<null>"}).");
    }

    /// <summary>Runner refresh was intentionally skipped before build/restart.</summary>
    public sealed record Skipped(string Reason) : RunnerRefreshOutcome
    {
        public override void WriteSummary(TextWriter output, TextWriter error)
            => output.WriteLine($"Runner runtime verification: runner-refresh-skipped({Reason}).");
    }
}

internal sealed record RunnerIdentityView(
    string RunnerId,
    string Hostname,
    string? BuildGitHash,
    string Status,
    DateTimeOffset? LastHeartbeatAt,
    string ConnectionState);

/// <summary>
/// Encapsulates the runner refresh verification flow: read the runner identity endpoint once after
/// activation and reduce the result to a
/// <see cref="RunnerRefreshOutcome"/>. Extracted from <see cref="SourceCodeUpdater"/> so the
/// facade no longer carries the verify identity logic alongside stage orchestration.
/// </summary>
internal sealed class RunnerRefreshVerifier
{
    private readonly HttpClient _http;
    private readonly Func<string?> _getLocalHostname;

    public RunnerRefreshVerifier(
        HttpClient http,
        ICommandExecutor commandExecutor,
        IFileSystem fileSystem,
        Func<string?>? getLocalHostname = null,
        TimeSpan? runnerIdentityTimeout = null,
        TimeSpan? runnerIdentityPollInterval = null,
        TimeProvider? timeProvider = null)
    {
        _http = http;
        _ = commandExecutor;
        _ = fileSystem;
        _getLocalHostname = getLocalHostname ?? (() => Environment.MachineName);
        _ = runnerIdentityTimeout;
        _ = runnerIdentityPollInterval;
        _ = timeProvider;
    }

    public void WriteSkippedSummary(string reason, TextWriter output, TextWriter error)
    {
        new RunnerRefreshOutcome.Skipped(reason).WriteSummary(output, error);
    }

    public async Task<RunnerRefreshOutcome> VerifyRunnerRuntimeAsync(string expectedSourceHash)
    {
        var hostname = _getLocalHostname();
        if (string.IsNullOrWhiteSpace(hostname))
        {
            return new RunnerRefreshOutcome.UnknownIdentity("local hostname is unavailable; cannot identify local runner");
        }

        RunnerIdentityView? identity = null;
        try
        {
            identity = await TryReadRunnerIdentityAsync(hostname, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            return RunnerRefreshOutcome.NotReconnected.Instance;
        }

        if (identity is null)
        {
            return RunnerRefreshOutcome.NotReconnected.Instance;
        }

        if (identity.Status != "online")
        {
            return new RunnerRefreshOutcome.StaleRunnerRuntime(
                ReportedHash: identity.BuildGitHash,
                ExpectedHash: expectedSourceHash,
                Reason: $"runner reported status '{identity.Status}' instead of 'online'");
        }

        if (string.IsNullOrWhiteSpace(identity.BuildGitHash))
        {
            return new RunnerRefreshOutcome.UnknownIdentity("runner did not report a buildGitHash; runner build cannot be verified");
        }

        return string.Equals(identity.BuildGitHash, expectedSourceHash, StringComparison.Ordinal)
            ? RunnerRefreshOutcome.Current.Instance
            : new RunnerRefreshOutcome.StaleRunnerRuntime(
                ReportedHash: identity.BuildGitHash,
                ExpectedHash: expectedSourceHash,
                Reason: "reported buildGitHash differs from expected source hash");
    }

    private async Task<RunnerIdentityView?> TryReadRunnerIdentityAsync(string hostname, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync($"/api/runner/identity?hostname={Uri.EscapeDataString(hostname)}", ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return null;
            return ReadRunnerIdentityView(data);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static RunnerIdentityView ReadRunnerIdentityView(JsonElement data)
    {
        string? GetString(string property)
        {
            if (!data.TryGetProperty(property, out var el) || el.ValueKind == JsonValueKind.Null)
                return null;
            return el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();
        }
        DateTimeOffset? GetDate(string property)
        {
            if (!data.TryGetProperty(property, out var el) || el.ValueKind == JsonValueKind.Null)
                return null;
            if (el.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(el.GetString(), out var parsed))
                return parsed;
            return null;
        }
        return new RunnerIdentityView(
            GetString("runnerId") ?? string.Empty,
            GetString("hostname") ?? string.Empty,
            GetString("buildGitHash"),
            GetString("status") ?? "offline",
            GetDate("lastHeartbeatAt"),
            GetString("connectionState") ?? "disconnected");
    }
}
