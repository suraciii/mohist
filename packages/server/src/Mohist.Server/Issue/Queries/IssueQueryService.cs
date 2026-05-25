using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Epics;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.WorkflowProfiles;
using Mohist.Server.Project.Domain;
using Mohist.Server.Storage.Db;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Issue.Queries;

public class IssueQueryService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IGrainFactory _grains;
    private readonly IssueWorkflowProfileRegistry _profiles;
    private readonly string _issueType = typeof(Domain.Issue).FullName!;

    public IssueQueryService(IDbContextFactory<MohistDbContext> dbFactory, IGrainFactory grains, IssueWorkflowProfileRegistry profiles)
    {
        _dbFactory = dbFactory;
        _grains = grains;
        _profiles = profiles;
    }

    public async Task<IssueReadModel?> GetAsync(string projectId, int number, ProjectInfo? project = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var key = $"{projectId}:{number}";
        var row = await db.GrainStates
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Key == key && row.Type == _issueType);
        var issue = row is null ? null : JsonSerializer.Deserialize<Domain.Issue>(row.JsonState);
        return issue is null ? null : await EnrichAsync(db, await ToReadModelAsync(issue, project));
    }

    public async Task<List<IssueReadModel>> ListAsync(
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
        var list = rows
            .Select(row => JsonSerializer.Deserialize<Domain.Issue>(row.JsonState))
            .Where(issue => issue is not null)
            .Cast<Domain.Issue>()
            .Where(issue => issue.ProjectId == projectId)
            .Select(issue => ToReadModel(ToInfo(issue, project)))
            .OrderBy(i => i.Number)
            .ToList();

        foreach (var issue in list)
            await ApplyWorkflowProjectionAsync(issue);

        var query = list.AsEnumerable();

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

        return await EnrichAsync(db, query.OrderBy(i => i.Number).ToList());
    }

    private async Task<IssueReadModel> ToReadModelAsync(Domain.Issue issue, ProjectInfo? project = null)
    {
        var model = ToReadModel(ToInfo(issue, project));
        await ApplyWorkflowProjectionAsync(model);
        return model;
    }

    public static IssueInfo ToInfo(Domain.Issue issue, ProjectInfo? project = null) => new()
    {
        Id = issue.Id,
        Number = issue.Number,
        Title = issue.Title,
        Body = issue.Body,
        Stage = IssueDomainNames.Status(issue.Status),
        Status = IssueRuntimeSummary(issue.Status, issue.Attention),
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
        Attention = issue.Attention,
        WorkflowRunId = issue.WorkflowRunId,
        WorkflowProfileId = issue.WorkflowProfileId,
        PrerequisiteNumbers = issue.PrerequisiteNumbers,
    };

    public static IssueReadModel ToReadModel(IssueInfo issue) => new()
    {
        Id = issue.Id,
        Number = issue.Number,
        Title = issue.Title,
        Body = issue.Body,
        Stage = issue.Stage,
        Status = issue.Status,
        ProjectId = issue.ProjectId,
        ProjectName = issue.ProjectName,
        Labels = issue.Labels,
        Priority = issue.Priority,
        Model = issue.Model,
        StageModels = issue.StageModels,
        CreatedAt = issue.CreatedAt,
        UpdatedAt = issue.UpdatedAt,
        ArchivedAt = issue.ArchivedAt,
        ApprovalState = issue.ApprovalState,
        MergeState = issue.MergeState,
        RetryCount = issue.RetryCount,
        ConflictRetryCount = issue.ConflictRetryCount,
        BlockedReason = issue.BlockedReason,
        Attention = issue.Attention,
        WorkflowRunId = issue.WorkflowRunId,
        WorkflowProfileId = issue.WorkflowProfileId,
        PrerequisiteNumbers = issue.PrerequisiteNumbers,
    };

    private async Task ApplyWorkflowProjectionAsync(IssueReadModel issue)
    {
        if (issue.WorkflowRunId is null) return;

        var workflow = _grains.GetGrain<IWorkflowGrain>(issue.WorkflowRunId);
        var status = await workflow.GetStatusAsync();
        if (status is null) return;

        var projection = _profiles.Get(issue.WorkflowProfileId).Project(issue, status);

        issue.Stage = projection.IssueStatus;
        issue.Status = projection.RuntimeStatus;
        issue.BlockedReason = projection.BlockedReason;
        issue.ApprovalState = projection.ApprovalState;
        issue.Attention = projection.Attention;
    }

    private static string IssueRuntimeSummary(IssueStatus status, IssueAttention? attention) =>
        status switch
        {
            IssueStatus.Done => "completed",
            IssueStatus.Cancelled => "cancelled",
            _ when attention?.Reason is IssueAttentionReasons.Blocked or IssueAttentionReasons.WorkflowFailed => "blocked",
            _ when attention is not null => "attention",
            _ => "active",
        };

    private static async Task<List<IssueReadModel>> EnrichAsync(MohistDbContext db, List<IssueReadModel> issues)
    {
        if (issues.Count == 0) return issues;

        var projectId = issues[0].ProjectId;
        var numbers = issues.Select(i => i.Number).ToArray();
        var issueIds = issues.Select(i => i.Id).ToArray();
        var byNumber = issues.ToDictionary(i => i.Number);
        var byId = issues.ToDictionary(i => i.Id);

        var comments = await db.IssueComments.AsNoTracking()
            .Where(c => c.ProjectId == projectId && numbers.Contains(c.IssueNumber))
            .ToListAsync();
        comments = comments.OrderBy(c => c.CreatedAt).ToList();
        foreach (var group in comments.GroupBy(c => c.IssueNumber))
        {
            if (byNumber.TryGetValue(group.Key, out var issue))
            {
                issue.Comments = group.Select(ToCommentDto).ToArray();
            }
        }

        var persistedRows = await db.IssuePrerequisites.AsNoTracking()
            .Where(p => p.ProjectId == projectId && numbers.Contains(p.IssueNumber))
            .ToListAsync();
        var prereqRows = issues
            .SelectMany(issue => issue.PrerequisiteNumbers.Select(prerequisiteNumber => new IssuePrerequisiteEntry
            {
                ProjectId = projectId,
                IssueNumber = issue.Number,
                PrerequisiteNumber = prerequisiteNumber,
            }))
            .Concat(persistedRows)
            .GroupBy(p => new { p.IssueNumber, p.PrerequisiteNumber })
            .Select(group => group.First())
            .ToList();
        var prereqNumbers = prereqRows.Select(p => p.PrerequisiteNumber).Distinct().ToArray();
        var prereqIssues = issues.Where(i => prereqNumbers.Contains(i.Number)).ToDictionary(i => i.Number);
        var missingPrereqNumbers = prereqNumbers.Where(number => !prereqIssues.ContainsKey(number)).ToArray();
        if (missingPrereqNumbers.Length > 0)
        {
            var keys = missingPrereqNumbers.Select(number => $"{projectId}:{number}").ToArray();
            var rows = await db.GrainStates.AsNoTracking()
                .Where(row => row.Type == typeof(Domain.Issue).FullName! && keys.Contains(row.Key))
                .ToListAsync();
            foreach (var row in rows)
            {
                var domain = JsonSerializer.Deserialize<Domain.Issue>(row.JsonState);
                if (domain is not null)
                    prereqIssues[domain.Number] = ToReadModel(ToInfo(domain));
            }
        }
        foreach (var group in prereqRows.GroupBy(p => p.IssueNumber))
        {
            if (!byNumber.TryGetValue(group.Key, out var issue)) continue;
            var summaries = group
                .Select(p => prereqIssues.TryGetValue(p.PrerequisiteNumber, out var prereq) ? ToPrerequisiteSummary(prereq) : null)
                .Where(p => p is not null)
                .Cast<IssuePrerequisiteSummary>()
                .ToArray();
            issue.Prerequisites = summaries;
            issue.StartEligibility = IssueStartEligibility.FromPrerequisites(summaries);
        }

        var epicLinks = await db.EpicIssues.AsNoTracking()
            .Where(link => link.ProjectId == projectId && issueIds.Contains(link.IssueId))
            .ToListAsync();
        if (epicLinks.Count > 0)
        {
            var epicIds = epicLinks.Select(l => l.EpicId).Distinct().ToArray();
            var epics = await db.Epics.AsNoTracking()
                .Where(epic => epic.ProjectId == projectId && epicIds.Contains(epic.Id))
                .ToDictionaryAsync(e => e.Id);
            foreach (var link in epicLinks)
            {
                if (byId.TryGetValue(link.IssueId, out var issue) && epics.TryGetValue(link.EpicId, out var epic))
                {
                    issue.PrimaryEpic = new IssuePrimaryEpic
                    {
                        Id = epic.Id,
                        Title = epic.Title,
                        Status = epic.Status,
                        Priority = epic.Priority,
                    };
                }
            }
        }

        return issues;
    }

    private static async Task<IssueReadModel> EnrichAsync(MohistDbContext db, IssueInfo issue) =>
        (await EnrichAsync(db, [ToReadModel(issue)]))[0];

    private static async Task<IssueReadModel> EnrichAsync(MohistDbContext db, IssueReadModel issue) =>
        (await EnrichAsync(db, [issue]))[0];

    public static IssueCommentDto ToCommentDto(IssueCommentEntry comment) =>
        new(comment.Id, comment.IssueId, comment.Body, comment.CreatedAt.ToString("o"));

    private static IssuePrerequisiteSummary ToPrerequisiteSummary(IssueReadModel issue) => new()
    {
        IssueId = issue.Id,
        Number = issue.Number,
        Title = issue.Title,
        Delivered = issue.Stage == "done" || issue.Status == "completed" || issue.MergeState == "merged",
        Stage = issue.Stage,
        Status = issue.Status,
        MergeState = issue.MergeState,
    };
}
