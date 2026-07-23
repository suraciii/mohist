using System.Text.Json;
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
        var activeRuns = await ListActiveRunsAsync(db, projectId, profileId, ct);

        return new WorkflowProfileDeletionBlockers(
            ProjectDefault: projectDefault,
            IssueSelections: issueSelection,
            ActiveRuns: activeRuns);
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

    private static async Task<IReadOnlyList<WorkflowProfileRunBlocker>> ListActiveRunsAsync(
        MohistDbContext db, string projectId, string profileId, CancellationToken ct)
    {
        var rows = await db.WorkflowRuns.AsNoTracking()
            .Where(r => r.MetadataProjectId == projectId
                && r.Status != null
                && !TerminalRunStatuses.Contains(r.Status.ToLower()))
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(ct);

        return rows
            .Where(row => string.Equals(row.WorkflowProfileIdKey, profileId, StringComparison.Ordinal)
                || string.Equals(ReadProfileId(row.State), profileId, StringComparison.Ordinal))
            .Select(row => new WorkflowProfileRunBlocker(row.WorkflowRunId, row.Status ?? "unknown"))
            .ToList();
    }

    private static string? ReadProfileId(string? state)
    {
        if (string.IsNullOrWhiteSpace(state)) return null;
        try
        {
            using var document = JsonDocument.Parse(state);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            if (root.TryGetProperty("workflowProfileId", out var profileId)
                && profileId.ValueKind == JsonValueKind.String)
            {
                return profileId.GetString();
            }

            if (root.TryGetProperty("metadata", out var metadata)
                && metadata.ValueKind == JsonValueKind.Object
                && metadata.TryGetProperty("annotations", out var annotations)
                && annotations.ValueKind == JsonValueKind.Object
                && annotations.TryGetProperty("workflowProfileId", out var legacyProfileId)
                && legacyProfileId.ValueKind == JsonValueKind.String)
            {
                return legacyProfileId.GetString();
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Mirrors WorkflowRunStatusExtensions.IsTerminal: only Stopped and
    // Completed are permanently terminal. `Failed` is a recoverable mid-state
    // (Retry/Rerun/RerunFromStage revive it) and its custom-Profile backing
    // key is retained, so a failed run still blocks Profile deletion.
    private static readonly HashSet<string> TerminalRunStatuses = new(StringComparer.Ordinal)
    {
        "done",
        "completed",
        "stopped",
    };
}

public sealed record WorkflowProfileDeletionBlockers(
    bool ProjectDefault,
    IReadOnlyList<WorkflowProfileIssueBlocker> IssueSelections,
    IReadOnlyList<WorkflowProfileRunBlocker> ActiveRuns)
{
    public static WorkflowProfileDeletionBlockers Empty { get; } =
        new(false, Array.Empty<WorkflowProfileIssueBlocker>(), Array.Empty<WorkflowProfileRunBlocker>());

    public bool HasAnyBlocker =>
        ProjectDefault
        || IssueSelections.Count > 0
        || ActiveRuns.Count > 0;
}

public sealed record WorkflowProfileIssueBlocker(
    string ProjectId,
    int IssueNumber,
    string Status);

public sealed record WorkflowProfileRunBlocker(
    string WorkflowRunId,
    string Status);
