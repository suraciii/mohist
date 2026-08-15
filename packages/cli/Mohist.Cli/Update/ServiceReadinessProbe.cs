using System.Net;
using System.Text.RegularExpressions;

namespace Mohist.Cli;

internal sealed record ServerReadinessResult(bool Ready, string? LastFailure);

internal sealed class ReadinessProbeState
{
    public string? Reason { get; set; }
}

/// <summary>
/// Polls the freshly restarted Mohist server until it reports a usable stack (/api/health,
/// /, and a referenced /assets/* bundle) or the timeout elapses. Extracted from
/// <see cref="SourceCodeUpdater"/> so the facade no longer carries the readiness polling loop
/// alongside stage orchestration. Exposes a <see cref="ServerReadinessResult"/> DTO so callers
/// can format the timeout / failure reason in the stage machine without knowing the probe
/// internals.
/// </summary>
internal sealed class ServiceReadinessProbe
{
    private static readonly TimeSpan ServerReadyPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ServerReadyProgressInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FinalFailureProbeTimeout = TimeSpan.FromSeconds(1);
    private static readonly Regex AssetPathRegex = new(
        """(?:src|href)=["'](?<path>/assets/[^"']+)["']""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly HttpClient _http;
    private readonly TextWriter _out;
    private readonly TimeProvider _timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> _pollWait;

    public ServiceReadinessProbe(
        HttpClient http,
        TextWriter output,
        TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task>? pollWait = null)
    {
        _http = http;
        _out = output;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _pollWait = pollWait
            ?? ((delay, cancellationToken) => Task.Delay(delay, _timeProvider, cancellationToken));
    }

    internal TimeProvider TimeProvider => _timeProvider;
    internal Func<TimeSpan, CancellationToken, Task> PollWait => _pollWait;

    public async Task<ServerReadinessResult> WaitForServerReadyWithProgressAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var timeoutTimer = StartTimeoutTimer(timeoutCts, timeout);
        var waitToken = timeoutCts.Token;
        string? lastFailure = null;
        string? lastReason = null;
        var lastProgress = _timeProvider.GetUtcNow();
        var deadline = _timeProvider.GetUtcNow() + timeout;

        while (!waitToken.IsCancellationRequested && _timeProvider.GetUtcNow() < deadline)
        {
            var probe = new ReadinessProbeState();
            try
            {
                lastFailure = await CheckServerReadyOnceWithReasonAsync(waitToken, probe);
                if (lastFailure is null)
                    return new ServerReadinessResult(true, null);
                lastReason = probe.Reason;
            }
            catch (OperationCanceledException) when (waitToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                lastFailure = FormatReadinessException(ex);
                lastReason ??= "waiting for Mohist API";
            }

            var now = _timeProvider.GetUtcNow();
            if (now - lastProgress >= ServerReadyProgressInterval)
            {
                _out.WriteLine($"  waiting... {lastReason ?? "waiting for Mohist API"}");
                lastProgress = now;
            }

            if (deadline - _timeProvider.GetUtcNow() < ServerReadyPollInterval)
                break;

            try
            {
                await DelayAsync(ServerReadyPollInterval, waitToken);
            }
            catch (OperationCanceledException) when (waitToken.IsCancellationRequested)
            {
                break;
            }
        }

        lastFailure ??= await TryCaptureFinalFailureAsync(cancellationToken);
        return new ServerReadinessResult(false, lastFailure);
    }

    public async Task<ServerReadinessResult> WaitForServerReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var timeoutTimer = StartTimeoutTimer(timeoutCts, timeout);
        var waitToken = timeoutCts.Token;
        string? lastFailure = null;
        var deadline = _timeProvider.GetUtcNow() + timeout;
        while (!waitToken.IsCancellationRequested && _timeProvider.GetUtcNow() < deadline)
        {
            try
            {
                lastFailure = await CheckServerReadyOnceAsync(waitToken);
                if (lastFailure is null)
                    return new ServerReadinessResult(true, null);
            }
            catch (OperationCanceledException) when (waitToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                lastFailure = FormatReadinessException(ex);
                // The service can be active before Kestrel starts accepting requests.
            }

            if (deadline - _timeProvider.GetUtcNow() < ServerReadyPollInterval)
                break;

            try
            {
                await DelayAsync(ServerReadyPollInterval, waitToken);
            }
            catch (OperationCanceledException) when (waitToken.IsCancellationRequested)
            {
                break;
            }
        }

        lastFailure ??= await TryCaptureFinalFailureAsync(cancellationToken);
        return new ServerReadinessResult(false, lastFailure);
    }

    internal async Task<string?> CheckServerReadyOnceWithReasonAsync(CancellationToken ct, ReadinessProbeState state)
    {
        using var health = await _http.GetAsync("/api/health", HttpCompletionOption.ResponseHeadersRead, ct);
        if (!health.IsSuccessStatusCode)
        {
            state.Reason = "waiting for Mohist API";
            return FormatReadinessStatus("/api/health", health.StatusCode);
        }

        using var index = await _http.GetAsync("/", HttpCompletionOption.ResponseHeadersRead, ct);
        if (!index.IsSuccessStatusCode)
        {
            state.Reason = "waiting for Web assets";
            return FormatReadinessStatus("/", index.StatusCode);
        }

        var contentType = index.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(contentType, "text/html", StringComparison.OrdinalIgnoreCase))
        {
            state.Reason = "waiting for Web assets";
            return $"GET / returned content type {contentType ?? "unknown"}, expected text/html";
        }

        var html = await index.Content.ReadAsStringAsync(ct);
        var assetPath = FindFirstAssetPath(html);
        if (assetPath is null)
        {
            state.Reason = "waiting for Web assets";
            return "GET / did not reference a /assets/* bundle";
        }

        using var asset = await _http.GetAsync(assetPath, HttpCompletionOption.ResponseHeadersRead, ct);
        if (asset.StatusCode != HttpStatusCode.OK)
        {
            state.Reason = "waiting for Web assets";
            return FormatReadinessStatus(assetPath, asset.StatusCode);
        }

        state.Reason = null;
        return null;
    }

    internal async Task<string?> CheckServerReadyOnceAsync(CancellationToken ct)
    {
        using var health = await _http.GetAsync("/api/health", HttpCompletionOption.ResponseHeadersRead, ct);
        if (!health.IsSuccessStatusCode)
            return FormatReadinessStatus("/api/health", health.StatusCode);

        using var index = await _http.GetAsync("/", HttpCompletionOption.ResponseHeadersRead, ct);
        if (!index.IsSuccessStatusCode)
            return FormatReadinessStatus("/", index.StatusCode);

        var contentType = index.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(contentType, "text/html", StringComparison.OrdinalIgnoreCase))
            return $"GET / returned content type {contentType ?? "unknown"}, expected text/html";

        var html = await index.Content.ReadAsStringAsync(ct);
        var assetPath = FindFirstAssetPath(html);
        if (assetPath is null)
            return "GET / did not reference a /assets/* bundle";

        using var asset = await _http.GetAsync(assetPath, HttpCompletionOption.ResponseHeadersRead, ct);
        return asset.StatusCode == HttpStatusCode.OK ? null : FormatReadinessStatus(assetPath, asset.StatusCode);
    }

    private static string FormatReadinessStatus(string path, HttpStatusCode statusCode)
        => $"GET {path} returned {(int)statusCode} {statusCode}";

    private static string FormatReadinessException(Exception ex)
    {
        var message = ex.InnerException is null
            ? ex.Message
            : $"{ex.Message} ({ex.InnerException.Message})";
        return $"{ex.GetType().Name}: {message}";
    }

    private async Task<string?> TryCaptureFinalFailureAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return null;

        using var finalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var finalTimer = StartTimeoutTimer(finalCts, FinalFailureProbeTimeout);

        try
        {
            return await CheckServerReadyOnceAsync(finalCts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            return FormatReadinessException(ex);
        }
    }

    private Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        delay <= TimeSpan.Zero
            ? Task.CompletedTask
            : _pollWait(delay, cancellationToken);

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

    private static string? FindFirstAssetPath(string html)
    {
        var match = AssetPathRegex.Match(html);
        return match.Success ? match.Groups["path"].Value : null;
    }
}
