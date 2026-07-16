namespace Mohist.Server.Sessions.Services;

internal static class RuntimeEventTypes
{
    public const string SessionInput = "session.input";
    public const string SessionLiveness = "session.liveness";
    public const string SessionClosed = "session.closed";
    public const string SessionFollowupFailed = "session.followup_failed";
    public const string MessageDelta = "message.delta";
    public const string ReasoningDelta = "reasoning.delta";
    public const string ToolCallStarted = "tool_call.started";
    public const string ToolCallUpdated = "tool_call.updated";
    public const string ToolCallCompleted = "tool_call.completed";
    public const string UsageUpdated = "usage.updated";
    public const string ModelResolved = "model.resolved";
    public const string Compaction = "compaction";
    public const string CompactionEvent = "compaction_event";
    public const string ContextHealthUpdate = "context_health_update";
}

internal static class TranscriptPartTypes
{
    public const string Input = "input";
    public const string Text = "text";
    public const string Reasoning = "reasoning";
    public const string Tool = "tool";
    public const string Status = "status";
    public const string Usage = "usage";
    public const string Model = "model";
    public const string SessionClosed = "session.closed";
    public const string Compaction = "compaction";
}
