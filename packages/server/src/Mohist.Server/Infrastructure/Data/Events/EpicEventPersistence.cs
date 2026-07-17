namespace Mohist.Server.Infrastructure.Data.Events;

internal static class EpicEventPersistence
{
    public static string EpicSource(string projectId, int epicNumber) =>
        $"/mohist/projects/{projectId}/epics/{epicNumber}";

    public static bool IsEpicSource(string source) =>
        source.StartsWith("/mohist/projects/", StringComparison.Ordinal)
        && source.Contains("/epics/", StringComparison.Ordinal);
}
