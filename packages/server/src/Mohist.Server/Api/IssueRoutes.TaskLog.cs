using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Routing;
using Mohist.Server.Infrastructure;
using Mohist.Server.Runner.Services;

namespace Mohist.Server.Api;

public static partial class IssueRoutes
{
    /// <summary>
    /// Cursor-paginated query for one Action attempt's execution logs,
    /// addressed by the timeline task id (<c>WorkflowActionAttempt.Id</c>) of
    /// the originating <c>workflowRunId</c>. The run is required: the issue's
    /// current run must never stand in for the run that produced the
    /// evidence, so a retry or a later run cannot redirect the read to other
    /// execution. <see cref="TaskLogService"/> validates the run's
    /// project/issue scope and resolves the attempt to its work id; no grain
    /// call is involved.
    /// </summary>
    internal static void MapIssueWorkflowTaskLogs(this RouteGroupBuilder group)
    {
        group.MapGet("/{number:int}/workflow/tasks/{taskId}/logs", async (
            HttpContext ctx,
            int number,
            string taskId,
            string? workflowRunId,
            long? cursor,
            int? limit,
            TaskLogService logService) =>
        {
            var project = GetRequiredProject(ctx);
            if (string.IsNullOrWhiteSpace(workflowRunId))
                return ApiResults.BadRequest("workflowRunId is required");

            var ct = ctx.RequestAborted;
            var page = await logService.QueryByTaskIdAsync(project.Id, number, workflowRunId, taskId, cursor, limit, ct);
            if (page is null)
                return ApiResults.NotFound(
                    $"No task-log evidence for attempt '{taskId}' in workflow run '{workflowRunId}' of issue #{number}");

            return ApiResults.Ok(new TaskLogQueryPage(
                page.Lines.Select(line => new TaskLogQueryLine(
                    line.Seq,
                    line.Timestamp.ToString("o"),
                    line.Source,
                    line.Text)).ToList(),
                page.NextCursor,
                page.Truncated));
        });
    }
}

public sealed record TaskLogQueryPage(
    IReadOnlyList<TaskLogQueryLine> Lines,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? NextCursor,
    bool Truncated);

public sealed record TaskLogQueryLine(
    long Seq,
    string Timestamp,
    string Source,
    string Text);
