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
    Executing,
    StopRequested,
    Stopped,
    Unknown,
    Queued,
    NotCancellable,
    RunnerUnavailable,
}

internal sealed record TurnControlResult(
    TurnControlResultKind Kind,
    AgentTurnStatus? Status = null,
    bool? InterruptUnconfirmed = null,
    string? StatusText = null);

internal static class AgentSessionTurnControlOperations
{
    public static async Task<TurnControlResult> CancelAsync(
        IGrainFactory grains,
        string sessionId,
        string turnId)
    {
        var session = grains.GetGrain<IAgentSessionGrain>(sessionId);
        var cancellation = await session.CancelQueuedTurnAsync(turnId);
        var control = cancellation.Control;
        if (control is null)
            return new(TurnControlResultKind.NotFound);
        if (cancellation.Cancelled)
            return new(TurnControlResultKind.Cancelled, control.Status);
        if (control.Classification == AgentTurnControlClassification.Terminal)
            return new(TurnControlResultKind.AlreadyEnded, control.Status);
        if (control.Classification == AgentTurnControlClassification.Executing)
            return new(TurnControlResultKind.Executing, control.Status);
        if (control.IsLaunchTurn)
        {
            var result = await grains.GetGrain<IAgentJobGrain>(control.JobId!).CancelAsync();
            return result.Disposition switch
            {
                AgentJobCancelDisposition.Cancelled => new(TurnControlResultKind.Cancelled),
                AgentJobCancelDisposition.Executing => new(TurnControlResultKind.Executing),
                _ => new(
                    TurnControlResultKind.AlreadyEnded,
                    StatusText: result.Status.ToString().ToLowerInvariant()),
            };
        }
        return new(TurnControlResultKind.Cancelled, control.Status);
    }

    public static async Task<TurnControlResult> StopAsync(
        string projectId,
        IGrainFactory grains,
        IHubContext<RunnerHub> runnerHub,
        RunnerConnectionTracker connections,
        SessionCancelTarget target,
        string turnId,
        CancellationToken ct)
    {
        var session = grains.GetGrain<IAgentSessionGrain>(target.SessionId);
        var claim = await session.ClaimTurnStopAsync(turnId);
        var control = claim.Control;
        if (control is null)
            return new(TurnControlResultKind.NotFound);
        if (control.Classification == AgentTurnControlClassification.Terminal && !claim.CanDispatch)
            return new(TurnControlResultKind.AlreadyEnded, control.Status);
        if (control.Classification == AgentTurnControlClassification.Queued)
            return new(TurnControlResultKind.Queued, control.Status);
        if (!claim.CanDispatch || string.IsNullOrWhiteSpace(claim.OperationId))
            return new(TurnControlResultKind.StopRequested, control.Status);

        var runnerId = connections.GetConnectionId(target.RunnerId);
        if (string.IsNullOrWhiteSpace(runnerId)
            || string.IsNullOrWhiteSpace(target.Runtime)
            || string.IsNullOrWhiteSpace(target.RuntimeSessionId)
            || string.IsNullOrWhiteSpace(target.WorkDir))
        {
            await session.AbandonUndispatchedTurnStopAsync(control.TurnId, claim.OperationId);
            return new(TurnControlResultKind.RunnerUnavailable, control.Status);
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
            reply = await runnerHub.Clients.Client(runnerId).InvokeAsync<RunnerStopReply?>(
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
            return new(TurnControlResultKind.RunnerUnavailable, control.Status);
        }

        if (reply is null)
            return new(TurnControlResultKind.RunnerUnavailable, control.Status);

        if (control.IsLaunchTurn
            && string.Equals(reply.State, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            await grains.GetGrain<IAgentJobGrain>(control.JobId!).MarkUnknownAsync("stop-unconfirmed");
        }
        else if (string.Equals(reply.State, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            await session.MarkTurnTerminalAsync(control.TurnId, AgentTurnStatus.Unknown, null);
        }
        else if (string.Equals(reply.State, "stopped", StringComparison.OrdinalIgnoreCase))
        {
            if (!control.IsLaunchTurn)
                await session.MarkTurnTerminalAsync(control.TurnId, AgentTurnStatus.Completed, null);
            await session.CompleteTurnStopAsync(control.TurnId, claim.OperationId);
        }
        else if (string.Equals(reply.State, "not-cancellable", StringComparison.OrdinalIgnoreCase))
        {
            await session.CompleteTurnStopAsync(control.TurnId, claim.OperationId);
        }

        return reply.State?.ToLowerInvariant() switch
        {
            "stopped" => new(TurnControlResultKind.Stopped, control.Status, reply.InterruptUnconfirmed),
            "unknown" => new(TurnControlResultKind.Unknown, control.Status, reply.InterruptUnconfirmed),
            "not-cancellable" => new(TurnControlResultKind.NotCancellable, control.Status, reply.InterruptUnconfirmed),
            _ => new(TurnControlResultKind.StopRequested, control.Status, reply.InterruptUnconfirmed),
        };
    }
}
