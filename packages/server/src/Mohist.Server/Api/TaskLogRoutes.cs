using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Mohist.Server.Infrastructure;
using Mohist.Server.Runner.Services;

namespace Mohist.Server.Api;

/// <summary>
/// Internal multipart upload endpoints used by the Mohist runner to
/// post captured task execution logs to a dedicated store. Mirrors
/// <see cref="WorkflowArtifactUploadRoutes"/> in routing shape: a
/// dual owner-kind pair of <c>POST</c> endpoints that write directly
/// to <see cref="TaskLogStore"/> (via <see cref="TaskLogService"/>)
/// with no grain involvement (design D1).
/// </summary>
/// <remarks>
/// TaskLog is review evidence associated with a work item only — it
/// does not flow through <c>WorkflowGrain</c>,
/// <c>RunnerGrain.ReportWorkflowResultAsync</c>, or the
/// <c>WorkResult</c> / report contract. Uploads are independent of
/// task status adjudication; an upload failure or absence cannot
/// change a task's verdict.
/// </remarks>
public static class TaskLogRoutes
{
    public const string RouteWorkflow = "/api/workflow-runs/{workflowRunId}/work/{workId}/task-log";
    public const string RouteAgentJob = "/api/agent-jobs/{agentJobId}/work/{workId}/task-log";

    public const string OwnerKindWorkflow = TaskLogOwnershipKinds.Workflow;
    public const string OwnerKindAgentJob = TaskLogOwnershipKinds.AgentJob;

    public static WebApplication MapTaskLogRoutes(this WebApplication app)
    {
        app.MapPost(RouteWorkflow, async (
            HttpRequest request,
            string workflowRunId,
            string workId,
            TaskLogService service,
            CancellationToken cancellationToken) =>
        {
            return await HandleUploadAsync(request, OwnerKindWorkflow, workflowRunId, workId, service, cancellationToken);
        });

        app.MapPost(RouteAgentJob, async (
            HttpRequest request,
            string agentJobId,
            string workId,
            TaskLogService service,
            CancellationToken cancellationToken) =>
        {
            return await HandleUploadAsync(request, OwnerKindAgentJob, agentJobId, workId, service, cancellationToken);
        });

        return app;
    }

    private static async Task<IResult> HandleUploadAsync(
        HttpRequest request,
        string ownerKind,
        string ownerId,
        string workId,
        TaskLogService service,
        CancellationToken cancellationToken)
    {
        TaskLogUploadRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<TaskLogUploadRequest>(
                request.Body,
                JSON.Options,
                cancellationToken);
        }
        catch (JsonException ex)
        {
            return ApiResults.BadRequest($"Invalid task-log body: {ex.Message}");
        }

        if (body is null)
            return ApiResults.BadRequest("Empty task-log body");

        var entries = body.Entries ?? new List<TaskLogUploadEntry>();
        if (entries.Count > TaskLogUploadRequest.MaxEntries)
            return ApiResults.BadRequest(
                $"Too many entries ({entries.Count}); max {TaskLogUploadRequest.MaxEntries}");

        var lines = entries
            .Select(e => new TaskLogLine(
                e.Seq,
                e.Timestamp,
                e.Source ?? string.Empty,
                e.Text ?? string.Empty))
            .ToList();

        await service.AppendAsync(ownerKind, ownerId, workId, lines, body.Truncated, cancellationToken);

        return ApiResults.Ok(new
        {
            ownerKind,
            ownerId,
            workId,
            accepted = entries.Count,
            truncated = body.Truncated,
        });
    }
}

/// <summary>
/// Wire-shape for a runner-side task-log upload. The runner emits
/// one batch per work item at task completion (design D6).
/// </summary>
public sealed class TaskLogUploadRequest
{
    /// <summary>
    /// Server-side safety cap on a single upload. Larger uploads are
    /// rejected with <c>400</c> so a runaway sink cannot blow up the
    /// store in one request. The runner-side cap is
    /// <c>TaskLogCollector</c>'s <c>MAX_TASK_LOG_LINES</c>; this
    /// ceiling is intentionally a little higher to absorb any
    /// version skew between the two.
    /// </summary>
    public const int MaxEntries = 20_000;

    public List<TaskLogUploadEntry>? Entries { get; set; }

    /// <summary>
    /// True when the runner dropped head lines at capture time
    /// because the captured output exceeded the per-task capacity
    /// limit. The web client surfaces this so the user knows the
    /// earliest lines are not available.
    /// </summary>
    public bool Truncated { get; set; }
}

public sealed class TaskLogUploadEntry
{
    public long Seq { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string? Source { get; set; }
    public string? Text { get; set; }
}