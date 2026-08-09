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
    RunnerConnectionTracker connections,
    WorkflowSessionWorkReconciler workReconciler) : ISessionTreeStopTargetAdapter, IScopedService
{
    public async Task<SessionTreeStopTargetResult> StopAsync(
        string projectId,
        SessionTreeStopTargetSnapshot target,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(target.TurnId))
        {
            await workReconciler.ReconcileAsync(projectId, target.SessionId, target.RunnerId, "session-stop", cancellationToken);
            return Result(target, SessionTreeStopTargetOutcome.AlreadyIdle, "no active turn");
        }
        if (target.TurnStatus is AgentTurnStatus.Completed or AgentTurnStatus.Failed or AgentTurnStatus.Cancelled)
        {
            await workReconciler.ReconcileAsync(projectId, target.SessionId, target.RunnerId, "session-stop", cancellationToken);
            return Result(target, SessionTreeStopTargetOutcome.AlreadyIdle, "turn already terminal");
        }
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
            if (cancelled.Kind == TurnControlResultKind.Cancelled)
                return await ReconcileAndResultAsync(
                    projectId,
                    target,
                    SessionTreeStopTargetOutcome.Cancelled,
                    "queued turn cancelled",
                    "session-cancel",
                    cancellationToken);
            if (cancelled.Kind == TurnControlResultKind.AlreadyEnded)
            {
                if (cancelled.Status == AgentTurnStatus.Unknown)
                    return Result(target, SessionTreeStopTargetOutcome.Unknown, "turn became unknown");
                return await ReconcileAndResultAsync(
                    projectId,
                    target,
                    SessionTreeStopTargetOutcome.AlreadyIdle,
                    "turn already terminal",
                    "session-stop",
                    cancellationToken);
            }
            return Result(target, SessionTreeStopTargetOutcome.Rejected, "queued turn was replaced");
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

        if (stopped.Kind == TurnControlResultKind.Stopped)
        {
            await workReconciler.ReconcileAsync(projectId, target.SessionId, target.RunnerId, "session-stop", cancellationToken);
            return Result(target, SessionTreeStopTargetOutcome.Cancelled, "executing turn stopped");
        }

        if (stopped.Kind == TurnControlResultKind.NotCancellable)
            return Result(target, SessionTreeStopTargetOutcome.Cancelled, "executing turn stopped");

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
            return await ReconcileAndResultAsync(
                projectId,
                target,
                SessionTreeStopTargetOutcome.AlreadyIdle,
                "turn already terminal",
                "session-stop",
                cancellationToken);
        }
        return Result(target, SessionTreeStopTargetOutcome.Rejected, "turn control rejected the target");
    }

    private async Task<SessionTreeStopTargetResult> ReconcileAndResultAsync(
        string projectId,
        SessionTreeStopTargetSnapshot target,
        SessionTreeStopTargetOutcome outcome,
        string detail,
        string reason,
        CancellationToken cancellationToken)
    {
        await workReconciler.ReconcileAsync(projectId, target.SessionId, target.RunnerId, reason, cancellationToken);
        return Result(target, outcome, detail);
    }

    private static SessionTreeStopTargetResult Result(
        SessionTreeStopTargetSnapshot target,
        SessionTreeStopTargetOutcome outcome,
        string detail) => new(target.SessionId, target.StopOperationId, outcome, detail);
}
