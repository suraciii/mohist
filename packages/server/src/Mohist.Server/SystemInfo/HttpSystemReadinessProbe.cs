using System.Net;
using System.Text.RegularExpressions;

namespace Mohist.Server.SystemInfo;

public interface ISystemReadinessProbe
{
    Task<SystemReadinessResult> ProbeAsync(CancellationToken cancellationToken = default);
}

public sealed record SystemReadinessResult(
    bool HealthReady,
    bool RootReady,
    bool AssetsReady,
    string? RootAssetPath,
    string? FailureReason);

public sealed class HttpSystemReadinessProbe : ISystemReadinessProbe
{
    private static readonly Regex AssetRegex = new("(?:src|href)=\"(?<path>/assets/[^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly HttpClient _httpClient;

    public HttpSystemReadinessProbe(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<SystemReadinessResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var health = await GetAsync("/api/health", cancellationToken);
        if (!health.IsSuccessStatusCode)
            return new SystemReadinessResult(false, false, false, null, "API health endpoint is not ready");

        var root = await GetAsync("/", cancellationToken);
        if (!root.IsSuccessStatusCode)
            return new SystemReadinessResult(true, false, false, null, "Web root is not ready");

        var html = await root.Content.ReadAsStringAsync(cancellationToken);
        var assetPath = AssetRegex.Match(html).Groups["path"].Value;
        if (string.IsNullOrWhiteSpace(assetPath))
            return new SystemReadinessResult(true, true, false, null, "Web root did not reference a bundled asset");

        var asset = await GetAsync(assetPath, cancellationToken);
        if (!asset.IsSuccessStatusCode)
            return new SystemReadinessResult(true, true, false, assetPath, "Bundled asset is not ready");

        return new SystemReadinessResult(true, true, true, assetPath, null);
    }

    private async Task<HttpResponseMessage> GetAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.GetAsync(path, cancellationToken);
        }
        catch
        {
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
    }
}