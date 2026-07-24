namespace Mohist.Server.Infrastructure.Data.Agent;

public sealed class WatchEntryRow
{
    public string ProjectId { get; set; } = string.Empty;
    public int IssueNumber { get; set; }
    public string AgentId { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
