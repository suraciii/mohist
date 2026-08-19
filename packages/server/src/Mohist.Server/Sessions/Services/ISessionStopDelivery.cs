using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Sessions.Services;

public sealed record SessionStopDeliveryRequest(
    string ProjectId,
    string SessionId,
    string TurnId,
    string OperationId,
    string RunnerId,
    string SourceKind,
    string? WorkflowRunId,
    string? SessionName,
    string? Runtime,
    string? RuntimeSessionId,
    string? WorkDir);

public interface ISessionStopDelivery
{
    Task<SessionStopDeliveryResponse> DispatchAsync(
        SessionStopDeliveryRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Interprets the runner's stable stop reply vocabulary before either the API
/// or the recovery reminder updates the owning session.
/// </summary>
public static class SessionStopDeliveryArbitration
{
    public static SessionStopDeliveryResult Interpret(SessionStopDeliveryResponse response)
    {
        var disposition = response.Reply?.State?.ToLowerInvariant() switch
        {
            "stopped" => AgentSessionStopDisposition.Stopped,
            "unknown" => AgentSessionStopDisposition.Unknown,
            "not-cancellable" => AgentSessionStopDisposition.NotCancellable,
            "ended" => AgentSessionStopDisposition.Ended,
            "idle" => AgentSessionStopDisposition.Idle,
            null => AgentSessionStopDisposition.Unavailable,
            _ => AgentSessionStopDisposition.StopRequested,
        };

        return new SessionStopDeliveryResult(
            disposition,
            response.Reply?.InterruptUnconfirmed,
            response.DispatchStarted);
    }
}

public sealed record SessionStopDeliveryResponse(
    RunnerStopReply? Reply,
    bool DispatchStarted);

public sealed record RunnerStopReply(string? State, bool? InterruptUnconfirmed = null);

public sealed record SessionStopDeliveryResult(
    AgentSessionStopDisposition Disposition,
    bool? InterruptUnconfirmed,
    bool DispatchStarted);
