namespace Mohist.Server.Infrastructure.Slack;

public sealed class SlackProviderInboxCapacityExceededException(string projectId, string connectionId, int capacity)
    : Exception($"Provider inbox for connection '{connectionId}' in project '{projectId}' is at capacity ({capacity}).")
{
    public string ProjectId { get; } = projectId;
    public string ConnectionId { get; } = connectionId;
    public int Capacity { get; } = capacity;
}

public sealed class SlackOutboxCapacityExceededException(string projectId, string connectionId, int capacity)
    : Exception($"Outbound outbox for connection '{connectionId}' in project '{projectId}' is at capacity ({capacity}); non-replaceable rows cannot be silently dropped.")
{
    public string ProjectId { get; } = projectId;
    public string ConnectionId { get; } = connectionId;
    public int Capacity { get; } = capacity;
}

public sealed class SlackOutboxRowNotFoundException(string rowId)
    : Exception($"Outbox row '{rowId}' was not found.")
{
    public string RowId { get; } = rowId;
}

public sealed class SlackOutboxStateException(string rowId, string expectedState, string actualState)
    : Exception($"Outbox row '{rowId}' is in state '{actualState}'; expected '{expectedState}'.")
{
    public string RowId { get; } = rowId;
    public string ExpectedState { get; } = expectedState;
    public string ActualState { get; } = actualState;
}
