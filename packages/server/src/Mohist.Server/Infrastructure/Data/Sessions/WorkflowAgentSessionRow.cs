namespace Mohist.Server.Infrastructure.Data.Sessions;

public class WorkflowAgentSessionRow
{
    public string Id { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public int IssueNumber { get; set; }
    public string WorkflowRunId { get; set; } = string.Empty;
    public string SessionName { get; set; } = string.Empty;
    public string? WorkId { get; set; }
    public string? WorkType { get; set; }
    public string? Stage { get; set; }
    public string? Title { get; set; }
    public string? RunnerId { get; set; }
    public string? AgentSessionId { get; set; }
    public string Status { get; set; } = "created";
    public string? Model { get; set; }
    public string? WorkDir { get; set; }
    public string? ChangeDir { get; set; }
    public int? ProcessPid { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? LastDataAt { get; set; }
    public DateTime? LastHeartbeatAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? FailureReason { get; set; }
    public int? ExitCode { get; set; }

    public string? ResolvedModel { get; set; }
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public long? TotalTokens { get; set; }
    public long? CachedReadTokens { get; set; }
    public long? ThoughtTokens { get; set; }
    public double? CostAmount { get; set; }
    public string? CostCurrency { get; set; }
    public long? ContextWindowUsed { get; set; }
    public long? ContextWindowSize { get; set; }
    public string? FailureCategory { get; set; }
    public int? ToolCallCount { get; set; }
    public int? ToolErrorCount { get; set; }
}
