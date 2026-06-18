using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Epic.Services;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Services;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Infrastructure.Data.Workflow;

namespace Mohist.Server.Issue.Services;

public class IssueQuerier
{
    private static readonly JsonSerializerOptions RunJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IssueWorkflowProfileRegistry _profiles;
    private readonly ProjectQuerier _projects;
    private readonly IssueRepositoryResolver _resolver;

    public IssueQuerier(
        IDbContextFactory<MohistDbContext> dbFactory,
        IssueWorkflowProfileRegistry profiles,
        ProjectQuerier projects,
        IssueRepositoryResolver resolver)
    {
        _dbFactory = dbFactory;
        _profiles = profiles;
        _projects = projects;
        _resolver = resolver;
    }

    public async Task<IssueReadModel?> GetAsync(string projectId, int number, ProjectInfo? project = null)
    {
        project ??= await _projects.GetByIdAsync(projectId);
        await using var db = await _dbFactory.CreateDbContextAsync();
        var issue = await LoadIssueAsync(db, projectId, number);
        return issue is null ? null : await EnrichAsync(db, await ToReadModelAsync(db, issue, project));
    }

    public async Task<IssueInfo?> GetInfoAsync(string projectId, int number, ProjectInfo? project = null)
    {
        project ??= await _projects.GetByIdAsync(projectId);
        await using var db = await _dbFactory.CreateDbContextAsync();
        var issue = await LoadIssueAsync(db, projectId, number);
        if (issue is null) return null;
        return ToInfo(issue, project);
    }

    private static async Task<Domain.Issue?> LoadIssueAsync(MohistDbContext db, string projectId, int number)
    {
        var row = await db.Issues.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ProjectId == projectId && r.Number == number);
        return row is null ? null : IssueStore.Deserialize(row.State);
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
        await using var db = await _dbFactory.CreateDbContextAsync(); var rows = await db.Issues
            .AsNoTracking()
            .Where(row => row.ProjectId == projectId)
            .ToListAsync();
        var list = IssueRowMapper.ByNumber(rows, projectId)
            .Select(issue => ToReadModel(ToInfo(issue, project)))
            .OrderBy(i => i.Number)
            .ToList();

        ApplyWorkflowProjections(list, await LoadWorkflowStatesAsync(db, list));
        ApplyFeedbackProjections(list, await LoadFeedbackAsync(db, list));

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

    public async Task<IReadOnlyList<IssueReadModel>> ListInProgressWithApprovalGateAsync(string projectId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var rows = await db.Issues
            .AsNoTracking()
            .Where(row => row.ProjectId == projectId)
            .ToListAsync();
        var list = IssueRowMapper.ByNumber(rows, projectId)
            .Select(issue => ToReadModel(ToInfo(issue)))
            .ToList();
        ApplyWorkflowProjections(list, await LoadWorkflowStatesAsync(db, list));
        ApplyFeedbackProjections(list, await LoadFeedbackAsync(db, list));

        return list
            .Where(IsPausedOnApprovalGate)
            .OrderBy(i => i.Number)
            .ToList();
    }

    private static bool IsPausedOnApprovalGate(IssueReadModel issue) =>
        string.Equals(issue.Status, "in_progress", StringComparison.OrdinalIgnoreCase)
        && string.Equals(issue.WorkflowStatus, "awaiting-approval", StringComparison.OrdinalIgnoreCase);

    private async Task<IssueReadModel> ToReadModelAsync(MohistDbContext db, Domain.Issue issue, ProjectInfo? project = null)
    {
        var model = ToReadModel(ToInfo(issue, project));
        ApplyWorkflowProjections([model], await LoadWorkflowStatesAsync(db, [model]));
        ApplyFeedbackProjections([model], await LoadFeedbackAsync(db, [model]));
        return model;
    }

    public static IssueInfo ToInfo(Domain.Issue issue, ProjectInfo? project = null)
    {
        return ToInfo(new IssueRepositoryResolver(), issue, project);
    }

    public static IssueInfo ToInfo(IssueRepositoryResolver resolver, Domain.Issue issue, ProjectInfo? project = null)
    {
        var resolution = resolver.Resolve(project, issue.RepositoryRef);
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
            Risk = issue.Risk,
            Model = null,
            AgentConfig = null,
            StageModels = null,
            StageVariables = null,
            CreatedAt = issue.CreatedAt.ToString("o"),
            UpdatedAt = issue.UpdatedAt.ToString("o"),
            ArchivedAt = issue.ArchivedAt?.ToString("o"),
            WorkflowRunId = issue.ActiveWorkflowRunId,
            WorkflowProfileId = IssueWorkflowProfiles.DefaultId,
            PrerequisiteNumbers = issue.PrerequisiteNumbers,
            IsDraft = issue.IsDraft,
            Repository = resolution.Repository,
            RepositoryProblem = resolution.Problem,
        };
    }

    internal static RepositoryInfo? ResolveIssueRepository(IssueRepositoryResolver resolver, Domain.Issue issue, ProjectInfo? project)
    {
        return resolver.Resolve(project, issue.RepositoryRef).Repository;
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
        Risk = issue.Risk,
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
        IsDraft = issue.IsDraft,
        CanStart = issue.CanStart,
        Blocker = issue.Blocker,
        Repository = issue.Repository,
        RepositoryProblem = issue.RepositoryProblem,
    };

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

        var workflows = new Dictionary<string, WorkflowStatusView>(StringComparer.Ordinal);
        foreach (var row in runRows)
        {
            var run = DeserializeRun(row.State);
            if (run is null) continue;
            var snapshot = WorkflowStatusMapper.BuildStatusView(run, definition: null);
            if (snapshot is not null)
                workflows[row.WorkflowRunId] = snapshot;
        }
        return workflows;
    }

    private async Task<Dictionary<string, IReadOnlyList<ApprovalFeedback>>> LoadFeedbackAsync(MohistDbContext db, IReadOnlyCollection<IssueReadModel> issues)
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

        var result = new Dictionary<string, IReadOnlyList<ApprovalFeedback>>(StringComparer.Ordinal);
        foreach (var row in runRows)
        {
            var run = DeserializeRun(row.State);
            if (run is null) continue;
            if (run.Feedback.Count == 0) continue;
            result[row.WorkflowRunId] = run.Feedback;
        }
        return result;
    }

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

    private void ApplyFeedbackProjections(
        IReadOnlyCollection<IssueReadModel> issues,
        IReadOnlyDictionary<string, IReadOnlyList<ApprovalFeedback>> feedbackByRun)
    {
        foreach (var issue in issues)
        {
            if (issue.WorkflowRunId is null
                || !feedbackByRun.TryGetValue(issue.WorkflowRunId, out var feedback)
                || feedback.Count == 0)
            {
                issue.Feedback = [];
                continue;
            }

            issue.Feedback = feedback
                .OrderByDescending(f => f.CreatedAt)
                .Select(f => new IssueFeedbackDto(
                    Id: f.Id,
                    IssueNumber: issue.Number,
                    WorkflowRunId: f.WorkflowRunId,
                    Stage: f.Stage,
                    Status: f.Status,
                    Body: f.Body,
                    CreatedAt: f.CreatedAt.ToString("o"),
                    Resolution: f.Status == ApprovalFeedbackStatus.Resolved
                        ? new IssueFeedbackResolutionDto(
                            ResolutionTaskId: f.ResolutionTaskId,
                            ResolvedAt: f.ResolvedAt?.ToString("o"),
                            ResolutionSummary: f.ResolutionSummary)
                        : null))
                .ToArray();
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
        var completed = userTasks.Count(t => t.Status == "completed");
        var running = userTasks.Count(t => t.Status == "running");
        var failed = userTasks.Count(t => t.Status == "failed");

        if (total == 0) return null;

        var currentTaskTitle = userTasks.FirstOrDefault(t => t.Status is "running" or "pending")?.Title;

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
        if (status.Status is "completed" or "failed" or "awaiting-approval" or "paused")
            return true;

        var currentStage = status.Stages.FirstOrDefault(s => s.Stage == status.CurrentStage);
        if (currentStage is null)
            return true;

        return currentStage.ApprovalStatus is { Result: null }
            || (currentStage.Tasks.All(t => t.Status == "completed") && currentStage.Checks.All(c => c.Status == "completed"));
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

        var profileRows = await db.IssueWorkflowProfiles.AsNoTracking()
            .Where(profile => issueIds.Contains(profile.IssueId))
            .ToListAsync();
        foreach (var profile in profileRows)
        {
            if (!byId.TryGetValue(profile.IssueId, out var issue)) continue;
            ApplyIssueWorkflowVariables(issue, profile.Variables);
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
            var rows = await db.Issues.AsNoTracking()
                .Where(row => row.ProjectId == projectId && row.Number != null && missingPrereqNumbers.Contains(row.Number.Value))
                .ToListAsync();
            foreach (var issue in IssueRowMapper.ByNumber(rows, projectId, missingPrereqNumbers).Values)
            {
                prereqIssues[issue.Number] = ToReadModel(ToInfo(issue));
            }
        }
        var prereqGroups = prereqRows.GroupBy(p => p.IssueNumber).ToDictionary(g => g.Key);
        foreach (var issue in issues)
        {
            var summaries = prereqGroups.TryGetValue(issue.Number, out var group)
                ? group
                    .Select(p => prereqIssues.TryGetValue(p.PrerequisiteNumber, out var prereq) ? IssuePrerequisiteSummary.FromReadModel(prereq) : null)
                    .Where(p => p is not null)
                    .Cast<IssuePrerequisiteSummary>()
                    .ToArray()
                : [];
            issue.Prerequisites = summaries;
            var summariesByNumber = summaries.ToDictionary(s => s.Number);
            var undelivered = new HashSet<int>(summaries.Where(s => !s.Completed).Select(s => s.Number));
            var blocker = ComputeBlockerForReadModel(issue, undelivered);
            issue.Blocker = IssueStartBlockerDto.FromDomain(blocker, summariesByNumber);
            issue.CanStart = blocker is null;
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
                        Number = epic.Number,
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

    private static void ApplyIssueWorkflowVariables(IssueReadModel issue, string? variablesJson)
    {
        var bundle = VariableBundle.FromJson(variablesJson);
        var agentConfig = ReadAgentConfig(bundle.Vars);
        issue.AgentConfig = agentConfig;
        issue.Model = ReadAgentModel(agentConfig);

        if (bundle.Stages is null || bundle.Stages.Count == 0)
        {
            issue.StageModels = null;
            return;
        }

        var stageModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (stage, variables) in bundle.Stages)
        {
            var model = ReadAgentModel(ReadAgentConfig(variables.Vars));
            if (!string.IsNullOrWhiteSpace(model))
                stageModels[stage] = model;
        }

        issue.StageModels = stageModels.Count > 0 ? stageModels : null;
    }

    private static Dictionary<string, object?>? ReadAgentConfig(JsonElement? vars)
    {
        if (!vars.HasValue || vars.Value.ValueKind != JsonValueKind.Object)
            return null;
        if (!vars.Value.TryGetProperty("agent", out var agent) || agent.ValueKind != JsonValueKind.Object)
            return null;

        return JsonSerializer.Deserialize<Dictionary<string, object?>>(agent.GetRawText(), RunJsonOptions);
    }

    private static string? ReadAgentModel(Dictionary<string, object?>? agentConfig)
    {
        if (agentConfig is null || !agentConfig.TryGetValue("model", out var raw) || raw is null)
            return null;
        if (raw is string model)
            return string.IsNullOrWhiteSpace(model) ? null : model;
        if (raw is JsonElement { ValueKind: JsonValueKind.String } element)
            return element.GetString();
        return null;
    }

    private static WorkflowRun? DeserializeRun(string json)
    {
        try { return JsonSerializer.Deserialize<WorkflowRun>(json, RunJsonOptions); }
        catch { return null; }
    }

    private static IssueStartBlocker? ComputeBlockerForReadModel(IssueReadModel issue, IReadOnlySet<int> undeliveredPrerequisites)
    {
        if (issue.IsDraft) return new IssueStartBlocker.Draft();
        if (undeliveredPrerequisites.Count == 0) return null;
        foreach (var number in issue.PrerequisiteNumbers)
        {
            if (undeliveredPrerequisites.Contains(number))
                return new IssueStartBlocker.WaitingFor(number);
        }
        return null;
    }

}
