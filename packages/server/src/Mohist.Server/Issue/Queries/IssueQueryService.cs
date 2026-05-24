using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Project.Domain;
using Mohist.Server.Storage.Db;

namespace Mohist.Server.Issue.Queries;

public class IssueQueryService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly string _issueType = typeof(Domain.Issue).FullName!;

    public IssueQueryService(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<IssueInfo?> GetAsync(string projectId, int number, ProjectInfo? project = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var key = $"{projectId}:{number}";
        var row = await db.GrainStates
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Key == key && row.Type == _issueType);
        var issue = row is null ? null : JsonSerializer.Deserialize<Domain.Issue>(row.JsonState);
        return issue is null ? null : ToInfo(issue, project);
    }

    public async Task<List<IssueInfo>> ListAsync(
        string projectId,
        ProjectInfo? project = null,
        string? stage = null,
        string? label = null,
        string? priority = null,
        bool? archived = null,
        bool? all = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var rows = await db.GrainStates
            .AsNoTracking()
            .Where(row => row.Type == _issueType && EF.Functions.Like(row.Key, projectId + ":%"))
            .ToListAsync();
        var query = rows
            .Select(row => JsonSerializer.Deserialize<Domain.Issue>(row.JsonState))
            .Where(issue => issue is not null)
            .Cast<Domain.Issue>()
            .Where(issue => issue.ProjectId == projectId)
            .Select(issue => ToInfo(issue, project))
            .AsEnumerable();

        if (archived == true)
            query = query.Where(i => i.ArchivedAt != null);
        else if (all != true)
            query = query.Where(i => i.ArchivedAt == null);

        if (!string.IsNullOrEmpty(stage))
            query = query.Where(i => string.Equals(i.Stage, stage, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(label))
            query = query.Where(i => i.Labels.Contains(label, StringComparer.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(priority))
            query = query.Where(i => string.Equals(i.Priority, priority, StringComparison.OrdinalIgnoreCase));

        return query.OrderBy(i => i.Number).ToList();
    }

    public static IssueInfo ToInfo(Domain.Issue issue, ProjectInfo? project = null) => new()
    {
        Id = issue.Id,
        Number = issue.Number,
        Title = issue.Title,
        Body = issue.Body,
        Stage = issue.Stage.ToString().ToLower(),
        Status = issue.RuntimeStatus.ToString().ToLower(),
        ProjectId = issue.ProjectId,
        ProjectName = project?.Name,
        Labels = issue.Labels,
        Priority = issue.Priority,
        Model = issue.Model,
        StageModels = issue.StageModels,
        CreatedAt = issue.CreatedAt.ToString("o"),
        UpdatedAt = issue.UpdatedAt.ToString("o"),
        ArchivedAt = issue.ArchivedAt?.ToString("o"),
        ApprovalState = issue.ApprovalState,
        MergeState = issue.MergeState?.ToString().ToLower(),
        RetryCount = issue.RetryCount,
        ConflictRetryCount = issue.ConflictRetryCount,
        BlockedReason = issue.BlockedReason,
        WorkflowRunId = issue.WorkflowRunId,
    };
}
