using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Epics;
using Mohist.Server.Infrastructure.Persistence.Db;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Storage;
using Mohist.Server.Issue.WorkflowProfiles;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Queries;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Projection;
using Mohist.Server.Workflow.Storage;
using Mohist.Server.Workflow.Views;

namespace Mohist.Server.Issue.Queries;

public class IssueQueryService
{
    private static readonly JsonSerializerOptions RunJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IssueWorkflowProfileRegistry _profiles;

    public IssueQueryService(
        IDbContextFactory<MohistDbContext> dbFactory,
        IssueWorkflowProfileRegistry profiles)
    {
        _dbFactory = dbFactory;
        _profiles = profiles;
    }

    public async Task<IssueReadModel?> GetAsync(string projectId, int number, ProjectInfo? project = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var issue = await LoadIssueAsync(db, projectId, number);
        return issue is null ? null : await EnrichAsync(db, await ToReadModelAsync(db, issue, project));
    }

    public async Task<IssueInfo?> GetInfoAsync(string projectId, int number, ProjectInfo? project = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var issue = await LoadIssueAsync(db, projectId, number);
        if (issue is null) return null;
        var info = ToInfo(issue, project);
        await PopulateProfileDataOnInfoAsync(db, info, projectId, number);
        return info;
    }

    private static async Task<Domain.Issue?> LoadIssueAsync(MohistDbContext db, string projectId, int number)
    {
        var key = $"{projectId}:{number}"; var row = await db.IssueStates
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Key == key);
        return row is null ? null : IssueSnapshot.DeserializeIssue(row.StateJson);
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
        await using var db = await _dbFactory.CreateDbContextAsync(); var rows = await db.IssueStates
            .AsNoTracking()
            .Where(row => EF.Functions.Like(row.Key, projectId + ":%"))
            .ToListAsync();
        var list = rows
            .Select(row => IssueSnapshot.DeserializeIssue(row.StateJson))
            .Where(issue => issue is not null)
            .Cast<Domain.Issue>()
            .Where(issue => issue.ProjectId == projectId)
            .Select(issue => ToReadModel(ToInfo(issue, project)))
            .OrderBy(i => i.Number)
            .ToList();

        ApplyWorkflowProjections(list, await LoadWorkflowStatesAsync(db, list));
        await PopulateProfileDataAsync(db, list);

        var query = list.AsEnumerable();

        if (archived == true)
            query = query.Where(i => i.ArchivedAt != null);
        else if (all != true)
            query = query.Where(i => i.ArchivedAt == null);

        if (!string.IsNullOrEmpty(stage))
            query = query.Where(i => string.Equals(i.Status, stage, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(label))
            query = query.Where(i => i.Labels.Contains(label, StringComparer.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(priority))
            query = query.Where(i => string.Equals(i.Priority, priority, StringComparison.OrdinalIgnoreCase));

        return await EnrichAsync(db, query.OrderBy(i => i.Number).ToList());
    }

    private async Task<IssueReadModel> ToReadModelAsync(MohistDbContext db, Domain.Issue issue, ProjectInfo? project = null)
    {
        var model = ToReadModel(ToInfo(issue, project));
        ApplyWorkflowProjections([model], await LoadWorkflowStatesAsync(db, [model]));
        await PopulateProfileDataAsync(db, [model]);
        return model;
    }

    public static IssueInfo ToInfo(Domain.Issue issue, ProjectInfo? project = null)
    {
        return new()
        {
            Id = issue.Id,
            Number = issue.Number,
            Title = issue.Title,
            Body = issue.Body,
            Status = MohistDefaultWorkflowProjection.IssueStatusName(issue.Status),
            Health = MohistDefaultWorkflowProjection.Health(issue.Status),
            ProjectId = issue.ProjectId,
            ProjectName = project?.Name,
            Labels = issue.Labels,
            Priority = issue.Priority,
            Model = null,
            AgentConfig = null,
            StageModels = null,
            StageVariables = null,
            CreatedAt = issue.CreatedAt.ToString("o"),
            UpdatedAt = issue.UpdatedAt.ToString("o"),
            ArchivedAt = issue.ArchivedAt?.ToString("o"),
            WorkflowRunId = issue.WorkflowRunId,
            WorkflowProfileId = IssueWorkflowProfiles.DefaultId,
            PrerequisiteNumbers = issue.PrerequisiteNumbers,
            Repository = issue.Repository,
        };
    }

    public static IssueReadModel ToReadModel(IssueInfo issue) => new()
    {
        Id = issue.Id,
        Number = issue.Number,
        Title = issue.Title,
        Body = issue.Body,
        Status = issue.Status,
        Health = issue.Health,
        ProjectId = issue.ProjectId,
        ProjectName = issue.ProjectName,
        Labels = issue.Labels,
        Priority = issue.Priority,
        Model = issue.Model,
        AgentConfig = issue.AgentConfig,
        StageModels = issue.StageModels,
        CreatedAt = issue.CreatedAt,
        UpdatedAt = issue.UpdatedAt,
        ArchivedAt = issue.ArchivedAt,
        Attention = issue.Attention,
        WorkflowRunId = issue.WorkflowRunId,
        WorkflowProfileId = issue.WorkflowProfileId,
        WorkflowProfileMode = issue.WorkflowProfileMode,
        PrerequisiteNumbers = issue.PrerequisiteNumbers,
        Repository = issue.Repository,
    };

    private async Task PopulateProfileDataAsync(MohistDbContext db, List<IssueReadModel> issues)
    {
        var keys = issues.Select(i => $"{i.ProjectId}:{i.Number}").ToArray();
        var rows = await db.IssueProfiles
            .AsNoTracking()
            .Where(r => keys.Contains(r.Key))
            .ToListAsync();
        var profilesByKey = new Dictionary<string, IssueWorkflowProfile>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var profile = IssueWorkflowProfileSnapshot.Deserialize(row.StateJson);
            if (profile is not null)
                profilesByKey[row.Key] = profile;
        }
        foreach (var issue in issues)
        {
            var key = $"{issue.ProjectId}:{issue.Number}";
            if (profilesByKey.TryGetValue(key, out var profile))
            {
                issue.WorkflowProfileId = profile.SourceProfileId;
                issue.WorkflowProfileMode = profile.UpdateMode.ToString();
            }
        }
    }

    private async Task PopulateProfileDataOnInfoAsync(MohistDbContext db, IssueInfo info, string projectId, int number)
    {
        var key = $"{projectId}:{number}";
        var row = await db.IssueProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Key == key);
        if (row is not null)
        {
            var profile = IssueWorkflowProfileSnapshot.Deserialize(row.StateJson);
            if (profile is not null)
            {
                info.WorkflowProfileId = profile.SourceProfileId;
                info.WorkflowProfileMode = profile.UpdateMode.ToString();
            }
        }
    }

    public async Task<IssueProfileRow?> LoadIssueProfileAsync(string key)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.IssueProfiles.AsNoTracking().FirstOrDefaultAsync(r => r.Key == key);
    }

    private async Task<Dictionary<string, WorkflowStatusView>> LoadWorkflowStatesAsync(MohistDbContext db, IReadOnlyCollection<IssueReadModel> issues)
    {
        var workflowRunIds = issues
            .Select(i => i.WorkflowRunId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (workflowRunIds.Length == 0) return [];

        var runRows = await db.WorkflowRuns
            .AsNoTracking()
            .Where(row => workflowRunIds.Contains(row.WorkflowRunId))
            .ToListAsync();

        var leaseRows = await db.WorkflowLeases
            .AsNoTracking()
            .Where(row => workflowRunIds.Contains(row.WorkflowRunId))
            .ToListAsync();

        var leases = leaseRows.ToDictionary(r => r.WorkflowRunId, r => DeserializeLease(r.StateJson), StringComparer.Ordinal);

        var workflows = new Dictionary<string, WorkflowStatusView>(StringComparer.Ordinal);
        foreach (var row in runRows)
        {
            var run = DeserializeRun(row.State);
            if (run is null) continue;
            leases.TryGetValue(row.WorkflowRunId, out var lease);
            var snapshot = WorkflowStatusReader.Read(run, lease);
            if (snapshot is not null)
                workflows[row.WorkflowRunId] = snapshot;
        }
        return workflows;
    }

    private static WorkLease? DeserializeLease(string json) => WorkflowLeaseJson.Deserialize(json);

    private void ApplyWorkflowProjections(IReadOnlyCollection<IssueReadModel> issues, IReadOnlyDictionary<string, WorkflowStatusView> workflows)
    {
        foreach (var issue in issues)
        {
            if (issue.WorkflowRunId is null || !workflows.TryGetValue(issue.WorkflowRunId, out var status)) continue;

            var projection = _profiles.Get(issue.WorkflowProfileId).ProjectWorkflowState(issue, status);

            issue.Status = projection.IssueStatus;
            issue.Health = projection.Health;
            issue.BlockedReason = projection.BlockedReason;
            issue.StageApproval = projection.StageApproval;
            issue.Attention = projection.Attention;
            issue.WorkflowStage = status.CurrentStage;
            issue.WorkflowStatus = status.Status;
            issue.WorkflowStageProgress = ComputeStageProgress(status);
        }
    }

    private static WorkflowStageProgress? ComputeStageProgress(WorkflowStatusView status)
    {
        if (IsNonMeaningfulProgressState(status))
            return null;

        var currentStage = status.Stages.FirstOrDefault(s => s.Stage == status.CurrentStage);
        if (currentStage is null) return null;

        var userTasks = currentStage.Tasks.Where(t => t.Classification == TaskClassification.UserFacing).ToList();
        if (userTasks.Count == 0) return null;

        var total = userTasks.Count;
        var completed = userTasks.Count(t => t.Status == "Completed");
        var running = userTasks.Count(t => t.Status == "Running");
        var failed = userTasks.Count(t => t.Status == "Failed");

        if (total == 0) return null;

        var currentTaskTitle = userTasks.FirstOrDefault(t => t.Status is "Running" or "Pending")?.Title;

        return new WorkflowStageProgress(
            status.CurrentStage!,
            total,
            completed,
            running,
            failed,
            currentTaskTitle);
    }

    private static bool IsNonMeaningfulProgressState(WorkflowStatusView status)
    {
        if (status.Status is "Completed" or "Failed" or "AwaitingApproval" or "Paused")
            return true;

        var currentStage = status.Stages.FirstOrDefault(s => s.Stage == status.CurrentStage);
        if (currentStage is null)
            return true;

        // Approval-only waiting should be omitted from the board API even if the run-level status
        // still looks active after projection/materialization.
        return currentStage.ApprovalStatus is { Result: null }
            || (currentStage.Tasks.All(t => t.Status == "Completed") && currentStage.Checks.All(c => c.Status == "Completed"));
    }

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
            .SelectMany(issue => issue.PrerequisiteNumbers.Select(prerequisiteNumber => new IssuePrerequisiteRow
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
            var rows = await db.IssueStates.AsNoTracking()
                .Where(row => keys.Contains(row.Key))
                .ToListAsync();
            foreach (var row in rows)
            {
                var issue = IssueSnapshot.DeserializeIssue(row.StateJson);
                if (issue is not null)
                    prereqIssues[issue.Number] = ToReadModel(ToInfo(issue));
            }
        }
        foreach (var group in prereqRows.GroupBy(p => p.IssueNumber))
        {
            if (!byNumber.TryGetValue(group.Key, out var issue)) continue;
            var summaries = group
                .Select(p => prereqIssues.TryGetValue(p.PrerequisiteNumber, out var prereq) ? IssuePrerequisiteSummary.FromReadModel(prereq) : null)
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

    public static IssueCommentDto ToCommentDto(IssueCommentRow comment) =>
        new(comment.Id, comment.IssueId, comment.Body, comment.CreatedAt.ToString("o"));

    private static WorkflowRun? DeserializeRun(string json)
    {
        try { return JsonSerializer.Deserialize<WorkflowRun>(json, RunJsonOptions); }
        catch { return null; }
    }

}
