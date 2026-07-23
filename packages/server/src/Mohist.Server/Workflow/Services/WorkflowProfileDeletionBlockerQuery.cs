using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Workflow.Services;

/// <summary>
/// issue-477 T-001: deletion-blocker projection for the WorkflowProfile
/// collection. Mirrors <see cref="Mohist.Server.Project.Services.RepositoryDeletionBlockerQuery"/>'s
/// shape: a single Project-scoped projection that issues one read each
/// for the Project default, the Issue explicit selections (including
/// terminal Issues), and the active WorkflowRun bindings.
///
/// The query is a *diagnostic* companion to the
/// <c>WorkflowProfileReferenceCoordinator</c>; the FK backstop on the
/// nullable custom-Profile backing-key columns is the final concurrency
/// safety net. Built-in Profiles return an empty blocker.
/// </summary>
public sealed class WorkflowProfileDeletionBlockerQuery : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public WorkflowProfileDeletionBlockerQuery(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<WorkflowProfileDeletionBlockers> GetBlockersAsync(
        string projectId, string profileId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(profileId))
            return WorkflowProfileDeletionBlockers.Empty;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var projectDefault = await IsProjectDefaultAsync(db, projectId, profileId, ct);
        var issueSelection = await ListIssueBlockersAsync(db, projectId, profileId, ct);
        var activeRun = await FindActiveRunAsync(db, projectId, profileId, ct);

        return new WorkflowProfileDeletionBlockers(
            ProjectDefault: projectDefault,
            IssueSelections: issueSelection,
            ActiveRun: activeRun);
    }

    private static async Task<bool> IsProjectDefaultAsync(
        MohistDbContext db, string projectId, string profileId, CancellationToken ct)
    {
        var row = await db.ProjectWorkflowProfiles.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ProjectId == projectId, ct);
        return row is not null
            && string.Equals(row.DefaultWorkflowProfileId, profileId, StringComparison.Ordinal);
    }

    private static async Task<IReadOnlyList<WorkflowProfileIssueBlocker>> ListIssueBlockersAsync(
        MohistDbContext db, string projectId, string profileId, CancellationToken ct)
    {
        var rows = await db.Issues.AsNoTracking()
            .Where(r => r.ProjectId == projectId
                && r.WorkflowProfileIdKey != null
                && r.WorkflowProfileIdKey == profileId)
            .ToListAsync(ct);

        return rows
            .Select(r => new WorkflowProfileIssueBlocker(r.ProjectId ?? string.Empty, r.Number ?? 0, r.Status ?? "unknown"))
            .ToList();
    }

    private static async Task<WorkflowProfileRunBlocker?> FindActiveRunAsync(
        MohistDbContext db, string projectId, string profileId, CancellationToken ct)
    {
        var row = await db.WorkflowRuns.AsNoTracking()
            .Where(r => r.MetadataProjectId == projectId
                && r.WorkflowProfileIdKey != null
                && r.WorkflowProfileIdKey == profileId
                && r.Status != null
                && !TerminalRunStatuses.Contains(r.Status.ToLower()))
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new { r.WorkflowRunId, r.Status })
            .FirstOrDefaultAsync(ct);

        if (row is null) return null;
        return new WorkflowProfileRunBlocker(row.WorkflowRunId, row.Status ?? "unknown");
    }

    private static readonly HashSet<string> TerminalRunStatuses = new(StringComparer.Ordinal)
    {
        "done",
        "completed",
        "failed",
        "cancelled",
        "canceled",
    };
}

public sealed record WorkflowProfileDeletionBlockers(
    bool ProjectDefault,
    IReadOnlyList<WorkflowProfileIssueBlocker> IssueSelections,
    WorkflowProfileRunBlocker? ActiveRun)
{
    public static WorkflowProfileDeletionBlockers Empty { get; } =
        new(false, Array.Empty<WorkflowProfileIssueBlocker>(), null);

    public bool HasAnyBlocker =>
        ProjectDefault
        || IssueSelections.Count > 0
        || ActiveRun is not null;
}

public sealed record WorkflowProfileIssueBlocker(
    string ProjectId,
    int IssueNumber,
    string Status);

public sealed record WorkflowProfileRunBlocker(
    string WorkflowRunId,
    string Status);
