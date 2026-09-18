using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Auth.Identity;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Workflow.Services.Artifacts;
using Mohist.Server.Workflow.Storage;

namespace Mohist.Server.Api;

/// <summary>
/// Internal multipart upload endpoints used by the Mohist runner to
/// register pending artifact uploads before reporting the task
/// result. The endpoints are intentionally separate from
/// <see cref="WorkflowRoutes"/>: they accept raw multipart bodies
/// rather than JSON, and the URLs are internal (not part of the public
/// issue-scoped query surface).
/// </summary>
/// <remarks>
/// <para>
/// Single-file and directory artifacts use separate routes so their
/// transport limits stay separate. A directory envelope can be up to
/// <c>MaxEnvelopeBytes</c> (hundreds of MiB after base64 inflation), so only
/// the directory routes raise the request body and form read limits. The
/// single-file routes keep the default Kestrel/form boundary, preserving the
/// previous anti-flood ceiling for regular file uploads.
/// </para>
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

    /// <summary>Relative path segment that identifies a directory upload route.</summary>
    public const string DirectoryUploadRouteSuffix = "artifact-directory-uploads";

    public static WebApplication MapWorkflowArtifactUploadRoutes(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // The envelope limit is the effective directory-upload gate. Only the
        // directory routes derive their request-size metadata from it (plus
        // multipart framing); the file routes keep the default transport
        // boundary.
        var limits = app.Services
            .GetRequiredService<IOptions<WorkflowArtifactStorageOptions>>()
            .Value.DirectoryLimits ?? WorkflowArtifactDirectoryLimits.Default;
        var directoryBodyLimit = limits.MaxMultipartBodyBytes;
        var directoryRequestSizeLimit = new RequestSizeLimitAttribute(directoryBodyLimit);

        app.MapPost(
            "/api/workflow-runs/{workflowRunId}/work/{workId}/artifact-uploads",
            (HttpRequest request, string workflowRunId, string workId,
                WorkflowArtifactUploadService uploadService, CancellationToken cancellationToken) =>
                HandleUploadAsync(
                    request, workflowRunId, workId, ArtifactUploadKind.File, maxMultipartBodyBytes: null,
                    (parsed, token) => uploadService.UploadAsync(parsed, token),
                    cancellationToken))
            .RequireScopes(Scope.Runner);

        app.MapPost(
            "/api/workflow-runs/{workflowRunId}/work/{workId}/artifact-directory-uploads",
            (HttpRequest request, string workflowRunId, string workId,
                WorkflowArtifactUploadService uploadService, CancellationToken cancellationToken) =>
                HandleUploadAsync(
                    request, workflowRunId, workId, ArtifactUploadKind.Directory, directoryBodyLimit,
                    (parsed, token) => uploadService.UploadAsync(parsed, token),
                    cancellationToken))
            .RequireScopes(Scope.Runner)
            .WithMetadata(directoryRequestSizeLimit);

        app.MapPost(
            "/api/agent-jobs/{agentJobId}/work/{workId}/artifact-uploads",
            (HttpRequest request, string agentJobId, string workId,
                AgentJobArtifactUploadService uploadService, CancellationToken cancellationToken) =>
                HandleUploadAsync(
                    request, agentJobId, workId, ArtifactUploadKind.File, maxMultipartBodyBytes: null,
                    (parsed, token) => uploadService.UploadAsync(parsed, token),
                    cancellationToken))
            .RequireScopes(Scope.Runner);

        app.MapPost(
            "/api/agent-jobs/{agentJobId}/work/{workId}/artifact-directory-uploads",
            (HttpRequest request, string agentJobId, string workId,
                AgentJobArtifactUploadService uploadService, CancellationToken cancellationToken) =>
                HandleUploadAsync(
                    request, agentJobId, workId, ArtifactUploadKind.Directory, directoryBodyLimit,
                    (parsed, token) => uploadService.UploadAsync(parsed, token),
                    cancellationToken))
            .RequireScopes(Scope.Runner)
            .WithMetadata(directoryRequestSizeLimit);

        return app;
    }

    /// <summary>
    /// Replaces the request's form feature with one whose
    /// <see cref="FormOptions.MultipartBodyLengthLimit"/> admits a directory
    /// envelope. <see cref="FormOptions"/> is process-global, so this is applied
    /// only to the directory route, after which the file and attachment routes
    /// keep the framework default.
    /// </summary>
    internal static void ApplyDirectoryFormLimit(HttpRequest request, long maxMultipartBodyBytes)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.HttpContext.Features.Set<IFormFeature>(
            new FormFeature(request, new FormOptions
            {
                MultipartBodyLengthLimit = maxMultipartBodyBytes,
            }));
    }

    private static async Task<IResult> HandleUploadAsync(
        HttpRequest request,
        string ownerId,
        string workId,
        ArtifactUploadKind expectedKind,
        long? maxMultipartBodyBytes,
        Func<WorkflowArtifactUploadRequest, CancellationToken, Task<WorkflowArtifactUploadResult>> upload,
        CancellationToken cancellationToken)
    {
        var parsed = await ParseUploadRequestAsync(
            request, ownerId, workId, expectedKind, maxMultipartBodyBytes, cancellationToken);
        if (parsed.Result is not null)
            return parsed.Result;

        return ToApiResult(await upload(parsed.Request!, cancellationToken));
    }

    private static async Task<ParsedUploadRequest> ParseUploadRequestAsync(
        HttpRequest request,
        string ownerId,
        string workId,
        ArtifactUploadKind expectedKind,
        long? maxMultipartBodyBytes,
        CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
            return new ParsedUploadRequest(null, ApiResults.BadRequest("multipart/form-data is required"));

        if (expectedKind == ArtifactUploadKind.Directory)
        {
            ArgumentNullException.ThrowIfNull(maxMultipartBodyBytes);
            ApplyDirectoryFormLimit(request, maxMultipartBodyBytes.Value);
        }

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

        var declaredDirectory = WorkflowArtifactDirectoryEnvelopeReader.IsDirectoryContentType(contentType);
        if (expectedKind == ArtifactUploadKind.Directory && !declaredDirectory)
        {
            return new ParsedUploadRequest(null, ApiResults.BadRequest(
                $"'{MultipartFieldContentType}' must be '{WorkflowArtifactDirectoryEnvelopeReader.ContentType}'"
                + " for a directory upload"));
        }

        if (expectedKind == ArtifactUploadKind.File && declaredDirectory)
        {
            return new ParsedUploadRequest(null, ApiResults.BadRequest(
                $"Directory uploads must use the dedicated directory upload endpoint"
                + $" ('{DirectoryUploadRouteSuffix}')"));
        }

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
        actionAttemptId = info.ActionAttemptId,
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

    private enum ArtifactUploadKind
    {
        File,
        Directory,
    }

    private sealed record ParsedUploadRequest(WorkflowArtifactUploadRequest? Request, IResult? Result);
}
