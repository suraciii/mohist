using Mohist.Server.Agent.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Api;

internal enum TurnControlResultKind
{
    NotFound,
    Cancelled,
    AlreadyEnded,
    StopRequested,
    Stopped,
    Unknown,
    NotCancellable,
    Blocked,
    RunnerUnavailable,
}

internal sealed record TurnControlResult(
    TurnControlResultKind Kind,
    AgentTurnStatus? Status = null,
    bool? InterruptUnconfirmed = null,
    string? StatusText = null,
    bool DispatchStarted = false);

internal static class AgentSessionStopOperations
{
    public static async Task<TurnControlResult> StopAsync(
        string projectId,
        IGrainFactory grains,
        ISessionStopDelivery delivery,
        SessionStopTarget target,
        string turnId,
        CancellationToken ct,
        string? expectedOperationId = null)
    {
        var session = grains.GetGrain<IAgentSessionGrain>(target.SessionId);
        var claim = await session.ClaimTurnStopAsync(turnId, expectedOperationId);
        var control = claim.Control;
        if (control is null)
            return new(TurnControlResultKind.NotFound);
        if (control.Classification == AgentTurnControlClassification.Terminal && !claim.CanDispatch)
            return new(TurnControlResultKind.AlreadyEnded, control.Status);

        if (control.Classification == AgentTurnControlClassification.Queued)
        {
            var queued = await session.StopQueuedTurnAsync(turnId);
            if (queued.Cancelled)
                return new(TurnControlResultKind.Cancelled, queued.Control?.Status ?? AgentTurnStatus.Cancelled);
            if (queued.Control?.Classification == AgentTurnControlClassification.Terminal)
                return new(TurnControlResultKind.AlreadyEnded, queued.Control.Status);

            if (control.IsLaunchTurn && control.JobId is not null)
            {
                var jobResult = await grains.GetGrain<IAgentJobGrain>(control.JobId).CancelAsync();
                return jobResult.Disposition switch
                {
                    AgentJobCancelDisposition.Cancelled => new(TurnControlResultKind.Cancelled, AgentTurnStatus.Cancelled),
                    AgentJobCancelDisposition.Executing => new(TurnControlResultKind.StopRequested, AgentTurnStatus.Executing),
                    _ => new(
                        TurnControlResultKind.AlreadyEnded,
                        StatusText: jobResult.Status.ToString().ToLowerInvariant()),
                };
            }

            return new(TurnControlResultKind.StopRequested, queued.Control?.Status);
        }

        if (!claim.CanDispatch || string.IsNullOrWhiteSpace(claim.OperationId))
            return new(TurnControlResultKind.StopRequested, control.Status);

        await session.MarkTurnStopDispatchedAsync(control.TurnId, claim.OperationId);
        var result = SessionStopDeliveryArbitration.Interpret(await delivery.DispatchAsync(
            new SessionStopDeliveryRequest(
                projectId,
                target.SessionId,
                control.TurnId,
                claim.OperationId,
                target.RunnerId,
                target.SourceKind,
                target.WorkflowRunId,
                target.SessionName,
                target.Runtime,
                target.RuntimeSessionId,
                target.WorkDir),
            ct));

        if (result.Disposition == AgentSessionStopDisposition.Unavailable)
            return new(TurnControlResultKind.RunnerUnavailable, control.Status, DispatchStarted: result.DispatchStarted);

        await session.ApplyStopDeliveryAsync(
            control.TurnId,
            claim.OperationId,
            result.Disposition);

        return result.Disposition switch
        {
            AgentSessionStopDisposition.Stopped => new(
                TurnControlResultKind.Stopped,
                AgentTurnStatus.Cancelled,
                result.InterruptUnconfirmed,
                DispatchStarted: result.DispatchStarted),
            AgentSessionStopDisposition.Unknown => new(
                TurnControlResultKind.Unknown,
                AgentTurnStatus.Unknown,
                result.InterruptUnconfirmed,
                DispatchStarted: result.DispatchStarted),
            AgentSessionStopDisposition.NotCancellable => new(
                TurnControlResultKind.NotCancellable,
                AgentTurnStatus.Executing,
                result.InterruptUnconfirmed,
                DispatchStarted: result.DispatchStarted),
            AgentSessionStopDisposition.Ended => new(
                TurnControlResultKind.AlreadyEnded,
                control.Status,
                result.InterruptUnconfirmed,
                DispatchStarted: result.DispatchStarted),
            AgentSessionStopDisposition.Blocked => new(
                TurnControlResultKind.Blocked,
                AgentTurnStatus.Unknown,
                result.InterruptUnconfirmed,
                DispatchStarted: result.DispatchStarted),
            _ => new(
                TurnControlResultKind.StopRequested,
                control.Status,
                result.InterruptUnconfirmed,
                DispatchStarted: result.DispatchStarted),
        };
    }
}
