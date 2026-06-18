namespace Mohist.Server.Infrastructure.Data.Agent;

public class AgentRow
{
    public string Id { get; set; } = string.Empty;
    public string State { get; set; } = "{}";
    public string? ProjectId { get; set; }
    public string? Name { get; set; }
    public string? Status { get; set; }
}
