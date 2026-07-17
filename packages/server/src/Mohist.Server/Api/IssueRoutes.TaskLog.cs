using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Routing;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Runner.Services;

namespace Mohist.Server.Api;

public static partial class IssueRoutes
{
    /// <summary>
    /// Cursor-paginated query for an issue's task execution logs.
    /// The endpoint accepts the timeline task id (<c>TaskRun.Id</c>)
    /// for consistency with how the web addresses tasks everywhere
    /// else (retry, artifacts-by-task); <see cref="TaskLogService"/>
    /// resolves it to a work id and queries the store. No grain
    /// call is involved.
    /// </summary>
    internal static void MapIssueWorkflowTaskLogs(this RouteGroupBuilder group)
    {
        group.MapGet("/{number:int}/workflow/tasks/{taskId}/logs", async (
            HttpContext ctx,
            int number,
            string taskId,
            long? cursor,
            int? limit,
            IGrainFactory grains,
            IssueQuerier issuesQuery,
            TaskLogService logService) =>
        {
            var project = GetRequiredProject(ctx);

            var wrId = await ResolveWorkflowRunIdAsync(grains, issuesQuery, project.Id, number);
            if (wrId is null)
                return ApiResults.Ok(EmptyPage());

            var ct = ctx.RequestAborted;
            var page = await logService.QueryByTaskIdAsync(wrId, taskId, cursor, limit, ct);
            if (page is null)
                return ApiResults.Ok(EmptyPage());

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

    private static TaskLogQueryPage EmptyPage() => new(
        Array.Empty<TaskLogQueryLine>(),
        NextCursor: null,
        Truncated: false);
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
