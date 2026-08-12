using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Workflow.Domain.Artifacts;
using Mohist.Workflow.Definition;

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
        int? issueNumber = null,
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

    public WorkflowArtifactBindService(
        IDbContextFactory<MohistDbContext> dbFactory,
        ILogger<WorkflowArtifactBindService> log,
        TimeProvider time)
    {
        _dbFactory = dbFactory;
        _log = log;
        _time = time;
    }

    public async Task<WorkflowArtifactBindResult> BindAsync(
        string workflowRunId,
        string workId,
        string taskRunId,
        string[] artifactUploadIds,
        TaskArtifactCapture? declaredArtifacts,
        JsonElement? variables = null,
        string? projectId = null,
        int? issueNumber = null,
        CancellationToken cancellationToken = default)
    {
        if (artifactUploadIds is null || artifactUploadIds.Length == 0)
            return new WorkflowArtifactBindResult([]);
        var requestedUploadIds = artifactUploadIds
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var existingArtifacts = await db.WorkflowArtifacts
            .Where(artifact => artifact.SourceUploadId != null
                && requestedUploadIds.Contains(artifact.SourceUploadId))
            .ToListAsync(cancellationToken);
        var existingByUploadId = existingArtifacts.ToDictionary(
            artifact => artifact.SourceUploadId!,
            StringComparer.Ordinal);
        var replayMismatch = existingArtifacts.FirstOrDefault(artifact =>
            !string.Equals(artifact.WorkflowRunId, workflowRunId, StringComparison.Ordinal)
            || !string.Equals(artifact.TaskRunId, taskRunId, StringComparison.Ordinal));
        if (replayMismatch is not null)
        {
            return new WorkflowArtifactBindResult(
                [],
                $"Upload id {replayMismatch.SourceUploadId} is already bound to a different workflow task attempt");
        }

        var pendingIds = requestedUploadIds
            .Where(uploadId => !existingByUploadId.ContainsKey(uploadId))
            .ToArray();

        var pendingUploads = await db.WorkflowArtifactPendingUploads
            .Where(p =>
                p.WorkflowRunId == workflowRunId
                && p.WorkId == workId
                && p.TaskRunId == taskRunId
                && pendingIds.Contains(p.UploadId))
            .ToListAsync(cancellationToken);

        var matchedIds = pendingUploads.Select(p => p.UploadId)
            .Concat(existingByUploadId.Keys)
            .ToHashSet(StringComparer.Ordinal);
        var foreignIds = requestedUploadIds.Where(id => !matchedIds.Contains(id)).ToList();
        if (foreignIds.Count > 0)
        {
            return new WorkflowArtifactBindResult(
                [],
                $"Upload ids {string.Join(", ", foreignIds)} do not belong to workflow run {workflowRunId} and work item {workId}");
        }

        var now = _time.GetUtcNow();
        var recordedEventsByUploadId = existingArtifacts.ToDictionary(
            artifact => artifact.SourceUploadId!,
            artifact => new WorkflowArtifactRecorded(
                workflowRunId, taskRunId, artifact.Path, artifact.RecordedAt),
            StringComparer.Ordinal);
        var artifactRows = new List<WorkflowArtifactRow>(pendingUploads.Count);

        foreach (var pending in pendingUploads)
        {
            var artifactId = NewArtifactId();

            var row = new WorkflowArtifactRow
            {
                ArtifactId = artifactId,
                WorkflowRunId = workflowRunId,
                TaskRunId = taskRunId,
                SourceUploadId = pending.UploadId,
                Path = pending.Path,
                RecordedAt = now,
                ArtifactStoragePath = pending.StoragePath,
                Kind = string.IsNullOrWhiteSpace(pending.Kind) ? "file" : pending.Kind,
                ContentType = pending.ContentType,
                ContentHash = pending.ContentHash,
                Size = pending.Size,
                ProjectId = projectId,
                IssueNumber = issueNumber,
                DisplayName = DeriveDisplayName(pending.Path),
            };

            artifactRows.Add(row);
            recordedEventsByUploadId.Add(pending.UploadId, new WorkflowArtifactRecorded(
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

        return new WorkflowArtifactBindResult(
            requestedUploadIds.Select(uploadId => recordedEventsByUploadId[uploadId]).ToList());
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
