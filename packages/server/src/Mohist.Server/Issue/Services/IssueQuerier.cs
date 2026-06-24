using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Epic.Services;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Services;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Issue.Services.Attachments;

namespace Mohist.Server.Issue.Services;

public class IssueQuerier : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IssueWorkflowProfileRegistry _profiles;
    private readonly ProjectQuerier _projects;
    private readonly IssueRepositoryResolver _resolver;
    private readonly ConfigService _configService;

    public IssueQuerier(
        IDbContextFactory<MohistDbContext> dbFactory,
        IssueWorkflowProfileRegistry profiles,
        ProjectQuerier projects,
        IssueRepositoryResolver resolver,
        ConfigService configService)
    {
        _dbFactory = dbFactory;
        _profiles = profiles;
        _projects = projects;
        _resolver = resolver;
        _configService = configService;
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

    public async Task<Domain.Issue?> GetDomainAsync(string projectId, int number)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await LoadIssueAsync(db, projectId, number);
    }

    private static async Task<Domain.Issue?> LoadIssueAsync(MohistDbContext db, string projectId, int number)
    {
        var row = await db.Issues.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ProjectId == projectId && r.Number == number);
        return row is null ? null : IssueStore.Deserialize(row.State);
    }

    public Task<List<IssueReadModel>> ListAsync(
        string projectId,
        ProjectInfo? project = null,
        string? stage = null,
        string? label = null,
        string? priority = null,
        bool? archived = null,
        bool? all = null) =>
        ListWithLabelFiltersAsync(projectId, project, stage, LabelFilterTokens(label), priority, archived, all);

    public async Task<List<IssueReadModel>> ListWithLabelFiltersAsync(
        string projectId,
        ProjectInfo? project,
        string? stage,
        IReadOnlyList<string>? labels,
        string? priority,
        bool? archived,
        bool? all)
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

        if (labels is { Count: > 0 })
        {
            var filters = labels
                .Select(ParseLabelFilter)
                .Where(filter => filter.Key is not null)
                .ToArray();
            if (filters.Length > 0)
            {
                query = query.Where(i => filters.All(filter =>
                    i.Labels.TryGetValue(filter.Key!, out var v)
                    && string.Equals(v, filter.Value, StringComparison.Ordinal)));
            }
        }

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

    /// <summary>
    /// Bucketing granularity for the completion time-series. v1 only supports
    /// fixed by-day and by-week windows; the controller rejects anything else.
    /// </summary>
    public enum CompletionBucket
    {
        Day,
        Week,
    }

    /// <summary>
    /// One bucket in the completion time-series. The <see cref="Boundary"/>
    /// string is the ISO calendar boundary that the bucket represents:
    /// <c>yyyy-MM-dd</c> for day buckets, and the Monday of the ISO week for
    /// week buckets. Both are UTC. Counts are the distinct number of
    /// project-scoped issues that reached the terminal state within the
    /// bucket.
    /// </summary>
    public sealed record CompletionBucketPoint(
        string Boundary,
        int Completed,
        int Failed);

    /// <summary>
    /// The result of <see cref="GetCompletionBucketsAsync"/>. <see cref="Buckets"/>
    /// is dense — every bucket in the fixed trailing window is present, even
    /// when its counts are zero, so callers can render a continuous chart.
    /// </summary>
    public sealed record CompletionBucketsResult(
        string Bucket,
        DateTimeOffset WindowFrom,
        DateTimeOffset WindowTo,
        IReadOnlyList<CompletionBucketPoint> Buckets);

    // CloudEvents reverse-DNS bus types that mark a terminal transition.
    // <c>com.mohist.issue.work-completed</c> → <c>completed</c> (Done).
    // <c>com.mohist.issue.closed</c> → <c>failed</c> (Cancelled).
    internal const string WorkCompletedType = "com.mohist.issue.work-completed";
    internal const string ClosedType = "com.mohist.issue.closed";
    internal const string IssueSourcePrefix = "/mohist/issues/";

    /// <summary>
    /// Buckets the project's terminal-issue transitions (<c>work-completed</c>
    /// and <c>closed</c>) from the durable <c>IssueEvents</c> table by
    /// <c>IssueEvents.Time</c>, not by issue <c>updatedAt</c>. The window
    /// is fixed: <paramref name="bucket"/> = <see cref="CompletionBucket.Day"/>
    /// returns 30 trailing UTC days; <see cref="CompletionBucket.Week"/>
    /// returns 12 trailing ISO weeks (Mon-anchored, UTC). Every bucket in
    /// the window is emitted (zeros included). Issue counts are distinct
    /// per bucket — an issue with multiple terminal events of the same
    /// Type in the same bucket counts once.
    /// </summary>
    public async Task<CompletionBucketsResult> GetCompletionBucketsAsync(
        string projectId,
        CompletionBucket bucket,
        DateTimeOffset now)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        // Resolve the project's issue sources once. IssueEvents has no
        // indexed projectId column, so we constrain on Source in the
        // computed set of "/mohist/issues/{id}" URIs.
        var projectIssueIds = await db.Issues.AsNoTracking()
            .Where(row => row.ProjectId == projectId)
            .Select(row => row.IssueId)
            .ToListAsync();
        var projectSources = projectIssueIds
            .Select(id => IssueSourcePrefix + id)
            .ToList();

        DateTimeOffset windowFrom;
        DateTimeOffset windowTo;
        IReadOnlyList<DateOnly> boundaries;

        if (bucket == CompletionBucket.Day)
        {
            // 30 trailing UTC days inclusive of today.
            var today = DateOnly.FromDateTime(now.UtcDateTime.Date);
            windowFrom = new DateTimeOffset(today.AddDays(-29).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            windowTo = new DateTimeOffset(today.AddDays(1).ToDateTime(new TimeOnly(0, 0)), TimeSpan.Zero);
            boundaries = Enumerable.Range(0, 30)
                .Select(i => today.AddDays(-29 + i))
                .ToList();
        }
        else
        {
            // 12 trailing ISO weeks inclusive of the current week.
            var currentWeek = ISOWeekHelper.StartOfIsoWeek(now.UtcDateTime);
            var firstWeek = currentWeek.AddDays(-7 * 11);
            windowFrom = new DateTimeOffset(firstWeek, TimeSpan.Zero);
            windowTo = new DateTimeOffset(currentWeek.AddDays(7), TimeSpan.Zero);
            boundaries = Enumerable.Range(0, 12)
                .Select(i => DateOnly.FromDateTime(firstWeek.AddDays(7 * i)))
                .ToList();
        }

        var points = Enumerable.Range(0, boundaries.Count)
            .Select(i => new CompletionBucketPoint(
                Boundary: boundaries[i].ToString("yyyy-MM-dd"),
                Completed: 0,
                Failed: 0))
            .ToList();

        if (projectSources.Count == 0)
        {
            return new CompletionBucketsResult(
                Bucket: bucket == CompletionBucket.Day ? "day" : "week",
                WindowFrom: windowFrom,
                WindowTo: windowTo,
                Buckets: points);
        }

        // Pull terminal events inside the bounded window, scoped to this
        // project's issue sources. We then bucket in-memory because the
        // result set is already tightly bounded (≤ 30/12 buckets × small
        // event volume) and the project-id set is already a memory hash.
        // EF Core SQLite cannot translate a `DateTimeOffset` comparison
        // against a TEXT column, so we fetch all events first and
        // filter the time + type + project-source predicates in memory.
        // At v1 volumes (≤ 30/12 buckets per project) the candidate set
        // is small; profiling per design D-OQ2 will tell us whether we
        // need an explicit index on (Type, Time) later.
        var candidates = await db.IssueEvents.AsNoTracking()
            .Select(e => new { e.Source, e.Type, e.Time })
            .ToListAsync();
        var sourceSet = new HashSet<string>(projectSources, StringComparer.Ordinal);
        var terminalTypes = new HashSet<string>(StringComparer.Ordinal) { WorkCompletedType, ClosedType };
        var rows = candidates
            .Where(r => r.Time >= windowFrom
                && r.Time < windowTo
                && sourceSet.Contains(r.Source)
                && terminalTypes.Contains(r.Type))
            .ToList();

        var indexByBoundary = boundaries
            .Select((b, i) => (Boundary: b, Index: i))
            .ToDictionary(t => t.Boundary, t => t.Index);

        // Dedupe per bucket on (Source, Type) so that an issue with
        // multiple same-type terminal events in one bucket counts once,
        // but a flapping issue whose closed→reopened→closed straddles
        // different buckets is counted in each bucket where an event
        // landed.
        var seenPerBucket = new Dictionary<int, HashSet<string>>(points.Count);
        foreach (var row in rows)
        {
            var boundary = bucket == CompletionBucket.Day
                ? DateOnly.FromDateTime(row.Time.UtcDateTime.Date)
                : DateOnly.FromDateTime(ISOWeekHelper.StartOfIsoWeek(row.Time.UtcDateTime));
            if (!indexByBoundary.TryGetValue(boundary, out var idx)) continue;

            if (!seenPerBucket.TryGetValue(idx, out var seen))
            {
                seen = new HashSet<string>(StringComparer.Ordinal);
                seenPerBucket[idx] = seen;
            }
            if (!seen.Add(row.Source + "|" + row.Type)) continue;

            if (row.Type == WorkCompletedType)
            {
                points[idx] = points[idx] with { Completed = points[idx].Completed + 1 };
            }
            else
            {
                points[idx] = points[idx] with { Failed = points[idx].Failed + 1 };
            }
        }

        return new CompletionBucketsResult(
            Bucket: bucket == CompletionBucket.Day ? "day" : "week",
            WindowFrom: windowFrom,
            WindowTo: windowTo,
            Buckets: points);
    }

    /// <summary>
    /// Helpers for the completion aggregation. Internal so the unit tests
    /// can pin ISO-week boundary computation without instantiating the
    /// service.
    /// </summary>
    internal static class ISOWeekHelper
    {
        public static DateTime StartOfIsoWeek(DateTime utc)
        {
            // ISO weeks start on Monday. DayOfWeek: Sunday=0, Monday=1, ....
            var dow = (int)utc.DayOfWeek;
            // Number of days since Monday (treating Monday as 0).
            var daysSinceMonday = (dow + 6) % 7;
            return utc.Date.AddDays(-daysSinceMonday);
        }
    }

    internal static (string? Key, string Value) ParseLabelFilter(string token)
    {
        var idx = token.IndexOf('=');
        if (idx <= 0) return (null, token);
        var key = token[..idx];
        var value = token[(idx + 1)..];
        return (key, value);
    }

    internal static IReadOnlyList<string> LabelFilterTokens(string? label) =>
        string.IsNullOrWhiteSpace(label)
            ? []
            : label.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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
        ModelVariant = issue.ModelVariant,
        AgentConfig = issue.AgentConfig,
        StageModels = issue.StageModels,
        StageModelVariants = issue.StageModelVariants,
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

    private async Task<List<IssueReadModel>> EnrichAsync(MohistDbContext db, List<IssueReadModel> issues)
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

        var commentIds = comments.Select(c => c.Id).ToArray();
        var attachmentRows = await db.Attachments.AsNoTracking()
            .Where(a => a.ProjectId == projectId
                && a.OwnerKind != null
                && a.OwnerId != null
                && ((a.OwnerKind == AttachmentService.OwnerKindIssue && issueIds.Contains(a.OwnerId))
                    || (a.OwnerKind == AttachmentService.OwnerKindComment && commentIds.Contains(a.OwnerId))))
            .ToListAsync();

        var issueAttachments = attachmentRows
            .Where(a => a.OwnerKind == AttachmentService.OwnerKindIssue && a.OwnerId is not null)
            .GroupBy(a => a.OwnerId!)
            .ToDictionary(group => group.Key, group => group.Select(ToAttachmentInfo).ToArray());
        var commentAttachments = attachmentRows
            .Where(a => a.OwnerKind == AttachmentService.OwnerKindComment && a.OwnerId is not null)
            .GroupBy(a => a.OwnerId!)
            .ToDictionary(group => group.Key, group => group.Select(ToAttachmentInfo).ToArray());

        foreach (var issue in issues)
        {
            if (issueAttachments.TryGetValue(issue.Id, out var attachments))
                issue.Attachments = attachments;
        }

        foreach (var group in comments.GroupBy(c => c.IssueNumber))
        {
            if (byNumber.TryGetValue(group.Key, out var issue))
            {
                issue.Comments = group.Select(comment => ToCommentDto(comment, commentAttachments)).ToArray();
            }
        }

        var profileRows = await db.IssueWorkflowProfiles.AsNoTracking()
            .Where(profile => issueIds.Contains(profile.IssueId))
            .ToDictionaryAsync(profile => profile.IssueId, profile => profile.Variables);

        // Resolve the effective agent config for display by merging the live
        // global + project layers with each issue's snapshot (which now holds
        // only built-in context + explicit issue overrides). This keeps the
        // displayed model/agent in sync with project edits; see
        // WorkflowProfileManager.LoadVariablesAsync for the dispatch equivalent.
        var globalBundle = await _configService.GetVariables();
        VariableBundle? projectBundle = null;
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            var projectProfile = await db.ProjectWorkflowProfiles.AsNoTracking()
                .FirstOrDefaultAsync(x => x.ProjectId == projectId);
            projectBundle = VariableBundle.FromJson(projectProfile?.Variables);
        }

        foreach (var issue in issues)
        {
            profileRows.TryGetValue(issue.Id, out var variablesJson);
            ApplyIssueWorkflowVariables(issue, variablesJson, globalBundle, projectBundle);
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

    private async Task<IssueReadModel> EnrichAsync(MohistDbContext db, IssueInfo issue) =>
        (await EnrichAsync(db, [ToReadModel(issue)]))[0];

    private async Task<IssueReadModel> EnrichAsync(MohistDbContext db, IssueReadModel issue) =>
        (await EnrichAsync(db, [issue]))[0];

    public static IssueCommentDto ToCommentDto(IssueCommentRow comment) =>
        ToCommentDto(comment, new Dictionary<string, AttachmentInfo[]>());

    private static IssueCommentDto ToCommentDto(
        IssueCommentRow comment,
        IReadOnlyDictionary<string, AttachmentInfo[]> attachmentsByComment) =>
        new(
            comment.Id,
            comment.IssueId,
            comment.Body,
            comment.CreatedAt.ToString("o"),
            attachmentsByComment.TryGetValue(comment.Id, out var attachments) ? attachments : []);

    private static AttachmentInfo ToAttachmentInfo(AttachmentRow row) => new(
        row.Id,
        row.OriginalFileName,
        string.IsNullOrWhiteSpace(row.ContentType) ? "application/octet-stream" : row.ContentType,
        row.Size);

    private static void ApplyIssueWorkflowVariables(
        IssueReadModel issue,
        string? variablesJson,
        VariableBundle globalBundle,
        VariableBundle? projectBundle)
    {
        var issueBundle = VariableBundle.FromJson(variablesJson);
        var effective = VariableBundle.MergeAll(globalBundle, projectBundle, issueBundle);
        var agentConfig = ReadAgentConfig(effective.Vars);
        issue.AgentConfig = agentConfig;
        issue.Model = ReadAgentModel(agentConfig);
        issue.ModelVariant = ReadAgentVariant(agentConfig, hasModel: !string.IsNullOrWhiteSpace(issue.Model));

        if (effective.Stages is null || effective.Stages.Count == 0)
        {
            issue.StageModels = null;
            issue.StageModelVariants = null;
            return;
        }

        var stageModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var stageModelVariants = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (stage, variables) in effective.Stages)
        {
            var stageAgentConfig = ReadAgentConfig(variables.Vars);
            var model = ReadAgentModel(stageAgentConfig);
            if (!string.IsNullOrWhiteSpace(model))
                stageModels[stage] = model;
            var variant = ReadAgentVariant(stageAgentConfig, hasModel: !string.IsNullOrWhiteSpace(model));
            if (!string.IsNullOrWhiteSpace(variant))
                stageModelVariants[stage] = variant;
        }

        issue.StageModels = stageModels.Count > 0 ? stageModels : null;
        issue.StageModelVariants = stageModelVariants.Count > 0 ? stageModelVariants : null;
    }

    private static Dictionary<string, object?>? ReadAgentConfig(JsonElement? vars)
    {
        if (!vars.HasValue || vars.Value.ValueKind != JsonValueKind.Object)
            return null;
        if (!vars.Value.TryGetProperty("agent", out var agent) || agent.ValueKind != JsonValueKind.Object)
            return null;

        return JsonSerializer.Deserialize<Dictionary<string, object?>>(agent.GetRawText(), JSON.Options);
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

    private static string? ReadAgentVariant(Dictionary<string, object?>? agentConfig, bool hasModel)
    {
        // Variant is bound to its model: if no model, the variant is meaningless
        // and is suppressed from the response, mirroring the clear-on-clear invariant.
        if (!hasModel) return null;
        if (agentConfig is null || !agentConfig.TryGetValue("variant", out var raw) || raw is null)
            return null;
        if (raw is string variant)
            return string.IsNullOrWhiteSpace(variant) ? null : variant;
        if (raw is JsonElement { ValueKind: JsonValueKind.String } element)
            return element.GetString();
        return null;
    }

    private static WorkflowRun? DeserializeRun(string json)
    {
        try { return JsonSerializer.Deserialize<WorkflowRun>(json, JSON.Options); }
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
