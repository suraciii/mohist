using Mohist.Server.Infrastructure;
using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Sessions.Services;

public sealed record CanonicalFollowupTarget(
    string RunnerId,
    string SessionId,
    string SourceKind,
    string? WorkflowRunId,
    string? SessionName,
    string? Runtime,
    string? RuntimeSessionId,
    string? WorkDir,
    AgentExecutionDefinition? Definition = null,
    string? ProjectId = null,
    string? AgentId = null);
