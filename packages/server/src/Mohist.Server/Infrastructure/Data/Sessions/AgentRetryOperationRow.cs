namespace Mohist.Server.Infrastructure.Data.Sessions;

/// <summary>
/// Durable receipt for one accepted Agent Session retry. The row is written
/// before the execution path is entered; the pre-allocated ids make a replay
/// address the same attempt even after the process that accepted the retry is
/// gone.
/// </summary>
public sealed class AgentRetryOperationRow
{
    public string OperationId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string TurnId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string PreAllocatedSessionId { get; set; } = string.Empty;
    public string PreAllocatedInputId { get; set; } = string.Empty;
    public string PreAllocatedTurnId { get; set; } = string.Empty;
    public string State { get; set; } = "pending";
    public string? ResultState { get; set; }
    public string? ResultText { get; set; }
    public string? ResultJobKey { get; set; }
    public string? ResultSessionId { get; set; }
    public string? ResultInputId { get; set; }
    public string? ResultTurnId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}
