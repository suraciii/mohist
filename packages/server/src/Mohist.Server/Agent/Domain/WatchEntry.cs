namespace Mohist.Server.Agent.Domain;

public sealed class WatchEntry
{
    public string ProjectId { get; set; } = string.Empty;
    public int IssueNumber { get; set; }
    public string AgentId { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public static class WatchEntryState
{
    public const string Watching = "watching";
    public const string Muted = "muted";
}
