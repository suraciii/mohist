namespace Mohist.Server.Infrastructure.Data.Events;

public static class IngressEventPersistence
{
    public const string SourcePrefix = "/mohist/projects/";

    public static string ConnectionSource(string projectId, string connectionId) =>
        $"{SourcePrefix}{projectId}/github-connections/{connectionId}";

    public static bool IsIngressSource(string source) =>
        source.StartsWith(SourcePrefix, StringComparison.Ordinal)
        && source.Contains("/github-connections/", StringComparison.Ordinal);
}
