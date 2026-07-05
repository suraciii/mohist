namespace Mohist.Server.Infrastructure.Data.Events;

internal static class EpicEventPersistence
{
    // CloudEvents 1.0.2 source URI-reference. Format: /{context}/{aggregate}/{id}.
    public const string SourcePrefix = "/mohist/epics/";
    public static string EpicSource(string epicId) => $"{SourcePrefix}{epicId}";
}