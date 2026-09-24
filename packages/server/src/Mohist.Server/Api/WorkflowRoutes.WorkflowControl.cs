using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Mohist.Server.Api.DirectApi;
using Mohist.Server.Auth.Identity;
using Mohist.Server.Infrastructure.Idempotency;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Api;

public static partial class WorkflowRoutes
{
    public static WebApplication MapWorkflowRunControlRoutes(this WebApplication app)
    {
        app.MapPost("/api/workflow-runs/{workflowRunId}/resume", async (
            HttpContext ctx,
            string workflowRunId,
            IGrainFactory grains,
            WorkflowQuerier reader,
            ICurrentUser currentUser,
            IdempotencyFence fence,
            TimeProvider timeProvider) =>
        {
            var key = DirectApiWriteValidation.ReadIdempotencyKey(ctx.Request.Headers);
            return await KeyedControlWrites.ExecuteAsync(
                key, currentUser, fence, timeProvider,
                IdempotencyCommands.WorkflowControl,
                KeyedControlWrites.WorkflowControlScopeKey(workflowRunId, currentUser.Principal.Id, key.Value!),
                KeyedControlWrites.WorkflowControlFingerprint(workflowRunId, "resume"),
                $"mo run resume {workflowRunId} --idempotency-key <new-key>",
                $"mo run resume {workflowRunId} --idempotency-key {key.Value!}",
                async () =>
                {
                    if (await ResolveWorkflowRunControlAsync(workflowRunId, reader, WorkflowControlAction.ActiveOnly) is { } failure)
                        return failure;
                    return await ExecuteControlAsync(
                        () => grains.GetGrain<IWorkflowGrain>(workflowRunId).ResumeAsync());
                });
        });

        app.MapPost("/api/workflow-runs/{workflowRunId}/approve", async (
            HttpContext ctx,
            string workflowRunId,
            ApproveRequest? req,
            IGrainFactory grains,
            WorkflowQuerier reader,
            ICurrentUser currentUser,
            IdempotencyFence fence,
            TimeProvider timeProvider) =>
        {
            var key = DirectApiWriteValidation.ReadIdempotencyKey(ctx.Request.Headers);
            var displayName = NormalizeDisplayName(req?.DisplayName);
            if (displayName.Failure is { } displayFailure)
                return displayFailure;
            return await KeyedControlWrites.ExecuteAsync(
                key, currentUser, fence, timeProvider,
                IdempotencyCommands.WorkflowControl,
                KeyedControlWrites.WorkflowControlScopeKey(workflowRunId, currentUser.Principal.Id, key.Value!),
                KeyedControlWrites.WorkflowControlFingerprint(workflowRunId, "approve", new { displayName = displayName.Value }),
                $"mo run approve {workflowRunId} --idempotency-key <new-key>",
                $"mo run approve {workflowRunId} --idempotency-key {key.Value!}",
                async () =>
                {
                    if (await ResolveWorkflowRunControlAsync(workflowRunId, reader, WorkflowControlAction.ActiveOnly) is { } failure)
                        return failure;
                    return await ExecuteControlAsync(
                        () => grains.GetGrain<IWorkflowGrain>(workflowRunId).ApproveAsync(currentUser.Principal.Id, displayName.Value));
                });
        });

        app.MapPost("/api/workflow-runs/{workflowRunId}/request-changes", async (
            HttpContext ctx,
            string workflowRunId,
            RequestChangesRequest? req,
            IGrainFactory grains,
            WorkflowQuerier reader,
            ICurrentUser currentUser,
            IdempotencyFence fence,
            TimeProvider timeProvider) =>
        {
            var key = DirectApiWriteValidation.ReadIdempotencyKey(ctx.Request.Headers);
            var displayName = NormalizeDisplayName(req?.DisplayName);
            if (displayName.Failure is { } displayFailure)
                return displayFailure;
            if (string.IsNullOrWhiteSpace(req?.Message))
                return ApiResults.BadRequest(
                    "Request changes message is required",
                    effect: ApiEffect.None,
                    retrySafe: false);
            return await KeyedControlWrites.ExecuteAsync(
                key, currentUser, fence, timeProvider,
                IdempotencyCommands.WorkflowControl,
                KeyedControlWrites.WorkflowControlScopeKey(workflowRunId, currentUser.Principal.Id, key.Value!),
                KeyedControlWrites.WorkflowControlFingerprint(workflowRunId, "request-changes", new { message = req.Message, displayName = displayName.Value }),
                $"mo run request-changes {workflowRunId} --idempotency-key <new-key>",
                $"mo run request-changes {workflowRunId} --idempotency-key {key.Value!}",
                async () =>
                {
                    if (await ResolveWorkflowRunControlAsync(workflowRunId, reader, WorkflowControlAction.ActiveOnly) is { } failure)
                        return failure;
                    return await ExecuteControlAsync(
                        () => grains.GetGrain<IWorkflowGrain>(workflowRunId).RequestChangesAsync(req.Message, currentUser.Principal.Id, displayName.Value));
                });
        });

        app.MapPost("/api/workflow-runs/{workflowRunId}/retry", async (
            HttpContext ctx,
            string workflowRunId,
            IGrainFactory grains,
            WorkflowQuerier reader,
            ICurrentUser currentUser,
            IdempotencyFence fence,
            TimeProvider timeProvider) =>
        {
            var key = DirectApiWriteValidation.ReadIdempotencyKey(ctx.Request.Headers);
            return await KeyedControlWrites.ExecuteAsync(
                key, currentUser, fence, timeProvider,
                IdempotencyCommands.WorkflowControl,
                KeyedControlWrites.WorkflowControlScopeKey(workflowRunId, currentUser.Principal.Id, key.Value!),
                KeyedControlWrites.WorkflowControlFingerprint(workflowRunId, "retry"),
                $"mo run retry {workflowRunId} --idempotency-key <new-key>",
                $"mo run retry {workflowRunId} --idempotency-key {key.Value!}",
                async () =>
                {
                    if (await ResolveWorkflowRunControlAsync(workflowRunId, reader, WorkflowControlAction.RetryOrRerun) is { } failure)
                        return failure;
                    return await ExecuteControlAsync(
                        () => grains.GetGrain<IWorkflowGrain>(workflowRunId).RetryAsync());
                });
        });

        app.MapPost("/api/workflow-runs/{workflowRunId}/rerun", async (
            HttpContext ctx,
            string workflowRunId,
            IGrainFactory grains,
            WorkflowQuerier reader,
            ICurrentUser currentUser,
            IdempotencyFence fence,
            TimeProvider timeProvider,
            IssueQuerier issuesQuery) =>
        {
            var key = DirectApiWriteValidation.ReadIdempotencyKey(ctx.Request.Headers);
            return await KeyedControlWrites.ExecuteAsync(
                key, currentUser, fence, timeProvider,
                IdempotencyCommands.WorkflowControl,
                KeyedControlWrites.WorkflowControlScopeKey(workflowRunId, currentUser.Principal.Id, key.Value!),
                KeyedControlWrites.WorkflowControlFingerprint(workflowRunId, "rerun"),
                $"mo run rerun {workflowRunId} --idempotency-key <new-key>",
                $"mo run rerun {workflowRunId} --idempotency-key {key.Value!}",
                async () =>
                {
                    try
                    {
                        if (await ResolveWorkflowRunControlAsync(workflowRunId, reader, WorkflowControlAction.RetryOrRerun) is { } failure)
                            return failure;
                        return await ExecuteControlAsync(
                            () => grains.GetGrain<IWorkflowGrain>(workflowRunId).RerunAsync());
                    }
                    catch (Exception ex) when (WorkflowControlRecovery.IsWorkflowRunStateCorruption(ex))
                    {
                        return await WorkflowControlRecovery.RecoverWorkflowRunScopedRerunAsync(grains, issuesQuery, workflowRunId);
                    }
                });
        });

        app.MapPost("/api/workflow-runs/{workflowRunId}/rerun-from-stage", async (
            HttpContext ctx,
            string workflowRunId,
            RerunFromStageRequest? req,
            IGrainFactory grains,
            WorkflowQuerier reader,
            ICurrentUser currentUser,
            IdempotencyFence fence,
            TimeProvider timeProvider,
            IssueQuerier issuesQuery) =>
        {
            if (string.IsNullOrWhiteSpace(req?.Stage))
                return ApiResults.BadRequest(
                    "Stage is required for rerun-from-stage",
                    effect: ApiEffect.None,
                    retrySafe: false);
            var key = DirectApiWriteValidation.ReadIdempotencyKey(ctx.Request.Headers);
            return await KeyedControlWrites.ExecuteAsync(
                key, currentUser, fence, timeProvider,
                IdempotencyCommands.WorkflowControl,
                KeyedControlWrites.WorkflowControlScopeKey(workflowRunId, currentUser.Principal.Id, key.Value!),
                KeyedControlWrites.WorkflowControlFingerprint(workflowRunId, "rerun-from-stage", new { stage = req.Stage }),
                $"mo run rerun-from-stage {workflowRunId} --idempotency-key <new-key>",
                $"mo run rerun-from-stage {workflowRunId} --idempotency-key {key.Value!}",
                async () =>
                {
                    try
                    {
                        if (await ResolveWorkflowRunControlAsync(workflowRunId, reader, WorkflowControlAction.RetryOrRerun) is { } failure)
                            return failure;
                        var result = await grains.GetGrain<IWorkflowGrain>(workflowRunId).RerunFromStageAsync(req.Stage);
                        if (!result.Success)
                        {
                            return result.Code switch
                            {
                                "unknown_stage" or "stage_not_reached" => KeyedControlWrites.Outcome.Rejected(StatusCodes.Status400BadRequest,
                                    ApiResults.Failure(result.Error ?? "Workflow control rejected", StatusCodes.Status400BadRequest, result.Code, result.Details, ApiEffect.None, retrySafe: false)),
                                "active_work_in_range" => KeyedControlWrites.Outcome.Rejected(StatusCodes.Status409Conflict,
                                    ApiResults.Failure(result.Error ?? "Workflow control rejected", StatusCodes.Status409Conflict, result.Code, result.Details, ApiEffect.None, retrySafe: false)),
                                _ => KeyedControlWrites.Outcome.Rejected(StatusCodes.Status400BadRequest,
                                    ApiResults.Failure(result.Error ?? "Workflow control rejected", StatusCodes.Status400BadRequest, result.Code, result.Details, ApiEffect.None, retrySafe: false)),
                            };
                        }
                        return KeyedControlWrites.Outcome.Accepted(ApiResults.SuccessEnvelope());
                    }
                    catch (Exception ex) when (WorkflowControlRecovery.IsWorkflowRunStateCorruption(ex))
                    {
                        return await WorkflowControlRecovery.RecoverWorkflowRunScopedRerunAsync(grains, issuesQuery, workflowRunId);
                    }
                });
        });

        app.MapPost("/api/workflow-runs/{workflowRunId}/pause", async (
            HttpContext ctx,
            string workflowRunId,
            IGrainFactory grains,
            WorkflowQuerier reader,
            ICurrentUser currentUser,
            IdempotencyFence fence,
            TimeProvider timeProvider) =>
        {
            var key = DirectApiWriteValidation.ReadIdempotencyKey(ctx.Request.Headers);
            return await KeyedControlWrites.ExecuteAsync(
                key, currentUser, fence, timeProvider,
                IdempotencyCommands.WorkflowControl,
                KeyedControlWrites.WorkflowControlScopeKey(workflowRunId, currentUser.Principal.Id, key.Value!),
                KeyedControlWrites.WorkflowControlFingerprint(workflowRunId, "pause"),
                $"mo run pause {workflowRunId} --idempotency-key <new-key>",
                $"mo run pause {workflowRunId} --idempotency-key {key.Value!}",
                async () =>
                {
                    if (await ResolveWorkflowRunControlAsync(workflowRunId, reader, WorkflowControlAction.ActiveOnly) is { } failure)
                        return failure;
                    return await ExecuteControlAsync(
                        () => grains.GetGrain<IWorkflowGrain>(workflowRunId).PauseAsync("user-pause"));
                });
        });

        app.MapPost("/api/workflow-runs/{workflowRunId}/stop", async (
            HttpContext ctx,
            string workflowRunId,
            IGrainFactory grains,
            WorkflowQuerier reader,
            ICurrentUser currentUser,
            IdempotencyFence fence,
            TimeProvider timeProvider) =>
        {
            var key = DirectApiWriteValidation.ReadIdempotencyKey(ctx.Request.Headers);
            return await KeyedControlWrites.ExecuteAsync(
                key, currentUser, fence, timeProvider,
                IdempotencyCommands.WorkflowControl,
                KeyedControlWrites.WorkflowControlScopeKey(workflowRunId, currentUser.Principal.Id, key.Value!),
                KeyedControlWrites.WorkflowControlFingerprint(workflowRunId, "stop"),
                $"mo run stop {workflowRunId} --idempotency-key <new-key>",
                $"mo run stop {workflowRunId} --idempotency-key {key.Value!}",
                async () =>
                {
                    if (await ResolveWorkflowRunControlAsync(workflowRunId, reader, WorkflowControlAction.Stop) is { } failure)
                        return failure;
                    return await ExecuteControlAsync(
                        () => grains.GetGrain<IWorkflowGrain>(workflowRunId).StopAsync("user-stop"));
                });
        });

        return app;
    }

    internal sealed record RerunFromStageRequest(string? Stage);

    private static DisplayNameResult NormalizeDisplayName(string? raw)
    {
        try
        {
            return new DisplayNameResult(ApprovalOperatorValidation.Normalize(raw), null);
        }
        catch (ArgumentException ex)
        {
            return new DisplayNameResult(null, ApiResults.BadRequest(ex.Message, effect: ApiEffect.None, retrySafe: false));
        }
    }

    private sealed record DisplayNameResult(string? Value, IResult? Failure);

    /// <summary>
    /// Runs one domain control and classifies its outcome. A state guard in
    /// the aggregate refuses the control before it changes anything; that
    /// refusal is a classified conflict, so the fence records it and a replay
    /// returns the same decision instead of re-evaluating it.
    /// </summary>
    private static async Task<KeyedControlWrites.Outcome> ExecuteControlAsync(Func<Task> control)
    {
        try
        {
            await control();
            return KeyedControlWrites.Outcome.Accepted(ApiResults.SuccessEnvelope());
        }
        catch (InvalidOperationException ex) when (!WorkflowControlRecovery.IsWorkflowRunStateCorruption(ex))
        {
            return KeyedControlWrites.Outcome.Rejected(
                StatusCodes.Status409Conflict,
                ApiResults.Failure(
                    ex.Message,
                    StatusCodes.Status409Conflict,
                    "conflict",
                    effect: ApiEffect.None,
                    retrySafe: false));
        }
    }

    /// <summary>
    /// Referees the run state before a control operation executes. The
    /// returned failure is a classified rejection: it is recorded on the
    /// fence row and replayed for later retries of the same key.
    /// </summary>
    private static async Task<KeyedControlWrites.Outcome?> ResolveWorkflowRunControlAsync(
        string workflowRunId,
        WorkflowQuerier reader,
        WorkflowControlAction action)
    {
        var status = await reader.GetStatusAsync(workflowRunId);
        if (status is null)
        {
            return KeyedControlWrites.Outcome.Rejected(
                StatusCodes.Status404NotFound,
                ApiResults.Failure(
                    $"Workflow run '{workflowRunId}' not found",
                    StatusCodes.Status404NotFound,
                    "not_found",
                    effect: ApiEffect.None,
                    retrySafe: false));
        }
        if (!WorkflowControlGuard.IsWorkflowControllableForAction(status.Status, action))
        {
            return KeyedControlWrites.Outcome.Rejected(
                StatusCodes.Status409Conflict,
                ApiResults.Failure(
                    "Workflow is not active for this run",
                    StatusCodes.Status409Conflict,
                    "conflict",
                    effect: ApiEffect.None,
                    retrySafe: false));
        }
        return null;
    }
}
