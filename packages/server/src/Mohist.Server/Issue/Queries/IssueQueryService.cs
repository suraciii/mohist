using Microsoft.EntityFrameworkCore;
using Mohist.Server.Epics;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Storage;
using Mohist.Server.Issue.WorkflowProfiles;
using Mohist.Server.Project.Queries;
using Mohist.Server.Storage.Db;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using System.Text.Json;

namespace Mohist.Server.Issue.Queries;

public class IssueQueryService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IssueWorkflowProfileRegistry _profiles;
    private readonly string _issueType = typeof(Domain.Issue).FullName!;
    private readonly string _workflowType = typeof(WorkflowGrainState).FullName!;

    public IssueQueryService(IDbContextFactory<MohistDbContext> dbFactory, IssueWorkflowProfileRegistry profiles)
    {
        _dbFactory = dbFactory;
        _profiles = profiles;
    }

    public async Task<IssueReadModel?> GetAsync(string projectId, int number, ProjectInfo? project = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var key = $"{projectId}:{number}";
        var row = await db.GrainStates
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Key == key && row.Type == _issueType);
        var issue = row is null ? null : IssueStateStore.Deserialize(row.JsonState);
        return issue is null ? null : await EnrichAsync(db, await ToReadModelAsync(db, issue, project));
    }

    public async Task<IssueInfo?> GetInfoAsync(string projectId, int number, ProjectInfo? project = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var key = $"{projectId}:{number}";
        var row = await db.GrainStates
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Key == key && row.Type == _issueType);
        var issue = row is null ? null : IssueStateStore.Deserialize(row.JsonState);
        return issue is null ? null : ToInfo(issue, project);
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
            .Select(row => IssueStateStore.Deserialize(row.JsonState))
            .Where(issue => issue is not null)
            .Cast<Domain.Issue>()
            .Where(issue => issue.ProjectId == projectId)
            .Select(issue => ToReadModel(ToInfo(issue, project)))
            .OrderBy(i => i.Number)
            .ToList();

        ApplyWorkflowProjections(list, await LoadWorkflowStatesAsync(db, list));

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

    private async Task<IssueReadModel> ToReadModelAsync(MohistDbContext db, Domain.Issue issue, ProjectInfo? project = null)
    {
        var model = ToReadModel(ToInfo(issue, project));
        ApplyWorkflowProjections([model], await LoadWorkflowStatesAsync(db, [model]));
        return model;
    }

    public static IssueInfo ToInfo(Domain.Issue issue, ProjectInfo? project = null) => new()
    {
        Id = issue.Id,
        Number = issue.Number,
        Title = issue.Title,
        Body = issue.Body,
        Stage = IssueDomainNames.Stage(issue.Stage),
        RuntimeStatus = IssueRuntimeSummary(issue.Stage, issue.Attention),
        ProjectId = issue.ProjectId,
        ProjectName = project?.Name,
        Labels = issue.Labels,
        Priority = issue.Priority,
        Model = issue.Model,
        AgentConfig = issue.AgentConfig,
        StageModels = issue.StageModels,
        StageVariables = issue.StageVariables,
        CreatedAt = issue.CreatedAt.ToString("o"),
        UpdatedAt = issue.UpdatedAt.ToString("o"),
        ArchivedAt = issue.ArchivedAt?.ToString("o"),
        StageApproval = issue.StageApproval,
        RetryCount = issue.RetryCount,
        ConflictRetryCount = issue.ConflictRetryCount,
        BlockedReason = issue.BlockedReason,
        Attention = issue.Attention,
        WorkflowRunId = issue.WorkflowRunId,
        WorkflowProfileId = issue.WorkflowProfileId ?? IssueWorkflowProfiles.DefaultId,
        PrerequisiteNumbers = issue.PrerequisiteNumbers,
    };

    public static IssueReadModel ToReadModel(IssueInfo issue) => new()
    {
        Id = issue.Id,
        Number = issue.Number,
        Title = issue.Title,
        Body = issue.Body,
        Stage = issue.Stage,
        RuntimeStatus = issue.RuntimeStatus,
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
        StageApproval = issue.StageApproval,
        RetryCount = issue.RetryCount,
        ConflictRetryCount = issue.ConflictRetryCount,
        BlockedReason = issue.BlockedReason,
        Attention = issue.Attention,
        WorkflowRunId = issue.WorkflowRunId,
        WorkflowStage = issue.WorkflowStage,
        WorkflowStatus = issue.WorkflowStatus,
        WorkflowProfileId = issue.WorkflowProfileId,
        PrerequisiteNumbers = issue.PrerequisiteNumbers,
    };

    private async Task<Dictionary<string, WorkflowStatusSnapshot>> LoadWorkflowStatesAsync(MohistDbContext db, IReadOnlyCollection<IssueReadModel> issues)
    {
        var workflowRunIds = issues
            .Select(i => i.WorkflowRunId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (workflowRunIds.Length == 0) return [];

        var rows = await db.GrainStates
            .AsNoTracking()
            .Where(row => row.Type == _workflowType && workflowRunIds.Contains(row.Key))
            .ToListAsync();

        var workflows = new Dictionary<string, WorkflowStatusSnapshot>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var workflow = DeserializeWorkflow(row.JsonState);
            if (workflow is not null)
                workflows[row.Key] = workflow;
        }
        return workflows;
    }

    private void ApplyWorkflowProjections(IReadOnlyCollection<IssueReadModel> issues, IReadOnlyDictionary<string, WorkflowStatusSnapshot> workflows)
    {
        foreach (var issue in issues)
        {
            if (issue.WorkflowRunId is null || !workflows.TryGetValue(issue.WorkflowRunId, out var status)) continue;

            var projection = _profiles.Get(issue.WorkflowProfileId).ProjectWorkflowState(issue, status);

            issue.Stage = projection.IssueStage;
            issue.RuntimeStatus = projection.RuntimeStatus;
            issue.BlockedReason = projection.BlockedReason;
            issue.StageApproval = projection.StageApproval;
            issue.Attention = projection.Attention;
            issue.WorkflowStage = status.CurrentStage;
            issue.WorkflowStatus = status.Status;
        }
    }

    private static WorkflowStatusSnapshot? DeserializeWorkflow(string jsonState)
    {
        var state = JsonSerializer.Deserialize<WorkflowGrainState>(jsonState);
        if (state?.Run is null) return null;

        var run = state.Run;
        var currentStageIndex = Math.Clamp(run.CurrentStageIndex, 0, run.Stages.Count - 1);
        var currentStage = run.Stages.Count == 0 ? null : run.Stages[currentStageIndex];
        if (currentStage is null) return null;

        var stages = run.Stages.Select(stage =>
            new StageStatusSnapshot(
                stage.Stage,
                StageStatus(stage),
                stage.Order,
                stage.Tasks.Select(task => new TaskStatusSnapshot(
                    task.DefinitionId,
                    task.Title,
                    task.Uses,
                    task.Status.ToString())).ToList(),
                stage.Checks.Select(check => new CheckStatusSnapshot(
                    check.Name,
                    check.Title,
                    check.Uses,
                    check.Status.ToString(),
                    check.Message)).ToList(),
                stage.Approval is not null
                    ? new ApprovalStatusSnapshot(stage.Approval.Status, stage.Approval.Output?.ToString(), stage.Approval.RequestedAt, stage.Approval.RespondedAt)
                    : null,
                stage.Failure is not null
                    ? new FailureStatusSnapshot(
                        stage.Failure.Reason.ToString(),
                        stage.Failure.Stage,
                        stage.Failure.TaskId,
                        stage.Failure.CheckName,
                        stage.Failure.Message)
                    : null)).ToList();

        var pending = state.LastDispatch is not null && state.Lease is not null
            ? new PendingWorkSnapshot(
                state.LastDispatch.WorkId,
                state.LastDispatch.WorkType,
                state.LastDispatch.Stage,
                state.LastDispatch.Title,
                state.LastDispatch.Uses)
            : null;

        var failure = currentStage.Failure is not null
            ? new FailureStatusSnapshot(
                currentStage.Failure.Reason.ToString(),
                currentStage.Failure.Stage,
                currentStage.Failure.TaskId,
                currentStage.Failure.CheckName,
                currentStage.Failure.Message)
            : null;

        return new WorkflowStatusSnapshot(
            run.Id,
            WorkflowStatus(run, currentStage),
            currentStage.Stage,
            stages,
            pending,
            failure,
            []);
    }

    private static string WorkflowStatus(WorkflowRunSnapshot run, StageRunSnapshot currentStage)
    {
        if (!run.Started) return WorkflowRunStatus.Pending.ToString();
        if (currentStage.Failure is not null) return WorkflowRunStatus.Failed.ToString();
        if (run.Paused) return WorkflowRunStatus.Paused.ToString();
        if (StageStatus(currentStage) == StageRunStatus.AwaitingApproval.ToString()) return WorkflowRunStatus.AwaitingApproval.ToString();
        if (StageStatus(currentStage) == StageRunStatus.Completed.ToString() && currentStage.Order == run.Stages.Max(s => s.Order)) return WorkflowRunStatus.Completed.ToString();
        return WorkflowRunStatus.Running.ToString();
    }

    private static string StageStatus(StageRunSnapshot stage)
    {
        if (stage.Failure is not null) return StageRunStatus.Failed.ToString();
        if (!stage.Started) return StageRunStatus.Pending.ToString();
        if (stage.Approval?.Status == "awaiting") return StageRunStatus.AwaitingApproval.ToString();
        if (StageIsComplete(stage))
        {
            if (stage.RequiresApproval && stage.Approval?.Status != "approved") return StageRunStatus.Running.ToString();
            return StageRunStatus.Completed.ToString();
        }
        return StageRunStatus.Running.ToString();
    }

    private static bool StageIsComplete(StageRunSnapshot stage) =>
        stage.Initialized &&
        stage.Tasks.All(t => t.Status == TaskRunStatus.Completed) &&
        stage.Checks.All(c => c.Status == CheckRunStatus.Passed);

    private static string IssueRuntimeSummary(IssueStage status, IssueAttention? attention) =>
        status switch
        {
            IssueStage.Done => "done",
            IssueStage.Cancelled => "cancelled",
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
                var domain = IssueStateStore.Deserialize(row.JsonState);
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
        Completed = issue.Stage == "done" || issue.RuntimeStatus is "done" or "completed",
        Stage = issue.Stage,
        Status = issue.RuntimeStatus,
    };
}
