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
    private static readonly Regex AssetPathRegex = new(
        """(?:src|href)=["'](?<path>/assets/[^"']+)["']""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly HttpClient _http;
    private readonly TextWriter _out;

    public ServiceReadinessProbe(HttpClient http, TextWriter output)
    {
        _http = http;
        _out = output;
    }

    public async Task<ServerReadinessResult> WaitForServerReadyWithProgressAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        string? lastFailure = null;
        string? lastReason = null;
        var lastProgress = DateTimeOffset.UtcNow;
        int i = 0;

        while (!cts.IsCancellationRequested)
        {
            i++;
            var probe = new ReadinessProbeState();
            try
            {
                lastFailure = await CheckServerReadyOnceWithReasonAsync(cts.Token, probe);
                if (lastFailure is null)
                    return new ServerReadinessResult(true, null);
                lastReason = probe.Reason;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                lastFailure = FormatReadinessException(ex);
                lastReason ??= "waiting for Mohist API";
            }

            if (DateTimeOffset.UtcNow - lastProgress >= ServerReadyProgressInterval)
            {
                _out.WriteLine($"  waiting... {lastReason ?? "waiting for Mohist API"}");
                lastProgress = DateTimeOffset.UtcNow;
            }

            try
            {
                await Task.Delay(ServerReadyPollInterval, cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return new ServerReadinessResult(false, lastFailure);
    }

    public async Task<ServerReadinessResult> WaitForServerReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        string? lastFailure = null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        int i = 0;
        while (!cts.IsCancellationRequested)
        {
            i++;
            try
            {
                lastFailure = await CheckServerReadyOnceAsync(cts.Token);
                if (lastFailure is null)
                    return new ServerReadinessResult(true, null);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                lastFailure = FormatReadinessException(ex);
                // The service can be active before Kestrel starts accepting requests.
            }

            try
            {
                await Task.Delay(ServerReadyPollInterval, cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

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

    private static string? FindFirstAssetPath(string html)
    {
        var match = AssetPathRegex.Match(html);
        return match.Success ? match.Groups["path"].Value : null;
    }
}
