using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Workflow.Domain.Artifacts;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Services.Prompts;

namespace Mohist.Server.Workflow.Services.Artifacts;

public interface IWorkflowArtifactBindService
{
    Task<WorkflowArtifactBindResult> BindAsync(
        string workflowRunId,
        string workId,
        string taskRunId,
        string[] artifactUploadIds,
        TaskArtifactCapture? declaredArtifacts,
        JsonElement? variables = null,
        string? projectId = null,
        string? issueId = null,
        CancellationToken cancellationToken = default);
}

public sealed record WorkflowArtifactBindResult(
    IReadOnlyList<WorkflowArtifactRecorded> ArtifactRecordedEvents,
    string? Error = null)
{
    public bool IsSuccess => Error is null;
}

public sealed class WorkflowArtifactBindService : IWorkflowArtifactBindService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly ILogger<WorkflowArtifactBindService> _log;
    private readonly TimeProvider _time;
    private readonly PromptTemplateEngine _templateEngine;

    public WorkflowArtifactBindService(
        IDbContextFactory<MohistDbContext> dbFactory,
        ILogger<WorkflowArtifactBindService> log,
        TimeProvider time,
        PromptTemplateEngine templateEngine)
    {
        _dbFactory = dbFactory;
        _log = log;
        _time = time;
        _templateEngine = templateEngine;
    }

    public async Task<WorkflowArtifactBindResult> BindAsync(
        string workflowRunId,
        string workId,
        string taskRunId,
        string[] artifactUploadIds,
        TaskArtifactCapture? declaredArtifacts,
        JsonElement? variables = null,
        string? projectId = null,
        string? issueId = null,
        CancellationToken cancellationToken = default)
    {
        if (artifactUploadIds is null || artifactUploadIds.Length == 0)
        {
            if (declaredArtifacts is not null && !declaredArtifacts.IsEmpty)
            {
                return new WorkflowArtifactBindResult(
                    [],
                    "Task has declared artifacts but no upload ids were provided");
            }
            return new WorkflowArtifactBindResult([]);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var pendingUploads = await db.WorkflowArtifactPendingUploads
            .Where(p =>
                p.WorkflowRunId == workflowRunId
                && p.WorkId == workId
                && artifactUploadIds.Contains(p.UploadId))
            .ToListAsync(cancellationToken);

        if (pendingUploads.Count == 0)
        {
            return new WorkflowArtifactBindResult(
                [],
                "No valid pending uploads found for the given upload ids and workflow work item");
        }

        var matchedIds = pendingUploads.Select(p => p.UploadId).ToHashSet();
        var foreignIds = artifactUploadIds.Where(id => !matchedIds.Contains(id)).ToList();
        if (foreignIds.Count > 0)
        {
            return new WorkflowArtifactBindResult(
                [],
                $"Upload ids {string.Join(", ", foreignIds)} do not belong to workflow run {workflowRunId} and work item {workId}");
        }

        if (declaredArtifacts is not null && !declaredArtifacts.IsEmpty)
        {
            // The runner renders declared artifact `path` strings against
            // the workflow variables before upload, so the upload
            // records the resolved workspace path. The declared
            // declaration still carries the unrendered template form
            // (e.g. `${{ openspecChangeDir }}/review.md`). Render the
            // declared paths with the same variables so both sides
            // agree on the comparison key.
            var uploadedPaths = pendingUploads.Select(p => p.Path).ToHashSet(StringComparer.Ordinal);
            var missingPaths = new List<string>();
            foreach (var declaredFile in declaredArtifacts.Files)
            {
                var declaredPath = declaredFile.Path ?? string.Empty;
                var comparisonPath = declaredPath;
                if (variables.HasValue
                    && !string.IsNullOrEmpty(declaredPath)
                    && declaredPath.Contains('$'))
                {
                    var (rendered, missing, _) = _templateEngine.Render(declaredPath, variables.Value);
                    if (missing.Count > 0)
                    {
                        return new WorkflowArtifactBindResult(
                            [],
                            $"Declared artifact path '{declaredPath}' references undefined variable(s): {string.Join(", ", missing.Select(m => "'${{ " + m + " }}'"))}");
                    }
                    comparisonPath = rendered;
                }

                if (!uploadedPaths.Contains(comparisonPath))
                {
                    missingPaths.Add(declaredPath);
                }
            }

            if (missingPaths.Count > 0)
            {
                return new WorkflowArtifactBindResult(
                    [],
                    $"Required declared artifacts missing: {string.Join(", ", missingPaths)}");
            }
        }

        var now = _time.GetUtcNow();
        var recordedEvents = new List<WorkflowArtifactRecorded>(pendingUploads.Count);
        var artifactRows = new List<WorkflowArtifactRow>(pendingUploads.Count);

        foreach (var pending in pendingUploads)
        {
            var artifactId = NewArtifactId();

            var row = new WorkflowArtifactRow
            {
                ArtifactId = artifactId,
                WorkflowRunId = workflowRunId,
                TaskRunId = taskRunId,
                Path = pending.Path,
                RecordedAt = now,
                ArtifactStoragePath = pending.StoragePath,
                Kind = string.IsNullOrWhiteSpace(pending.Kind) ? "file" : pending.Kind,
                ContentType = pending.ContentType,
                ContentHash = pending.ContentHash,
                Size = pending.Size,
                ProjectId = projectId,
                IssueId = issueId,
                DisplayName = DeriveDisplayName(pending.Path),
            };

            artifactRows.Add(row);
            recordedEvents.Add(new WorkflowArtifactRecorded(
                workflowRunId,
                taskRunId,
                pending.Path,
                now));
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        db.WorkflowArtifacts.AddRange(artifactRows);
        db.WorkflowArtifactPendingUploads.RemoveRange(pendingUploads);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _log.LogInformation(
            "Bound {Count} artifact(s) for workflow run {WorkflowRunId}, task run {TaskRunId}",
            artifactRows.Count, workflowRunId, taskRunId);

        return new WorkflowArtifactBindResult(recordedEvents);
    }

    private static string NewArtifactId() => $"art_{Guid.NewGuid():N}";

    private static string? DeriveDisplayName(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var name = path.Replace('\\', '/').Trim('/');
        var lastSlash = name.LastIndexOf('/');
        return lastSlash >= 0 ? name[(lastSlash + 1)..] : name;
    }
}
