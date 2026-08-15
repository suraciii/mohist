using System.Net;
using System.Text;
using System.Text.Json;

namespace Mohist.Cli;

internal abstract record RunnerRefreshOutcome
{
    private RunnerRefreshOutcome() { }

    public RunnerRecoveryReport? Recovery { get; init; }

    public int ExitCode => this switch
    {
        StaleRunnerRuntime => 1,
        NotReconnected => 1,
        UnknownIdentity => 1,
        _ when Recovery is { ExitCode: not 0 } => 1,
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
        {
            output.WriteLine("Runner runtime verification: current (matches repo HEAD).");
            Recovery?.WriteSummary(output, error);
        }
    }

    /// <summary>Runner runtime identity could not be determined.</summary>
    public sealed record UnknownIdentity(string Reason) : RunnerRefreshOutcome
    {
        public override void WriteSummary(TextWriter output, TextWriter error)
            => output.WriteLine($"Runner runtime verification: unknown-identity ({Reason}).");
    }

    /// <summary>Runner did not report a fresh build identity after the restart window.</summary>
    public sealed record NotReconnected : RunnerRefreshOutcome
    {
        public static readonly NotReconnected Instance = new();
        private NotReconnected() { }

        public override void WriteSummary(TextWriter output, TextWriter error)
            => error.WriteLine("Runner runtime verification: runner-not-reconnected (runner did not report a build identity after restart).");
    }

    /// <summary>Runner is online but reported build identity differs from the current source.</summary>
    public sealed record StaleRunnerRuntime(
        string? ReportedHash,
        string? RepoHeadHash,
        string Reason,
        IReadOnlyList<RuntimeIdentityDifference>? Differences = null) : RunnerRefreshOutcome
    {
        public override void WriteSummary(TextWriter output, TextWriter error)
        {
            if (Differences is { Count: > 0 })
            {
                error.WriteLine(
                    $"Runner runtime verification: stale-runner-runtime ({Reason}; {string.Join(", ", Differences)}).");
                return;
            }

            if (!string.IsNullOrWhiteSpace(Reason)
                && !string.Equals(Reason, "reported buildGitHash differs from repo HEAD", StringComparison.Ordinal))
            {
                error.WriteLine($"Runner runtime verification: stale-runner-runtime ({Reason}).");
                return;
            }

            error.WriteLine($"Runner runtime verification: stale-runner-runtime (runner buildGitHash {ReportedHash ?? "<null>"} != repo HEAD {RepoHeadHash ?? "<unavailable>"}).");
        }
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
    string? Component,
    string? Version,
    string? SourceRevision,
    string? TreeHash,
    string? ArtifactDigest,
    string? ReleaseId,
    long? Generation,
    string Status,
    DateTimeOffset? LastHeartbeatAt,
    string ConnectionState,
    string? ConnectionGeneration = null);

internal sealed record RunnerUpdateWorkIdentity(
    string OwnerKind,
    string OwnerId,
    string WorkId,
    string? TaskRunId,
    string WorkType);

internal sealed record RunnerRecoveryWorkOutcome(
    RunnerUpdateWorkIdentity Identity,
    string Status,
    string State)
{
    public bool Recovered => string.Equals(Status, "recovered", StringComparison.Ordinal);
}

internal sealed record RunnerRecoveryReport(IReadOnlyList<RunnerRecoveryWorkOutcome> Works)
{
    public bool HasAffectedWork => Works.Count > 0;
    public bool FullyRecovered => HasAffectedWork && Works.All(work => work.Recovered);
    public int ExitCode => HasAffectedWork && !FullyRecovered ? 1 : 0;

    public void WriteSummary(TextWriter output, TextWriter error)
    {
        if (Works.Count == 0)
        {
            output.WriteLine("Runner update recovery: affected work=none; no recovery claimed.");
            return;
        }

        foreach (var work in Works)
        {
            var identity = work.Identity;
            var task = string.IsNullOrWhiteSpace(identity.TaskRunId)
                ? string.Empty
                : $" taskRunId={identity.TaskRunId}";
            var owner = string.IsNullOrWhiteSpace(identity.OwnerId)
                ? string.Empty
                : $" ownerId={identity.OwnerId}";
            var state = string.IsNullOrWhiteSpace(work.State) ? work.Status : work.State;
            var line = $"Runner update recovery: workId={identity.WorkId} ownerKind={identity.OwnerKind}{owner}{task} state={state} status={work.Status}.";
            if (work.Recovered)
                output.WriteLine(line);
            else
                error.WriteLine(line);
        }
    }
}

internal sealed record RunnerInterruptResult(
    string? RunnerId,
    string? Status,
    string? UpdateInterruptId,
    IReadOnlyList<string> InterruptedWorkIds,
    int InterruptedWorkCount,
    string? Error,
    string? OperationId = null,
    DateTimeOffset? CreatedAt = null,
    IReadOnlyList<RunnerUpdateWorkIdentity>? AffectedWorks = null)
{
    public bool Succeeded => Error is null
        && string.Equals(Status, "interrupted", StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(RunnerId);

    public static RunnerInterruptResult Failed(string error) =>
        new(null, null, null, Array.Empty<string>(), 0, error, null, null, Array.Empty<RunnerUpdateWorkIdentity>());

}

/// <summary>
/// Encapsulates the runner refresh verification flow: poll the authoritative connected runner
/// identity endpoint and reduce the result to a
/// <see cref="RunnerRefreshOutcome"/>. Extracted from <see cref="SourceCodeUpdater"/> so the
/// facade no longer carries the verify identity logic alongside stage orchestration.
/// </summary>
internal sealed class RunnerRefreshVerifier
{
    private static readonly TimeSpan DefaultRunnerIdentityPollInterval = TimeSpan.FromMilliseconds(500);
    internal static readonly TimeSpan DefaultRunnerRecoveryTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan DefaultRunnerRecoveryPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly string RunnerDistBuildInfoRelativePath = Path.Combine("packages", "runner", "dist", "build-info.json");

    private readonly HttpClient _http;
    private readonly ICommandExecutor _commandExecutor;
    private readonly IFileSystem _fileSystem;
    private readonly Func<string?> _getLocalHostname;
    private readonly TimeSpan _runnerIdentityTimeout;
    private readonly TimeSpan _runnerIdentityPollInterval;
    private readonly TimeSpan _runnerRecoveryTimeout;
    private readonly TimeSpan _runnerRecoveryPollInterval;
    private readonly TimeProvider _timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> _pollWait;

    public RunnerRefreshVerifier(
        HttpClient http,
        ICommandExecutor commandExecutor,
        IFileSystem fileSystem,
        Func<string?>? getLocalHostname = null,
        TimeSpan? runnerIdentityTimeout = null,
        TimeSpan? runnerIdentityPollInterval = null,
        TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task>? pollWait = null,
        TimeSpan? runnerRecoveryTimeout = null,
        TimeSpan? runnerRecoveryPollInterval = null)

    {
        _http = http;
        _commandExecutor = commandExecutor;
        _fileSystem = fileSystem;
        _getLocalHostname = getLocalHostname ?? (() => Environment.MachineName);
        _runnerIdentityTimeout = runnerIdentityTimeout ?? TimeSpan.FromSeconds(30);
        _runnerIdentityPollInterval = runnerIdentityPollInterval ?? DefaultRunnerIdentityPollInterval;
        _runnerRecoveryTimeout = runnerRecoveryTimeout ?? DefaultRunnerRecoveryTimeout;
        _runnerRecoveryPollInterval = runnerRecoveryPollInterval ?? DefaultRunnerRecoveryPollInterval;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _pollWait = pollWait
            ?? ((delay, cancellationToken) => Task.Delay(delay, _timeProvider, cancellationToken));
    }

    internal TimeProvider TimeProvider => _timeProvider;
    internal Func<TimeSpan, CancellationToken, Task> PollWait => _pollWait;
    internal string? LocalHostname => _getLocalHostname();

    public void WriteSkippedSummary(string reason, TextWriter output, TextWriter error)
    {
        new RunnerRefreshOutcome.Skipped(reason).WriteSummary(output, error);
    }

    public async Task<RunnerInterruptResult> InterruptRunnerAsync(CancellationToken cancellationToken = default)
    {
        var hostname = _getLocalHostname();
        if (string.IsNullOrWhiteSpace(hostname))
            return RunnerInterruptResult.Failed("local hostname is unavailable; cannot identify local runner");

        var identity = await TryReadRunnerIdentityAsync(hostname, cancellationToken);
        if (identity is null || string.IsNullOrWhiteSpace(identity.RunnerId))
        {
            return RunnerInterruptResult.Failed(
                $"runner identity is unavailable for hostname '{hostname}'; update interrupt was not confirmed");
        }

        try
        {
            var path = $"/api/runner/{Uri.EscapeDataString(identity.RunnerId)}/update-interrupt";
            var updateInterruptId = Guid.NewGuid().ToString("N");
            using var content = new StringContent(
                JsonSerializer.Serialize(new { updateInterruptId }),
                Encoding.UTF8,
                "application/json");
            using var response = await _http.PostAsync(path, content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return RunnerInterruptResult.Failed(
                    $"HTTP {(int)response.StatusCode} {response.ReasonPhrase ?? "request failed"}");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            var data = document.RootElement.TryGetProperty("data", out var envelopeData)
                ? envelopeData
                : document.RootElement;
            return ReadRunnerInterruptResult(data, identity.RunnerId, updateInterruptId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return RunnerInterruptResult.Failed($"request failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Releases only the update fence this CLI invocation acquired. A failure
    /// is returned to the caller so the original update failure remains
    /// visible instead of being mistaken for a completed handoff.
    /// </summary>
    public async Task<string?> CancelRunnerUpdateInterruptAsync(
        RunnerInterruptResult interruption,
        CancellationToken cancellationToken = default)
    {
        if (!interruption.Succeeded
            || string.IsNullOrWhiteSpace(interruption.RunnerId)
            || string.IsNullOrWhiteSpace(interruption.UpdateInterruptId))
        {
            return "no confirmed update interrupt is available to cancel";
        }

        try
        {
            var path = $"/api/runner/{Uri.EscapeDataString(interruption.RunnerId)}/update-interrupt/"
                + $"{Uri.EscapeDataString(interruption.UpdateInterruptId)}/cancel";
            using var response = await _http.PostAsync(path, content: null, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return $"HTTP {(int)response.StatusCode} {response.ReasonPhrase ?? "request failed"}";
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            var data = document.RootElement.TryGetProperty("data", out var envelopeData)
                ? envelopeData
                : document.RootElement;
            if (data.ValueKind != JsonValueKind.Object)
                return "response data is not an object";

            var runnerId = ReadString(data, "runnerId");
            var updateInterruptId = ReadString(data, "updateInterruptId");
            var status = ReadString(data, "status");
            if (!string.Equals(runnerId, interruption.RunnerId, StringComparison.Ordinal))
                return "response runnerId does not match the confirmed update interrupt";
            if (!string.Equals(updateInterruptId, interruption.UpdateInterruptId, StringComparison.Ordinal))
                return "response updateInterruptId does not match the confirmed update interrupt";
            if (string.Equals(status, "cancelled", StringComparison.Ordinal)
                || string.Equals(status, "already-cancelled", StringComparison.Ordinal))
            {
                return null;
            }

            return $"response status was '{status ?? "<missing>"}', expected 'cancelled'";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"request failed: {ex.Message}";
        }
    }

    public async Task<RunnerRecoveryReport> WaitForRecoveryAsync(
        RunnerInterruptResult interruption,
        CancellationToken cancellationToken = default)
    {
        var identities = interruption.AffectedWorks is { Count: > 0 }
            ? interruption.AffectedWorks
            : interruption.InterruptedWorkIds
                .Select(workId => new RunnerUpdateWorkIdentity(
                    "unknown",
                    string.Empty,
                    workId,
                    null,
                    string.Empty))
                .ToArray();
        if (identities.Count == 0)
            return new RunnerRecoveryReport(Array.Empty<RunnerRecoveryWorkOutcome>());

        var unresolved = identities
            .Select(identity => new RunnerRecoveryWorkOutcome(identity, "unresolved", "receipt-pending"))
            .ToArray();
        if (string.IsNullOrWhiteSpace(interruption.OperationId))
            return new RunnerRecoveryReport(unresolved);

        var deadline = _timeProvider.GetUtcNow() + _runnerRecoveryTimeout;
        var latest = unresolved;
        while (!cancellationToken.IsCancellationRequested)
        {
            var remaining = deadline - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
                break;

            var status = await TryReadRecoveryStatusAsync(
                interruption.RunnerId!,
                interruption.OperationId,
                remaining,
                cancellationToken);
            if (status is not null)
            {
                latest = identities
                    .Select(identity =>
                    {
                        var serverWork = status.Works.FirstOrDefault(work =>
                            string.Equals(work.Identity.OwnerKind, identity.OwnerKind, StringComparison.Ordinal)
                            && string.Equals(work.Identity.OwnerId, identity.OwnerId, StringComparison.Ordinal)
                            && string.Equals(work.Identity.WorkId, identity.WorkId, StringComparison.Ordinal)
                            && string.Equals(work.Identity.TaskRunId, identity.TaskRunId, StringComparison.Ordinal));
                        return serverWork is { Recovered: true }
                            ? serverWork
                            : new RunnerRecoveryWorkOutcome(identity, "unresolved", serverWork?.State ?? "receipt-pending");
                    })
                    .ToArray();
                if (latest.All(work => work.Recovered))
                    break;
            }

            remaining = deadline - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
                break;
            try
            {
                await Task.Delay(
                    remaining < _runnerRecoveryPollInterval ? remaining : _runnerRecoveryPollInterval,
                    _timeProvider,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        if (cancellationToken.IsCancellationRequested)
            cancellationToken.ThrowIfCancellationRequested();

        return new RunnerRecoveryReport(latest);

    }

    public async Task<RunnerRefreshOutcome> VerifyRunnerRuntimeAsync(string repoRoot)
    {
        var repoHead = await TryReadRepoHeadAsync(repoRoot);
        var hostname = _getLocalHostname();
        if (string.IsNullOrWhiteSpace(hostname))
        {
            return new RunnerRefreshOutcome.UnknownIdentity("local hostname is unavailable; cannot identify local runner");
        }

        using var cts = new CancellationTokenSource();
        using var timeoutTimer = StartTimeoutTimer(cts, _runnerIdentityTimeout);
        RunnerIdentityView? identity = null;
        while (!cts.IsCancellationRequested)
        {
            try
            {
                identity = await TryReadRunnerIdentityAsync(hostname, cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                break;
            }
            if (identity is not null)
                break;
            try
            {
                await _pollWait(_runnerIdentityPollInterval, cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                break;
            }
        }

        if (identity is null)
        {
            return RunnerRefreshOutcome.NotReconnected.Instance;
        }

        if (identity.Status != "online")
        {
            return new RunnerRefreshOutcome.StaleRunnerRuntime(
                ReportedHash: identity.BuildGitHash,
                RepoHeadHash: repoHead,
                Reason: $"runner reported status '{identity.Status}' instead of 'online'");
        }

        if (repoHead is null)
        {
            return new RunnerRefreshOutcome.UnknownIdentity("git rev-parse HEAD is unavailable; cannot compare identity");
        }

        if (string.IsNullOrWhiteSpace(identity.BuildGitHash))
        {
            return new RunnerRefreshOutcome.UnknownIdentity("runner did not report a buildGitHash; runner build cannot be verified");
        }

        return string.Equals(identity.BuildGitHash, repoHead, StringComparison.Ordinal)
            ? RunnerRefreshOutcome.Current.Instance
            : new RunnerRefreshOutcome.StaleRunnerRuntime(
                ReportedHash: identity.BuildGitHash,
                RepoHeadHash: repoHead,
                Reason: "reported buildGitHash differs from repo HEAD");
    }

    public async Task<RunnerRefreshOutcome> VerifyRunnerRuntimeAsync(RuntimeIdentity expected)
    {
        var hostname = _getLocalHostname();
        if (string.IsNullOrWhiteSpace(hostname))
            return new RunnerRefreshOutcome.UnknownIdentity("local hostname is unavailable; cannot identify local runner");

        using var cts = new CancellationTokenSource();
        using var timeoutTimer = StartTimeoutTimer(cts, _runnerIdentityTimeout);
        RunnerIdentityView? lastIdentity = null;
        RuntimeIdentity? lastRuntimeIdentity = null;
        IReadOnlyList<RuntimeIdentityDifference>? lastDifferences = null;
        while (!cts.IsCancellationRequested)
        {
            var identity = await TryReadRunnerIdentityAsync(hostname, cts.Token);
            if (identity is not null)
            {
                lastIdentity = identity;
                if (IsCandidateConnection(identity, expected))
                {
                    var candidateRuntimeIdentity = ToRuntimeIdentity(identity);
                    lastRuntimeIdentity = candidateRuntimeIdentity;
                    var candidateDifferences = candidateRuntimeIdentity.Differences(expected);
                    lastDifferences = candidateDifferences;
                    if (candidateRuntimeIdentity.IsComplete && candidateDifferences.Count == 0)
                        return RunnerRefreshOutcome.Current.Instance;
                }
            }
            try
            {
                await _pollWait(_runnerIdentityPollInterval, cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                break;
            }
        }

        if (lastIdentity is null)
            return RunnerRefreshOutcome.NotReconnected.Instance;

        var actual = lastRuntimeIdentity ?? ToRuntimeIdentity(lastIdentity);
        var differences = lastDifferences ?? actual.Differences(expected);
        var reason = IsCandidateConnection(lastIdentity, expected)
            ? "connected runner identity differs from the candidate release"
            : $"runner did not report connected candidate generation {expected.Generation}";
        return new RunnerRefreshOutcome.StaleRunnerRuntime(
            actual.BuildGitHash ?? actual.SourceRevision,
            expected.BuildGitHash ?? expected.SourceRevision,
            reason,
            differences);
    }

    private static bool IsCandidateConnection(RunnerIdentityView identity, RuntimeIdentity expected) =>
        string.Equals(identity.Status, "online", StringComparison.OrdinalIgnoreCase)
        &&
        string.Equals(identity.ConnectionState, "connected", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(identity.ConnectionGeneration)
        && identity.Generation == expected.Generation;

    private static RuntimeIdentity ToRuntimeIdentity(RunnerIdentityView identity) =>
        new(
            identity.Component ?? string.Empty,
            identity.Version ?? string.Empty,
            identity.SourceRevision ?? string.Empty,
            identity.TreeHash ?? string.Empty,
            identity.ArtifactDigest ?? string.Empty,
            identity.ReleaseId ?? string.Empty,
            identity.Generation ?? 0,
            string.IsNullOrWhiteSpace(identity.RunnerId) ? null : identity.RunnerId,
            identity.ConnectionGeneration,
            identity.BuildGitHash);

    private async Task<RunnerRefreshOutcome> VerifyRunnerDistManifestAsync(
        string repoRoot,
        string? repoHead,
        string notReconnectedReason)
    {
        var manifestPath = Path.Combine(repoRoot, RunnerDistBuildInfoRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!_fileSystem.Exists(manifestPath))
        {
            return new RunnerRefreshOutcome.UnknownIdentity(
                $"runner did not reconnect; dist/build-info.json not found at {manifestPath}");
        }

        string? manifestHash = null;
        try
        {
            using var stream = _fileSystem.OpenRead(manifestPath);
            using var reader = new StreamReader(stream);
            var raw = await reader.ReadToEndAsync();
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("gitHash", out var element) && element.ValueKind == JsonValueKind.String)
            {
                manifestHash = element.GetString();
            }
        }
        catch (Exception ex)
        {
            return new RunnerRefreshOutcome.UnknownIdentity(
                $"runner did not reconnect; could not read dist/build-info.json: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(manifestHash) || repoHead is null)
        {
            return new RunnerRefreshOutcome.UnknownIdentity(
                $"runner did not reconnect and dist/build-info.json is missing gitHash ({manifestPath})");
        }

        return string.Equals(manifestHash, repoHead, StringComparison.Ordinal)
            ? RunnerRefreshOutcome.Current.Instance
            : new RunnerRefreshOutcome.StaleRunnerRuntime(
                ReportedHash: manifestHash,
                RepoHeadHash: repoHead,
                Reason: notReconnectedReason + "; dist/build-info.json differs from repo HEAD");
    }

    private async Task<string?> TryReadRepoHeadAsync(string repoRoot)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var (code, stdout, _) = await _commandExecutor.ExecuteAsync("git", ["rev-parse", "HEAD"], repoRoot).WaitAsync(cts.Token);
            if (code != 0) return null;
            var hash = stdout.Trim();
            return string.IsNullOrWhiteSpace(hash) ? null : hash;
        }
        catch
        {
            return null;
        }
    }

    private async Task<RunnerRecoveryReport?> TryReadRecoveryStatusAsync(
        string runnerId,
        string operationId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var timeoutTimer = StartTimeoutTimer(requestCts, timeout);
        try
        {
            using var response = await _http.GetAsync(
                $"/api/runner/{Uri.EscapeDataString(runnerId)}/update-operation/{Uri.EscapeDataString(operationId)}/recovery-status",
                requestCts.Token);
            if (!response.IsSuccessStatusCode)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(requestCts.Token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: requestCts.Token);
            var data = document.RootElement.TryGetProperty("data", out var envelopeData)
                ? envelopeData
                : document.RootElement;
            if (data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("affectedWorks", out var works)
                || works.ValueKind != JsonValueKind.Array)
                return null;

            var parsed = new List<RunnerRecoveryWorkOutcome>();
            foreach (var item in works.EnumerateArray())
            {
                var identity = new RunnerUpdateWorkIdentity(
                    ReadString(item, "ownerKind") ?? "unknown",
                    ReadString(item, "ownerId") ?? string.Empty,
                    ReadString(item, "workId") ?? string.Empty,
                    ReadString(item, "taskRunId"),
                    ReadString(item, "workType") ?? string.Empty);
                var status = ReadString(item, "status") ?? "unresolved";
                parsed.Add(new RunnerRecoveryWorkOutcome(
                    identity,
                    status is "receipt-acked" or "replacement-settled" ? "recovered" : "unresolved",
                    status));
            }
            return new RunnerRecoveryReport(parsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
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

    private static RunnerInterruptResult ReadRunnerInterruptResult(
        JsonElement data,
        string expectedRunnerId,
        string expectedUpdateInterruptId)
    {
        if (data.ValueKind != JsonValueKind.Object)
            return RunnerInterruptResult.Failed("response data is not an object");

        var runnerId = ReadString(data, "runnerId");
        var status = ReadString(data, "status");
        if (!string.Equals(runnerId, expectedRunnerId, StringComparison.Ordinal))
            return RunnerInterruptResult.Failed("response runnerId does not match the identified runner");
        if (!string.Equals(status, "interrupted", StringComparison.Ordinal))
            return RunnerInterruptResult.Failed($"response status was '{status ?? "<missing>"}', expected 'interrupted'");

        var updateInterruptId = ReadString(data, "updateInterruptId");
        // Servers that echo the fence id must match the requested one; operation-id
        // based responses carry no fence id and are accepted as-is.
        if (updateInterruptId is not null
            && !string.Equals(updateInterruptId, expectedUpdateInterruptId, StringComparison.Ordinal))
        {
            return RunnerInterruptResult.Failed(
                "response updateInterruptId does not match the requested update interrupt");
        }

        if (!data.TryGetProperty("interruptedWorkIds", out var workIdsElement)
            || workIdsElement.ValueKind != JsonValueKind.Array)
        {
            return RunnerInterruptResult.Failed("response is missing interruptedWorkIds");
        }

        var workIds = new List<string>();
        foreach (var item in workIdsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                return RunnerInterruptResult.Failed("response contains an invalid interruptedWorkIds entry");
            workIds.Add(item.GetString()!);
        }

        if (!data.TryGetProperty("interruptedWorkCount", out var countElement)
            || !countElement.TryGetInt32(out var count)
            || count < 0)
        {
            return RunnerInterruptResult.Failed("response is missing a valid interruptedWorkCount");
        }
        if (count != workIds.Count)
            return RunnerInterruptResult.Failed("interruptedWorkCount does not match interruptedWorkIds");

        var operationId = ReadString(data, "operationId");
        DateTimeOffset? createdAt = null;
        if (data.TryGetProperty("createdAt", out var createdAtElement)
            && createdAtElement.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(createdAtElement.GetString(), out var parsedCreatedAt))
        {
            createdAt = parsedCreatedAt;
        }

        var affectedWorks = new List<RunnerUpdateWorkIdentity>();
        if (data.TryGetProperty("affectedWorks", out var affectedElement)
            && affectedElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in affectedElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    return RunnerInterruptResult.Failed("response contains an invalid affectedWorks entry");
                affectedWorks.Add(new RunnerUpdateWorkIdentity(
                    ReadString(item, "ownerKind") ?? "unknown",
                    ReadString(item, "ownerId") ?? string.Empty,
                    ReadString(item, "workId") ?? string.Empty,
                    ReadString(item, "taskRunId"),
                    ReadString(item, "workType") ?? string.Empty));
            }
            if (affectedWorks.Count != workIds.Count)
                return RunnerInterruptResult.Failed("affectedWorks does not match interruptedWorkIds");
        }

        if (affectedWorks.Count == 0 && workIds.Count > 0)
        {
            affectedWorks.AddRange(workIds.Select(workId => new RunnerUpdateWorkIdentity(
                "unknown",
                string.Empty,
                workId,
                null,
                string.Empty)));
        }

        return new RunnerInterruptResult(runnerId, status, updateInterruptId, workIds, count, null, operationId, createdAt, affectedWorks);

    }

    private static string? ReadString(JsonElement data, string property)
    {
        if (!data.TryGetProperty(property, out var element) || element.ValueKind == JsonValueKind.Null)
            return null;
        return element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
    }

    private ITimer? StartTimeoutTimer(CancellationTokenSource cts, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            cts.Cancel();
            return null;
        }

        return _timeProvider.CreateTimer(static state =>
        {
            try
            {
                ((CancellationTokenSource)state!).Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }, cts, timeout, Timeout.InfiniteTimeSpan);
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
            GetString("component"),
            GetString("version"),
            GetString("sourceRevision"),
            GetString("treeHash"),
            GetString("artifactDigest"),
            GetString("releaseId"),
            long.TryParse(GetString("generation"), out var generation) && generation > 0 ? generation : null,
            GetString("status") ?? "offline",
            GetDate("lastHeartbeatAt"),
            GetString("connectionState") ?? "disconnected",
            GetString("connectionGeneration"));
    }
}
