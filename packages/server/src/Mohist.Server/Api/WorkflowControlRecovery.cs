using System.Text.Json;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;

namespace Mohist.Server.Api;

internal static class WorkflowControlRecovery
{
    internal static bool IsWorkflowRunStateCorruption(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is InvalidOperationException
                && current.Message.Contains("Failed to deserialize workflow run state", StringComparison.Ordinal))
                return true;

            if (current is JsonException)
                return true;
        }

        return false;
    }

    internal static async Task<IResult> RecoverIssueScopedRerunAsync(
        IGrainFactory grains,
        IssueQuerier issuesQuery,
        string projectId,
        int number,
        IEventStore events,
        WorkflowProvenanceActor? actor = null)
    {
        var issue = await issuesQuery.GetInfoAsync(projectId, number);
        var issueGrain = await IssueRoutes.GetIssueGrainAsync(grains, issuesQuery, projectId, number);
        if (issueGrain is null) return ApiResults.NotFound($"Issue #{number} not found");

        if (issue?.WorkflowRunId is { } workflowRunId)
        {
            string replacementRunId;
            try
            {
                replacementRunId = await issueGrain.StartWorkAsync();
            }
            catch (Exception ex)
            {
                await TryAppendRecoveryFactAsync(
                    events,
                    workflowRunId,
                    projectId,
                    number,
                    actor,
                    outcome: IsOutcomeKnownFailure(ex) ? "failed" : "unknown",
                    reason: ex.Message);
                return RecoveryFailureResult(ex);
            }

            try
            {
                await AppendRecoveryFactAsync(
                    events,
                    workflowRunId,
                    projectId,
                    number,
                    actor,
                    outcome: "succeeded",
                    result: replacementRunId);
            }
            catch (Exception ex)
            {
                return Results.Json(ApiResults.Failure(
                    "Replacement workflow started, but recovery provenance could not be recorded",
                    StatusCodes.Status503ServiceUnavailable,
                    "recovery_provenance_unknown",
                    new { replacementRunId, reason = ex.Message },
                    effect: ApiEffect.Unknown,
                    retrySafe: false,
                    nextAction: "Inspect the issue and workflow history before retrying recovery"),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }
        else
        {
            await issueGrain.StartWorkAsync();
        }
        return ApiResults.Ok();
    }

    private static bool IsOutcomeKnownFailure(Exception ex) =>
        ex is ArgumentException or InvalidOperationException;

    private static IResult RecoveryFailureResult(Exception ex)
    {
        var response = RecoveryFailureResponse(ex);
        return Results.Json(response.Body, statusCode: response.StatusCode);
    }

    private static (int StatusCode, ApiResponse<object> Body) RecoveryFailureResponse(Exception ex)
    {
        var known = IsOutcomeKnownFailure(ex);
        var statusCode = known
            ? StatusCodes.Status409Conflict
            : StatusCodes.Status503ServiceUnavailable;
        var code = known ? "replacement_rejected" : "replacement_unknown";
        var error = known
            ? "Replacement workflow could not be started"
            : "Replacement workflow outcome is unknown";
        var nextAction = known
            ? "Resolve the issue start error, then retry recovery"
            : "Inspect the issue and workflow history before retrying recovery";
        return (
            statusCode,
            ApiResults.Failure(
                error,
                statusCode,
                code,
                new { reason = ex.Message },
                known ? ApiEffect.None : ApiEffect.Unknown,
                retrySafe: false,
                nextAction: nextAction));
    }

    private static async Task TryAppendRecoveryFactAsync(
        IEventStore events,
        string workflowRunId,
        string projectId,
        int issueNumber,
        WorkflowProvenanceActor? actor,
        string outcome,
        string? result = null,
        string? reason = null)
    {
        try
        {
            await AppendRecoveryFactAsync(
                events, workflowRunId, projectId, issueNumber, actor, outcome, result, reason);
        }
        catch
        {
            // Preserve the original StartWork exception; the recovery attempt
            // remains observable through the failed control request.
        }
    }

    internal static async Task<KeyedControlWrites.Outcome> RecoverWorkflowRunScopedRerunAsync(
        IGrainFactory grains,
        IssueQuerier issuesQuery,
        string workflowRunId,
        IEventStore events,
        WorkflowProvenanceActor? actor = null)
    {
        var issue = await issuesQuery.GetIssueForWorkflowRunAsync(workflowRunId);
        if (issue is null)
        {
            return KeyedControlWrites.Outcome.Rejected(
                StatusCodes.Status404NotFound,
                ApiResults.Failure(
                    $"Issue for workflow run '{workflowRunId}' not found",
                    StatusCodes.Status404NotFound,
                    "not_found",
                    effect: ApiEffect.None,
                    retrySafe: false));
        }

        string replacementRunId;
        try
        {
            replacementRunId = await grains.GetGrain<IIssueGrain>(
                GrainKey.Issue(new IssueKey(issue.ProjectId, issue.Number))).StartWorkAsync();
        }
        catch (Exception ex)
        {
            await TryAppendRecoveryFactAsync(
                events, workflowRunId, issue.ProjectId, issue.Number, actor,
                IsOutcomeKnownFailure(ex) ? "failed" : "unknown", reason: ex.Message);
            var response = RecoveryFailureResponse(ex);
            return KeyedControlWrites.Outcome.Rejected(response.StatusCode, response.Body);
        }

        try
        {
            await AppendRecoveryFactAsync(
                events, workflowRunId, issue.ProjectId, issue.Number, actor,
                outcome: "succeeded", result: replacementRunId);
        }
        catch (Exception ex)
        {
            return KeyedControlWrites.Outcome.Rejected(
                StatusCodes.Status503ServiceUnavailable,
                ApiResults.Failure(
                    "Replacement workflow started, but recovery provenance could not be recorded",
                    StatusCodes.Status503ServiceUnavailable,
                    "recovery_provenance_unknown",
                    new { replacementRunId, reason = ex.Message },
                    effect: ApiEffect.Unknown,
                    retrySafe: false,
                    nextAction: "Inspect the issue and workflow history before retrying recovery"));
        }
        return KeyedControlWrites.Outcome.Accepted(ApiResults.SuccessEnvelope());
    }

    private static Task AppendRecoveryFactAsync(
        IEventStore events,
        string workflowRunId,
        string projectId,
        int issueNumber,
        WorkflowProvenanceActor? actor,
        string outcome,
        string? result = null,
        string? reason = null)
    {
        var resolved = actor ?? new WorkflowProvenanceActor(
            WorkflowProvenanceActorKinds.System,
            "workflow-control");
        var fact = new WorkflowProvenanceRecorded(
            WorkflowProvenanceActions.Recovery,
            resolved.Kind,
            resolved.Id,
            "workflow-control-recovery",
            outcome,
            Result: result,
            Reason: reason ?? "run-state-corruption",
            Target: workflowRunId,
            ActorDisplayName: resolved.DisplayName);
        var extensions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EventCatalog.Lineage.WorkflowRunId] = workflowRunId,
            [EventCatalog.Lineage.ProjectId] = projectId,
            [EventCatalog.Lineage.Issue] = issueNumber.ToString(),
        };
        var envelope = new CloudEvent(
            Guid.NewGuid().ToString(),
            new Uri($"/mohist/workflow-runs/{workflowRunId}", UriKind.Relative),
            EventCatalog.ReverseDns.WorkflowProvenanceRecorded,
            DateTimeOffset.UtcNow,
            WorkflowEventSerializer.ToData(fact),
            subject: null,
            specVersion: "1.0",
            extensions: extensions);
        return events.AppendAsync(envelope);
    }
}
