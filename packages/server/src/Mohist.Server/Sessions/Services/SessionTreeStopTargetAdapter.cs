using Mohist.Server.Sessions.Domain;
using Microsoft.AspNetCore.SignalR;
using Mohist.Server.Api;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Runner.Services;
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

        var stopped = await AgentSessionStopOperations.StopAsync(
            projectId,
            grains,
            runnerHub,
            connections,
            new SessionStopTarget(
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

        if (stopped.Kind == TurnControlResultKind.Cancelled)
            return Result(target, SessionTreeStopTargetOutcome.Cancelled, "turn cancelled");

        if (stopped.Kind == TurnControlResultKind.Stopped)
            return Result(target, SessionTreeStopTargetOutcome.Cancelled, "executing turn stopped");

        if (stopped.Kind == TurnControlResultKind.NotCancellable)
            return Result(target, SessionTreeStopTargetOutcome.NotCancellable, "runtime reported not-cancellable");

        if (stopped.Kind == TurnControlResultKind.Unknown)
            return Result(target, SessionTreeStopTargetOutcome.Unknown, "runner could not confirm stop");
        if (stopped.Kind == TurnControlResultKind.StopRequested)
            return Result(target, SessionTreeStopTargetOutcome.StopRequested, "stop request accepted for delivery");
        if (stopped.Kind == TurnControlResultKind.RunnerUnavailable)
        {
            return Result(
                target,
                stopped.DispatchStarted
                    ? SessionTreeStopTargetOutcome.Unknown
                    : SessionTreeStopTargetOutcome.Pending,
                stopped.DispatchStarted
                    ? "runner response was not confirmed"
                    : "runner was unavailable before dispatch");
        }
        if (stopped.Kind == TurnControlResultKind.AlreadyEnded)
        {
            if (stopped.Status == AgentTurnStatus.Unknown)
                return Result(target, SessionTreeStopTargetOutcome.Unknown, "turn became unknown");
            return Result(target, SessionTreeStopTargetOutcome.AlreadyIdle, "turn already terminal");
        }
        return Result(target, SessionTreeStopTargetOutcome.Rejected, "turn control rejected the target");
    }

    private static SessionTreeStopTargetResult Result(
        SessionTreeStopTargetSnapshot target,
        SessionTreeStopTargetOutcome outcome,
        string detail) => new(target.SessionId, target.StopOperationId, outcome, detail);
}
