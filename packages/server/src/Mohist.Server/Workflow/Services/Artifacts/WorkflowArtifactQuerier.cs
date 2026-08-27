using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;

namespace Mohist.Server.Workflow.Services.Artifacts;

public sealed record WorkflowArtifactInfo(
    string ArtifactId,
    string WorkflowRunId,
    string ActionAttemptId,
    string Path,
    string Kind,
    string? ContentType,
    long? Size,
    DateTimeOffset RecordedAt,
    string? DisplayName,
    string ArtifactStoragePath);

public sealed record WorkflowArtifactDirectoryEntryInfo(
    string RelativePath,
    long Size,
    string? ContentType);

public sealed record WorkflowArtifactDirectoryInfo(
    string ArtifactId,
    string Path,
    string? DisplayName,
    string RecordedAt,
    IReadOnlyList<WorkflowArtifactDirectoryEntryInfo> Entries,
    long TotalSize);

public interface IWorkflowArtifactQuerier
{
    Task<IReadOnlyList<WorkflowArtifactInfo>> ListLatestAsync(
        string workflowRunId, CancellationToken ct = default);

    Task<IReadOnlyList<WorkflowArtifactInfo>> ListLatestByPathAsync(
        string workflowRunId, string path, CancellationToken ct = default);

    Task<IReadOnlyList<WorkflowArtifactInfo>> ListHistoryAsync(
        string workflowRunId, string path, CancellationToken ct = default);

    Task<IReadOnlyList<WorkflowArtifactInfo>> ListByWorkflowActionAttemptAsync(
        string workflowRunId, string actionAttemptId, CancellationToken ct = default);

    Task<IReadOnlyList<WorkflowArtifactInfo>> ListAsync(
        string workflowRunId, CancellationToken ct = default);

    Task<WorkflowArtifactInfo?> GetArtifactAsync(
        string workflowRunId, string artifactId, CancellationToken ct = default);
}

public sealed class WorkflowArtifactQuerier : IWorkflowArtifactQuerier
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public WorkflowArtifactQuerier(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<IReadOnlyList<WorkflowArtifactInfo>> ListLatestAsync(
        string workflowRunId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.WorkflowArtifacts
            .AsNoTracking()
            .Where(a => a.WorkflowRunId == workflowRunId)
            .ToListAsync(ct);

        return rows
            .GroupBy(a => a.Path)
            .Select(g => g
                .OrderByDescending(a => a.RecordedAt)
                .ThenByDescending(a => a.ActionAttemptId, StringComparer.Ordinal)
                .ThenByDescending(a => a.ArtifactId, StringComparer.Ordinal)
                .First())
            .OrderBy(a => a.Path)
            .Select(MapInfo)
            .ToList();
    }

    public async Task<IReadOnlyList<WorkflowArtifactInfo>> ListLatestByPathAsync(
        string workflowRunId, string path, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        // SQLite cannot translate ORDER BY on DateTimeOffset; the
        // recorded timestamp comparison happens in memory after a
        // path-bounded fetch. The path filter is the selective
        // predicate so the materialized set is small.
        var rows = await db.WorkflowArtifacts
            .AsNoTracking()
            .Where(a => a.WorkflowRunId == workflowRunId && a.Path == path)
            .ToListAsync(ct);

        var latest = rows.Count == 0
            ? null
            : rows
                .OrderByDescending(a => a.RecordedAt)
                .ThenByDescending(a => a.ActionAttemptId, StringComparer.Ordinal)
                .ThenByDescending(a => a.ArtifactId, StringComparer.Ordinal)
                .First();
        return latest is null
            ? Array.Empty<WorkflowArtifactInfo>()
            : new[] { MapInfo(latest) };
    }

    public async Task<IReadOnlyList<WorkflowArtifactInfo>> ListHistoryAsync(
        string workflowRunId, string path, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.WorkflowArtifacts
            .AsNoTracking()
            .Where(a => a.WorkflowRunId == workflowRunId && a.Path == path)
            .ToListAsync(ct);

        return rows
            .OrderBy(a => a.RecordedAt)
            .ThenBy(a => a.ActionAttemptId, StringComparer.Ordinal)
            .ThenBy(a => a.ArtifactId, StringComparer.Ordinal)
            .Select(MapInfo)
            .ToList();
    }

    public async Task<IReadOnlyList<WorkflowArtifactInfo>> ListByWorkflowActionAttemptAsync(
        string workflowRunId, string actionAttemptId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.WorkflowArtifacts
            .AsNoTracking()
            .Where(a => a.WorkflowRunId == workflowRunId && a.ActionAttemptId == actionAttemptId)
            .ToListAsync(ct);

        return rows
            .OrderBy(a => a.Path)
            .ThenBy(a => a.RecordedAt)
            .ThenBy(a => a.ActionAttemptId, StringComparer.Ordinal)
            .ThenBy(a => a.ArtifactId, StringComparer.Ordinal)
            .Select(MapInfo)
            .ToList();
    }

    public async Task<IReadOnlyList<WorkflowArtifactInfo>> ListAsync(
        string workflowRunId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.WorkflowArtifacts
            .AsNoTracking()
            .Where(a => a.WorkflowRunId == workflowRunId)
            .ToListAsync(ct);

        return rows.OrderBy(a => a.Path).ThenBy(a => a.RecordedAt).Select(MapInfo).ToList();
    }

    public async Task<WorkflowArtifactInfo?> GetArtifactAsync(
        string workflowRunId, string artifactId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.WorkflowArtifacts
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.ArtifactId == artifactId && a.WorkflowRunId == workflowRunId, ct);

        return row is null ? null : MapInfo(row);
    }

    private static WorkflowArtifactInfo MapInfo(WorkflowArtifactRow row) => new(
        row.ArtifactId,
        row.WorkflowRunId,
        row.ActionAttemptId,
        row.Path,
        row.Kind,
        row.ContentType,
        row.Size,
        row.RecordedAt,
        row.DisplayName ?? DeriveDisplayName(row.Path),
        row.ArtifactStoragePath);

    private static string DeriveDisplayName(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "unknown";
        var trimmed = path.TrimEnd('/');
        var lastSegment = trimmed.Split('/').LastOrDefault();
        return string.IsNullOrWhiteSpace(lastSegment) ? path : lastSegment;
    }
}
