using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Services;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Issue.Services;

/// <summary>
/// Cross-service shared collaborator that owns the "load a project's
/// issues and map them to read models" prelude. Both
/// <see cref="IssueQuerier"/> (list / approval-gate paths) and
/// <see cref="IssueMetricsQuerier"/> (quality / approval-wait /
/// stage-duration paths) inject this helper so the load → resolve
/// template + disabled profile ids → map to read model → apply
/// workflow / feedback projection block is defined exactly once. The
/// helper sits outside either querier to keep the two services
/// independent — putting the prelude on either querier would re-couple
/// the split.
/// </summary>
public class IssueReadModelLoader : IScopedService
{
    private readonly IssueWorkflowProfileRegistry _profiles;
    private readonly EffectiveWorkflowProfileResolver _effectiveProfileResolver;
    private readonly ProjectWorkflowProfileManager _projectProfileManager;
    private readonly ILogger<IssueReadModelLoader> _logger;

    public IssueReadModelLoader(
        IssueWorkflowProfileRegistry profiles,
        EffectiveWorkflowProfileResolver effectiveProfileResolver,
        ProjectWorkflowProfileManager projectProfileManager,
        ILogger<IssueReadModelLoader> logger)
    {
        _profiles = profiles;
        _effectiveProfileResolver = effectiveProfileResolver;
        _projectProfileManager = projectProfileManager;
        _logger = logger;
    }

    /// <summary>
    /// Load a project's issue rows, resolve the project's default
    /// template id and disabled workflow profile ids, map each row
    /// through the consolidated <see cref="ToReadModel"/> path, and
    /// apply workflow / feedback projections in one shot. Returned in
    /// the same order as <see cref="IssueRowMapper.ByNumber"/>'s
    /// <paramref name="projectId"/>-filtered set.
    /// </summary>
    public async Task<List<IssueReadModel>> LoadProjectedAsync(
        MohistDbContext db,
        string projectId,
        ProjectInfo? project = null)
    {
        var rows = await db.Issues
            .AsNoTracking()
            .Where(row => row.ProjectId == projectId)
            .ToListAsync();

        if (rows.Count == 0)
        {
            return new List<IssueReadModel>();
        }

        var projectDefaultTemplateId = await LoadProjectDefaultTemplateAsync(db, projectId);
        var disabledIds = await _projectProfileManager.GetDisabledWorkflowProfileIdsAsync(projectId);

        var list = IssueRowMapper.ByNumber(rows, projectId)
            .Select(issue => ToReadModel(BuildInfo(issue, project, _effectiveProfileResolver.Resolve(
                issue.WorkflowProfileId, projectDefaultTemplateId, disabledIds))))
            .ToList();

        ApplyWorkflowProjections(list, await LoadWorkflowStatesAsync(db, list));
        ApplyFeedbackProjections(list, await LoadFeedbackAsync(db, list));

        return list;
    }

    /// <summary>
    /// Apply the same workflow / feedback projections used by
    /// <see cref="LoadProjectedAsync"/> to a single already-built read
    /// model. The single-issue path in <see cref="IssueQuerier"/>
    /// (GetAsync / GetInfoAsync) needs the same projection semantics
    /// without paying the cost of re-loading the entire project's
    /// issues.
    /// </summary>
    public async Task ApplyProjectionsToSingleAsync(MohistDbContext db, IssueReadModel model)
    {
        ApplyWorkflowProjections([model], await LoadWorkflowStatesAsync(db, [model]));
        ApplyFeedbackProjections([model], await LoadFeedbackAsync(db, [model]));
    }

    /// <summary>
    /// Single field-by-field mapping body. Static
    /// <see cref="ToInfo(Domain.Issue, ProjectInfo?)"/> callers hardcode
    /// <see cref="IssueWorkflowProfiles.LocalId"/> to preserve the
    /// no-DI default exactly; the instance path used by
    /// <see cref="LoadProjectedAsync"/> resolves through
    /// <see cref="EffectiveWorkflowProfileResolver"/> so the profile id
    /// agrees with every other read surface.
    /// </summary>
    internal static IssueInfo BuildInfo(Domain.Issue issue, ProjectInfo? project, string? resolvedProfileId)
    {
        var resolution = new IssueRepositoryResolver().Resolve(project, issue.RepositoryRef);
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
            Labels = new Dictionary<string, string>(issue.Labels, StringComparer.Ordinal),
            Priority = issue.Priority,
            Risk = issue.Risk,
            Model = null,
            ModelVariant = null,
            AgentConfig = null,
            StageModels = null,
            StageModelVariants = null,
            StageVariables = null,
            CreatedAt = issue.CreatedAt.ToString("o"),
            UpdatedAt = issue.UpdatedAt.ToString("o"),
            ArchivedAt = issue.ArchivedAt?.ToString("o"),
            CompletedAt = issue.CompletedAt?.ToString("o"),
            WorkflowRunId = issue.WorkflowRunId,
            WorkflowProfileId = resolvedProfileId,
            PrerequisiteNumbers = issue.PrerequisiteNumbers,
            IsDraft = issue.IsDraft,
            Repository = resolution.Repository,
            RepositoryProblem = resolution.Problem,
        };
    }

    public static IssueInfo ToInfo(Domain.Issue issue, ProjectInfo? project = null) =>
        ToInfo(new IssueRepositoryResolver(), issue, project);

    public static IssueInfo ToInfo(IssueRepositoryResolver resolver, Domain.Issue issue, ProjectInfo? project = null) =>
        BuildInfo(issue, project, IssueWorkflowProfiles.LocalId);

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
        ModelVariant = issue.ModelVariant,
        AgentConfig = issue.AgentConfig,
        StageModels = issue.StageModels,
        StageModelVariants = issue.StageModelVariants,
        CreatedAt = issue.CreatedAt,
        UpdatedAt = issue.UpdatedAt,
        ArchivedAt = issue.ArchivedAt,
        CompletedAt = issue.CompletedAt,
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

    private async Task<Dictionary<string, WorkflowStatusView>> LoadWorkflowStatesAsync(
        MohistDbContext db,
        IReadOnlyCollection<IssueReadModel> issues)
    {
        var workflowRunIds = issues
            .Select(i => i.WorkflowRunId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return await LoadWorkflowStatesAsync(db, workflowRunIds);
    }

    private async Task<Dictionary<string, WorkflowStatusView>> LoadWorkflowStatesAsync(
        MohistDbContext db,
        IReadOnlyCollection<string> workflowRunIds)
    {
        var runIds = workflowRunIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (runIds.Length == 0) return [];

        var runRows = await db.WorkflowRuns
            .AsNoTracking()
            .Where(row => runIds.Contains(row.WorkflowRunId))
            .ToListAsync();

        var workflows = new Dictionary<string, WorkflowStatusView>(StringComparer.Ordinal);
        foreach (var row in runRows)
        {
            var run = DeserializeRun(row.WorkflowRunId, row.State);
            if (run is null) continue;
            var snapshot = WorkflowStatusMapper.BuildStatusView(run, definition: null);
            if (snapshot is not null)
                workflows[row.WorkflowRunId] = snapshot;
        }
        return workflows;
    }

    private async Task<Dictionary<string, IReadOnlyList<ApprovalFeedback>>> LoadFeedbackAsync(
        MohistDbContext db,
        IReadOnlyCollection<IssueReadModel> issues)
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
            var run = DeserializeRun(row.WorkflowRunId, row.State);
            if (run is null) continue;
            if (run.Feedback.Count == 0) continue;
            result[row.WorkflowRunId] = run.Feedback;
        }
        return result;
    }

    /// <summary>
    /// Resolve the project's default workflow template id, or null
    /// when the project has no profile row. Exposed so callers needing
    /// only the default-template id (single-issue lookups, stage-order
    /// resolution) can avoid duplicating the SQL.
    /// </summary>
    public async Task<string?> LoadProjectDefaultTemplateAsync(MohistDbContext db, string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return null;
        var row = await db.ProjectWorkflowProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectId == projectId);
        return row?.DefaultTemplateId;
    }

    private void ApplyWorkflowProjections(
        IReadOnlyCollection<IssueReadModel> issues,
        IReadOnlyDictionary<string, WorkflowStatusView> workflows)
    {
        foreach (var issue in issues)
        {
            if (issue.WorkflowRunId is null || !workflows.TryGetValue(issue.WorkflowRunId, out var status)) continue;

            if (string.IsNullOrWhiteSpace(issue.WorkflowProfileId)) continue;

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

    private WorkflowRun? DeserializeRun(string workflowRunId, string json)
    {
        try
        {
            var run = JsonSerializer.Deserialize<WorkflowRun>(WorkflowRunStore.MigrateLegacyWorkflowRunJson(json), JSON.Options);
            if (run is not null) return run;

            _logger.LogError(
                "Cannot project workflow run {WorkflowRunId} into the issue read model: persisted state deserialized to null. The workflow will be omitted from issue projections until repaired.",
                workflowRunId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Cannot project workflow run {WorkflowRunId} into the issue read model: persisted state is invalid. The workflow will be omitted from issue projections until repaired.",
                workflowRunId);
            return null;
        }
    }
}
