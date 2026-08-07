using Mohist.Server.Sessions.Domain;
using Microsoft.AspNetCore.SignalR;
using Mohist.Server.Api;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Agent.Services;

public sealed class SessionTreeStopTargetAdapter(
    IGrainFactory grains,
    IHubContext<RunnerHub> runnerHub,
    RunnerConnectionTracker connections) : ISessionTreeStopTargetAdapter, IScopedService
{
    public async Task<SessionTreeStopTargetResult> StopAsync(
        string projectId,
        SessionTreeStopTargetSnapshot target,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(target.TurnId))
            return Result(target, SessionTreeStopTargetOutcome.AlreadyIdle, "no active turn");
        if (target.TurnStatus is AgentTurnStatus.Completed or AgentTurnStatus.Failed or AgentTurnStatus.Cancelled)
            return Result(target, SessionTreeStopTargetOutcome.AlreadyIdle, "turn already terminal");
        if (target.TurnStatus == AgentTurnStatus.Unknown)
            return Result(target, SessionTreeStopTargetOutcome.Unknown, "turn activity is unknown");

        var session = grains.GetGrain<IAgentSessionGrain>(target.SessionId);
        var current = await session.GetAsync();
        var control = await session.ResolveTurnControlAsync(target.TurnId);
        if (current is null || control is null || control.TurnId != target.TurnId)
            return Result(target, SessionTreeStopTargetOutcome.Rejected, "turn replaced");
        if (target.BindingEpoch > 0 && current.BindingEpoch != target.BindingEpoch)
            return Result(target, SessionTreeStopTargetOutcome.Rejected, "binding replaced");
        if (!string.Equals(current.RunnerId, target.RunnerId, StringComparison.Ordinal)
            || !string.Equals(current.Runtime, target.Runtime, StringComparison.Ordinal)
            || !string.Equals(current.AgentSessionId, target.RuntimeSessionId, StringComparison.Ordinal)
            || !string.Equals(current.WorkDir, target.WorkDir, StringComparison.Ordinal))
        {
            return Result(target, SessionTreeStopTargetOutcome.Rejected, "binding replaced");
        }

        if (control.Classification == AgentTurnControlClassification.Queued)
        {
            var cancelled = await AgentSessionTurnControlOperations.CancelAsync(
                grains,
                target.SessionId,
                target.TurnId);
            return cancelled.Kind switch
            {
                TurnControlResultKind.Cancelled => Result(target, SessionTreeStopTargetOutcome.Cancelled, "queued turn cancelled"),
                TurnControlResultKind.AlreadyEnded => cancelled.Status == AgentTurnStatus.Unknown
                    ? Result(target, SessionTreeStopTargetOutcome.Unknown, "turn became unknown")
                    : Result(target, SessionTreeStopTargetOutcome.AlreadyIdle, "turn already terminal"),
                _ => Result(target, SessionTreeStopTargetOutcome.Rejected, "queued turn was replaced"),
            };
        }

        var stopped = await AgentSessionTurnControlOperations.StopAsync(
            projectId,
            grains,
            runnerHub,
            connections,
            new SessionCancelTarget(
                target.RunnerId ?? string.Empty,
                target.SessionId,
                "agent-launch",
                null,
                null,
                target.Runtime,
                target.RuntimeSessionId,
                target.WorkDir),
            target.TurnId,
            cancellationToken,
            target.StopOperationId);

        return stopped.Kind switch
        {
            TurnControlResultKind.Stopped or TurnControlResultKind.NotCancellable =>
                Result(target, SessionTreeStopTargetOutcome.Cancelled, "executing turn stopped"),
            TurnControlResultKind.Unknown =>
                Result(target, SessionTreeStopTargetOutcome.Unknown, "runner could not confirm stop"),
            TurnControlResultKind.StopRequested =>
                Result(target, SessionTreeStopTargetOutcome.StopRequested, "stop request accepted for delivery"),
            TurnControlResultKind.RunnerUnavailable when stopped.DispatchStarted =>
                Result(target, SessionTreeStopTargetOutcome.Unknown, "runner response was not confirmed"),
            TurnControlResultKind.RunnerUnavailable =>
                Result(target, SessionTreeStopTargetOutcome.Pending, "runner was unavailable before dispatch"),
            TurnControlResultKind.AlreadyEnded when stopped.Status == AgentTurnStatus.Unknown =>
                Result(target, SessionTreeStopTargetOutcome.Unknown, "turn became unknown"),
            TurnControlResultKind.AlreadyEnded =>
                Result(target, SessionTreeStopTargetOutcome.AlreadyIdle, "turn already terminal"),
            _ => Result(target, SessionTreeStopTargetOutcome.Rejected, "turn control rejected the target"),
        };
    }

    private static SessionTreeStopTargetResult Result(
        SessionTreeStopTargetSnapshot target,
        SessionTreeStopTargetOutcome outcome,
        string detail) => new(target.SessionId, target.StopOperationId, outcome, detail);
}
