namespace Mohist.Server.Infrastructure.Data.Events;

internal static class AgentSessionEventPersistence
{
    // CloudEvents 1.0.2 source URI-reference. Format: /{context}/{aggregate}/{id}.
    public const string SourcePrefix = "/mohist/agent-session/";
    public static string AgentSessionSource(string sessionId) => $"{SourcePrefix}{sessionId}";
}