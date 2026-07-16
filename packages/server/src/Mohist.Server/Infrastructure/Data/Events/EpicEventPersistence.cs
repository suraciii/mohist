namespace Mohist.Server.Infrastructure.Data.Events;

internal static class EpicEventPersistence
{
    // CloudEvents 1.0.2 source URI-reference. Format: /mohist/projects/{project}/epics/{number}.
    public const string SourcePrefix = "/mohist/epics/";
    public static string EpicSource(string projectId, int epicNumber) =>
        $"/mohist/projects/{projectId}/epics/{epicNumber}";
}
