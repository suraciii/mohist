using Mohist.Server.Infrastructure;
using Mohist.Server.Sessions;

namespace Mohist.Server.Sessions.Services;

public sealed record GenericFollowupTarget(
    string RunnerId,
    string SessionId,
    bool IsActive);

public sealed record SessionStopTarget(
    string RunnerId,
    string SessionId,
    string SourceKind,
    string? WorkflowRunId,
    string? SessionName,
    string? Runtime,
    string? RuntimeSessionId,
    string? WorkDir);
