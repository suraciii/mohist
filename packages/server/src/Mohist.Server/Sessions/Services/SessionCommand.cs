using System.Text.Json.Serialization;

namespace Mohist.Server.Sessions.Services;

public enum SessionCommandKind
{
    Compact,
    Reset,
}

public enum SessionCommandError
{
    Conflict,
    Missing,
    NotStarted,
    Unavailable,
}

[GenerateSerializer]
public sealed record SessionCommandRequest(
    [property: Id(0)] string SessionId,
    [property: Id(1)] string Runtime,
    [property: Id(2)] string? RuntimeSessionId,
    [property: Id(3)] string RunnerId,
    [property: Id(4), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? WorkDir,
    [property: Id(5)] SessionCommandKind Command,
    [property: Id(6)] string? ExpectedRuntimeSessionId = null,
    [property: Id(7)] string OperationId = "",
    [property: Id(8)] string? ProjectId = null);

public sealed record SessionCommandResult(
    bool Ok,
    string? RuntimeSessionId = null,
    SessionCommandError? Error = null);

public interface ISessionCommandDispatcher
{
    Task<SessionCommandResult> DispatchAsync(
        SessionCommandRequest request,
        CancellationToken ct = default);
}
