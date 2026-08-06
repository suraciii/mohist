namespace Mohist.Server.Infrastructure.Data.Events;

public static class IngressEventPersistence
{
    public const string SourcePrefix = "/mohist/projects/";

    public static string ConnectionSource(string projectId, string connectionId) =>
        $"{SourcePrefix}{projectId}/github-connections/{connectionId}";

    public static bool IsIngressSource(string source) =>
        source.StartsWith(SourcePrefix, StringComparison.Ordinal)
        && source.Contains("/github-connections/", StringComparison.Ordinal);

    /// <summary>
    /// Splits a GitHub ingress source
    /// (<c>/mohist/projects/{projectId}/github-connections/{connectionId}</c>)
    /// into its project and connection ids. Consumers resolve the
    /// connection by id and never trust the payload for routing.
    /// </summary>
    public static bool TryParseConnectionSource(string? source, out string projectId, out string connectionId)
    {
        projectId = string.Empty;
        connectionId = string.Empty;
        if (string.IsNullOrEmpty(source))
            return false;
        const string marker = "/github-connections/";
        var start = source.StartsWith(SourcePrefix, StringComparison.Ordinal) ? SourcePrefix.Length : 0;
        var markerIndex = source.IndexOf(marker, start, StringComparison.Ordinal);
        if (markerIndex <= start)
            return false;
        projectId = source[start..markerIndex];
        connectionId = source[(markerIndex + marker.Length)..];
        return projectId.Length > 0 && connectionId.Length > 0;
    }
}
