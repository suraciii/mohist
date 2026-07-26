using Microsoft.AspNetCore.Http;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Workflow.Services.Artifacts;

namespace Mohist.Server.Api;

/// <summary>
/// Internal multipart upload endpoint used by the Mohist runner to
/// register pending artifact uploads before reporting the task
/// result. The endpoint is intentionally separate from
/// <see cref="WorkflowRoutes"/>: it accepts raw multipart bodies
/// rather than JSON, and the URL is internal (not part of the public
/// issue-scoped query surface).
/// </summary>
/// <remarks>
/// <para>
/// The endpoint derives the producing task run id from the active
/// workflow work context — the runner contract does not include an
/// attempt number, and we do not want to trust a runner-supplied id.
/// </para>
/// <para>
/// Pending uploads are <em>not</em> user-visible <c>WorkflowArtifact</c>
/// records: they remain hidden until
/// <c>WorkflowGrain.ReportResultAsync</c> binds them during task
/// result reporting.
/// </para>
/// </remarks>
public static class WorkflowArtifactUploadRoutes
{
    public const string MultipartFieldContent = "content";
    public const string MultipartFieldPath = "path";
    public const string MultipartFieldContentType = "contentType";
    public const string MultipartFieldContentHash = "contentHash";
    public const string MultipartFieldSize = "size";

    public static WebApplication MapWorkflowArtifactUploadRoutes(this WebApplication app)
    {
        app.MapPost(
            "/api/workflow-runs/{workflowRunId}/work/{workId}/artifact-uploads",
            async (
                HttpRequest request,
                string workflowRunId,
                string workId,
                WorkflowArtifactUploadService uploadService,
                CancellationToken cancellationToken) =>
            {
                var parsed = await ParseUploadRequestAsync(request, workflowRunId, workId, cancellationToken);
                if (parsed.Result is not null)
                    return parsed.Result;

                var result = await uploadService.UploadAsync(parsed.Request!, cancellationToken);
                return ToApiResult(result);
            });

        app.MapPost(
            "/api/agent-jobs/{agentJobId}/work/{workId}/artifact-uploads",
            async (
                HttpRequest request,
                string agentJobId,
                string workId,
                AgentJobArtifactUploadService uploadService,
                CancellationToken cancellationToken) =>
            {
                var parsed = await ParseUploadRequestAsync(request, agentJobId, workId, cancellationToken);
                if (parsed.Result is not null)
                    return parsed.Result;

                var result = await uploadService.UploadAsync(parsed.Request!, cancellationToken);
                return ToApiResult(result);
            });

        return app;
    }

    private static async Task<ParsedUploadRequest> ParseUploadRequestAsync(
        HttpRequest request,
        string ownerId,
        string workId,
        CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
            return new ParsedUploadRequest(null, ApiResults.BadRequest("multipart/form-data is required"));

        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            return new ParsedUploadRequest(null, ApiResults.BadRequest($"Invalid multipart body: {ex.Message}"));
        }

        var path = form[MultipartFieldPath].ToString();
        if (string.IsNullOrWhiteSpace(path))
            return new ParsedUploadRequest(null, ApiResults.BadRequest($"'{MultipartFieldPath}' field is required"));

        var contentType = form[MultipartFieldContentType].ToString();
        if (string.IsNullOrWhiteSpace(contentType)) contentType = null;

        var contentHash = form[MultipartFieldContentHash].ToString();
        if (string.IsNullOrWhiteSpace(contentHash)) contentHash = null;

        var sizeRaw = form[MultipartFieldSize].ToString();
        if (!TryParseSize(sizeRaw, out var size))
            return new ParsedUploadRequest(null, ApiResults.BadRequest(
                $"'{MultipartFieldSize}' must be a non-negative integer (got '{sizeRaw}')"));

        var file = form.Files.GetFile(MultipartFieldContent);
        if (file is null)
            return new ParsedUploadRequest(null, ApiResults.BadRequest($"'{MultipartFieldContent}' file part is required"));

        return new ParsedUploadRequest(new WorkflowArtifactUploadRequest
        {
            WorkflowRunId = ownerId,
            WorkId = workId,
            Path = path,
            ContentType = contentType ?? file.ContentType,
            ContentHash = contentHash,
            Size = size,
            OpenContent = () => file.OpenReadStream(),
        }, null);
    }

    private static IResult ToApiResult(WorkflowArtifactUploadResult result) => result.Kind switch
    {
        WorkflowArtifactUploadResultKind.Created => ApiResults.Ok(BuildResponse(result.Pending!, isIdempotent: false)),
        WorkflowArtifactUploadResultKind.Idempotent => ApiResults.Ok(BuildResponse(result.Pending!, isIdempotent: true)),
        WorkflowArtifactUploadResultKind.Conflict => ApiResults.Conflict(
            result.Error ?? "Conflicting upload for the same workflow run, work item, and path",
            code: "artifact_upload_conflict",
            details: new
            {
                existingUploadId = result.Conflict!.UploadId,
                existingContentHash = result.Conflict.ExistingContentHash,
                incomingContentHash = result.Conflict.IncomingContentHash,
            }),
        WorkflowArtifactUploadResultKind.Invalid => ApiResults.BadRequest(
            result.Error ?? "Invalid upload request"),
        WorkflowArtifactUploadResultKind.WorkItemNotFound => ApiResults.NotFound(
            result.Error ?? "Active workflow work item not found"),
        _ => ApiResults.Fail("Unsupported upload result", 500, "unsupported_upload_result"),
    };

    private static object BuildResponse(WorkflowArtifactPendingUploadInfo info, bool isIdempotent) => new
    {
        uploadId = info.UploadId,
        workflowRunId = info.WorkflowRunId,
        workId = info.WorkId,
        taskRunId = info.TaskRunId,
        path = info.Path,
        kind = info.Kind,
        contentType = info.ContentType,
        contentHash = info.ContentHash,
        size = info.Size,
        fileCount = info.FileCount,
        createdAt = info.CreatedAt,
        expiresAt = info.ExpiresAt,
        idempotent = isIdempotent,
    };

    private static bool TryParseSize(string? raw, out long size)
    {
        size = -1;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        return long.TryParse(raw, out size) && size >= 0;
    }

    private sealed record ParsedUploadRequest(WorkflowArtifactUploadRequest? Request, IResult? Result);
}
