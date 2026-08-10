using Microsoft.AspNetCore.SignalR;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Runner.Services.SignalR;
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
        IHubContext<RunnerHub> runnerHub,
        RunnerConnectionTracker connections,
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

        var connectionId = connections.GetConnectionId(target.RunnerId);
        if (string.IsNullOrWhiteSpace(connectionId)
            || string.IsNullOrWhiteSpace(target.Runtime)
            || string.IsNullOrWhiteSpace(target.RuntimeSessionId)
            || string.IsNullOrWhiteSpace(target.WorkDir))
        {
            return new(TurnControlResultKind.RunnerUnavailable, control.Status, DispatchStarted: false);
        }

        object binding = new
        {
            runtime = target.Runtime,
            runtimeSessionId = target.RuntimeSessionId,
            runnerId = target.RunnerId,
            workDir = target.WorkDir,
        };
        object wireTarget = string.Equals(target.SourceKind, "workflow", StringComparison.Ordinal)
            ? new
            {
                kind = "workflow",
                projectId,
                workflowRunId = target.WorkflowRunId,
                sessionName = target.SessionName,
                binding,
            }
            : new
            {
                kind = "generic",
                projectId,
                sessionId = target.SessionId,
                binding,
            };

        await session.MarkTurnStopDispatchedAsync(control.TurnId, claim.OperationId);

        RunnerStopReply? reply;
        try
        {
            reply = await runnerHub.Clients.Client(connectionId).InvokeAsync<RunnerStopReply?>(
                "CancelAgentSession",
                new { target = wireTarget, sessionId = target.SessionId, turnId, operationId = claim.OperationId },
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new(TurnControlResultKind.RunnerUnavailable, control.Status, DispatchStarted: true);
        }

        if (reply is null)
            return new(TurnControlResultKind.RunnerUnavailable, control.Status, DispatchStarted: true);

        if (string.Equals(reply.State, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            if (control.IsLaunchTurn && control.JobId is not null)
            {
                await grains.GetGrain<IAgentJobGrain>(control.JobId).MarkUnknownAsync("stop-unconfirmed");
            }
            else
            {
                await session.MarkTurnTerminalAsync(control.TurnId, AgentTurnStatus.Unknown, null);
            }

            return new(TurnControlResultKind.Unknown, AgentTurnStatus.Unknown, reply.InterruptUnconfirmed);
        }

        if (string.Equals(reply.State, "stopped", StringComparison.OrdinalIgnoreCase))
        {
            await session.CompleteTurnStopAsync(control.TurnId, claim.OperationId);
            return new(TurnControlResultKind.Stopped, AgentTurnStatus.Cancelled, reply.InterruptUnconfirmed);
        }

        if (string.Equals(reply.State, "not-cancellable", StringComparison.OrdinalIgnoreCase))
        {
            await session.ReleaseTurnStopAsync(control.TurnId, claim.OperationId);
            return new(TurnControlResultKind.NotCancellable, AgentTurnStatus.Executing, reply.InterruptUnconfirmed);
        }

        return new(TurnControlResultKind.StopRequested, control.Status, reply.InterruptUnconfirmed);
    }
}
