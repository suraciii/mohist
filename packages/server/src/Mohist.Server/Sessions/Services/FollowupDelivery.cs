using Mohist.Server.Contracts;
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
    IReadOnlyList<string> InputTexts,
    /// <summary>
    /// Accepted attachment descriptors for the dispatched turn. Empty
    /// when the turn is text-only. The Runner uses these to materialize
    /// the workspace and to build the honest, system-attributed manifest
    /// block; bytes are never carried over the wire — content is fetched
    /// via the owning-input scoped content route.
    /// </summary>
    IReadOnlyList<AgentSessionInputAttachmentDescriptor>? Attachments = null,
    string? InputId = null,
    AgentSlackExecutionContext? SlackExecutionContext = null,
    string? TurnId = null);

public sealed record FollowupDeliveryResult(bool Accepted);
