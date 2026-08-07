using System.Text.Json.Serialization;
using Mohist.Server.Infrastructure;

namespace Mohist.Server.Sessions.Domain;

/// <summary>
/// 定时输入的一次投递目标：到期后由 Session grain 经既有 follow-up
/// 受理路径转成一条普通 SessionInput。状态机：
/// scheduled -> pending-delivery -> delivered；scheduled /
/// pending-delivery -> cancelled。delivered 与 cancelled 是终态；
/// pending-delivery 是持久「欠投递」状态，阻塞恢复后由 recovery
/// reminder 重试，不随时间自动过期。
/// </summary>
[GenerateSerializer]
public sealed record SessionScheduleRecord(
    [property: Id(0)] string ScheduleId,
    [property: Id(1)] DateTime DueAt,
    [property: Id(2)] string Text,
    [property: Id(3)] SessionScheduleStatus Status,
    [property: Id(4)] string IdempotencyKey,
    [property: Id(5)] DateTime CreatedAt,
    [property: Id(6)] DateTime? CancelledAt = null,
    [property: Id(7)] string? InputId = null)
{
    public bool IsTerminal => Status is SessionScheduleStatus.Delivered or SessionScheduleStatus.Cancelled;
}

public enum SessionScheduleStatus
{
    Scheduled,
    PendingDelivery,
    Delivered,
    Cancelled,
}

[Serializable]
[GenerateSerializer]
public sealed class ScheduleDueInPastException : InvalidOperationException
{
    public ScheduleDueInPastException(string sessionId, DateTime dueAt)
        : base($"AgentSession {sessionId} schedule dueAt {dueAt:O} is not strictly after the server's current time.")
    {
        SessionId = sessionId;
        DueAt = dueAt;
    }

    [Id(0)]
    public string SessionId { get; }
    [Id(1)]
    public DateTime DueAt { get; }
}

[Serializable]
[GenerateSerializer]
public sealed class ScheduleIdempotencyConflictException : InvalidOperationException
{
    public ScheduleIdempotencyConflictException(string sessionId, string idempotencyKey)
        : base($"AgentSession {sessionId} already has a schedule for idempotency key '{idempotencyKey}' with different content.")
    {
        SessionId = sessionId;
        IdempotencyKey = idempotencyKey;
    }

    [Id(0)]
    public string SessionId { get; }
    [Id(1)]
    public string IdempotencyKey { get; }
}

[Serializable]
[GenerateSerializer]
public sealed class ScheduleNotFoundException : InvalidOperationException
{
    public ScheduleNotFoundException(string sessionId, string scheduleId)
        : base($"AgentSession {sessionId} has no schedule '{scheduleId}'.")
    {
        SessionId = sessionId;
        ScheduleId = scheduleId;
    }

    [Id(0)]
    public string SessionId { get; }
    [Id(1)]
    public string ScheduleId { get; }
}
