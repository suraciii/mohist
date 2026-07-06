namespace Mohist.Server.Infrastructure.Data.Agent;

public class AgentSubscriptionRow
{
    public string Id { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string FilterType { get; set; } = string.Empty;
    public string? FilterSource { get; set; }
    public string? FilterSubject { get; set; }
    public string ResponsePrompt { get; set; } = string.Empty;
    public int? Priority { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
