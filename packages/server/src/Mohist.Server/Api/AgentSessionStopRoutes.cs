using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Primitives;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Project.Services;
using Mohist.Server.Runner.Services;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Api;

public static class AgentSessionStopRoutes
{
    public static WebApplication MapAgentSessionStopRoutes(this WebApplication app)
    {
        var group = app.MapGroup(AgentSessionCancelRoutes.CancelPathPrefix)
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        group.MapPost("/{sessionId}/stop", async (
            HttpContext context,
            string projectRef,
            string sessionId,
            AgentSessionQuerier sessions,
            IGrainFactory grains,
            IHubContext<RunnerHub> runnerHub,
            RunnerConnectionTracker connections,
            SessionTreeStopOrchestrator cascadeStop,
            WorkflowSessionWorkReconciler workReconciler,
            CancellationToken ct) =>
        {
            var project = context.GetResolvedProject();
            AgentSessionCancelRequest? request = null;
            var hasBody = context.Request.ContentLength is > 0;
            if (hasBody)
            {
                try
                {
                    using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: ct);
                    if (document.RootElement.ValueKind != JsonValueKind.Object
                        || !document.RootElement.TryGetProperty("turnId", out _))
                    {
                        return ApiResults.BadRequest(
                            "Cascade stop accepts no revision, membership, targets, or other request body.",
                            "stop_body_not_allowed");
                    }
                    request = document.RootElement.Deserialize<AgentSessionCancelRequest>(JSON.Options);
                }
                catch (JsonException)
                {
                    return ApiResults.BadRequest("Stop request body is invalid JSON.", "invalid_request_body");
                }
            }

            if (string.IsNullOrWhiteSpace(request?.TurnId))
            {
                if (hasBody)
                    return ApiResults.BadRequest(
                        "Cascade stop does not accept a request body.",
                        "stop_body_not_allowed");

                if (!context.Request.Headers.TryGetValue("Idempotency-Key", out StringValues values)
                    || StringValues.IsNullOrEmpty(values)
                    || string.IsNullOrWhiteSpace(values.ToString()))
                    return ApiResults.BadRequest("Idempotency-Key is required", "idempotency_key_missing");

                try
                {
                    var operation = await cascadeStop.StartAsync(project.Id, sessionId, values.ToString());
                    return ApiResults.Ok(SessionTreeStopResponse.From(operation));
                }
                catch (SessionTreeStopOperationConflictException ex)
                {
                    return ApiResults.Conflict(ex.Message, "idempotency_conflict");
                }
            }

            return await ExecuteStopAsync(
                project.Id,
                sessionId,
                request,
                sessions,
                grains,
                runnerHub,
                connections,
                workReconciler,
                ct);
        });

        return app;
    }

    public static void MapAgentSessionStopOperationReadRoute(this WebApplication app)
    {
        var group = app.MapGroup(AgentSessionCancelRoutes.CancelPathPrefix)
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        group.MapGet("/{sessionId}/stop/{operationId}", async (
            HttpContext context,
            string sessionId,
            string operationId,
            SessionTreeStopOrchestrator cascadeStop) =>
        {
            var project = context.GetResolvedProject();
            var operation = await cascadeStop.GetAsync(project.Id, sessionId, operationId);
            return operation is null
                ? ApiResults.NotFound($"Stop operation {operationId} not found")
                : ApiResults.Ok(SessionTreeStopResponse.From(operation));
        });
    }

    internal static async Task<IResult> ExecuteStopAsync(
        string projectId,
        string sessionId,
        AgentSessionCancelRequest? request,
        AgentSessionQuerier sessions,
        IGrainFactory grains,
        IHubContext<RunnerHub> runnerHub,
        RunnerConnectionTracker connections,
        WorkflowSessionWorkReconciler workReconciler,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.TurnId))
            return ApiResults.BadRequest("turnId is required", "turn_id_missing");

        var target = await sessions.ResolveCancelTargetAsync(projectId, sessionId, ct);
        if (target is null)
            return ApiResults.NotFound($"Agent session {sessionId} not found");

        var result = await AgentSessionTurnControlOperations.StopAsync(
            projectId, grains, runnerHub, connections, target, request.TurnId, ct);
        if (result.Kind is TurnControlResultKind.Stopped or TurnControlResultKind.AlreadyEnded)
            await workReconciler.ReconcileAsync(projectId, target.SessionId, target.RunnerId, "session-stop", ct);
        return result.Kind switch
        {
            TurnControlResultKind.NotFound => ApiResults.NotFound($"Turn {request.TurnId} not found"),
            TurnControlResultKind.AlreadyEnded => ApiResults.Ok(new
            {
                state = "turn-already-ended",
                turnStatus = result.Status!.Value.ToString().ToLowerInvariant(),
            }),
            TurnControlResultKind.Queued => ApiResults.Ok(new { state = "queued", action = "cancel" }),
            TurnControlResultKind.StopRequested => ApiResults.Ok(new { state = "stop-requested" }),
            TurnControlResultKind.RunnerUnavailable => ApiResults.Fail(
                "Runner is unavailable", 503, "runner_unavailable", new { runnerId = target.RunnerId }),
            _ => ApiResults.Ok(new
            {
                state = result.Kind switch
                {
                    TurnControlResultKind.Stopped => "stopped",
                    TurnControlResultKind.Unknown => "unknown",
                    TurnControlResultKind.NotCancellable => "not-cancellable",
                    _ => throw new InvalidOperationException($"Unexpected stop result {result.Kind}"),
                },
                interruptUnconfirmed = result.InterruptUnconfirmed,
            }),
        };
    }
}

public sealed record RunnerStopReply(string? State, bool? InterruptUnconfirmed = null);

public sealed record SessionTreeStopResponse(
    string OperationId,
    string RootSessionId,
    string Status,
    bool AdmissionFenceActive,
    long? GraphRevision,
    IReadOnlyList<SessionTreeStopMembership> Membership,
    IReadOnlyList<SessionTreeStopTargetResponse> Targets)
{
    public static SessionTreeStopResponse From(SessionTreeStopOperation operation)
    {
        var snapshot = operation.Snapshot;
        var results = operation.TargetResults ?? [];
        var targets = snapshot?.Targets.Select(target =>
        {
            var result = results.FirstOrDefault(item => item.SessionId == target.SessionId);
            return new SessionTreeStopTargetResponse(
                target.SessionId,
                target.StopOperationId,
                target.TurnId,
                target.JobId,
                target.TurnStatus?.ToString().ToLowerInvariant(),
                target.RunnerId,
                target.Runtime,
                target.RuntimeSessionId,
                target.WorkDir,
                target.BindingEpoch,
                result?.Outcome.ToString().ToLowerInvariant(),
                result?.Detail);
        }).ToArray() ?? [];
        return new(
            operation.OperationId,
            operation.RootSessionId,
            operation.Status.ToString().ToLowerInvariant(),
            operation.AdmissionFenceActive,
            snapshot?.GraphRevision,
            snapshot?.Membership ?? [],
            targets);
    }
}

public sealed record SessionTreeStopTargetResponse(
    string SessionId,
    string StopOperationId,
    string? TurnId,
    string? JobId,
    string? TurnStatus,
    string? RunnerId,
    string? Runtime,
    string? RuntimeSessionId,
    string? WorkDir,
    long BindingEpoch,
    string? Outcome,
    string? Detail);
