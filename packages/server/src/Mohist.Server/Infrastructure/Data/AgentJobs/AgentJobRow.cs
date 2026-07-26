namespace Mohist.Server.Infrastructure.Data.AgentJobs;

public class AgentJobRow
{
    public string JobKey { get; set; } = string.Empty;
    public string State { get; set; } = "{}";

    public string? ProjectId { get; set; }
    public string? AgentId { get; set; }
    public string? Status { get; set; }
    public string? SubmittedAt { get; set; }
    public string? TerminalAt { get; set; }
}
