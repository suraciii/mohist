using Mohist.Server.Sessions.Domain;
using Mohist.Server.Infrastructure;

namespace Mohist.Server.Sessions.Services;

public interface IFollowupDeliveryDispatcher
{
    Task<FollowupDeliveryResult> DispatchAsync(FollowupDeliveryRequest request, CancellationToken ct = default);
}

public interface IFollowupDispatchScheduler
{
    void Schedule(string projectId, string sessionId);
}

public sealed record FollowupDeliveryRequest(
    string ProjectId,
    string SessionId,
    string SourceKind,
    string? WorkflowRunId,
    string? SessionName,
    string RunnerId,
    string Runtime,
    string RuntimeSessionId,
    string? WorkDir,
    AgentExecutionDefinition? Definition,
    string OperationId,
    IReadOnlyList<string> InputTexts);

public sealed record FollowupDeliveryResult(bool Accepted);
