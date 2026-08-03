namespace Mohist.Server.Infrastructure.Data.Slack;

public static class SlackManagerToolExecutionFenceStates
{
    public const string Started = "started";
    public const string Completed = "completed";
}

public sealed class SlackManagerToolExecutionFenceRow
{
    public string JobKey { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string State { get; set; } = SlackManagerToolExecutionFenceStates.Started;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
