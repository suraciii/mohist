using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Epic.Services;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Services.Attachments;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Services;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Issue.Services;

public class IssueQuerier : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly ProjectQuerier _projects;
    private readonly ConfigService _configService;
    private readonly EffectiveWorkflowProfileResolver _effectiveProfileResolver;
    private readonly IWorkflowProfileProvider _profileProvider;
    private readonly IssueReadModelLoader _loader;

    public IssueQuerier(
        IDbContextFactory<MohistDbContext> dbFactory,
        ProjectQuerier projects,
        ConfigService configService,
        EffectiveWorkflowProfileResolver effectiveProfileResolver,
        IWorkflowProfileProvider profileProvider,
        IssueReadModelLoader loader)
    {
        _dbFactory = dbFactory;
        _projects = projects;
        _configService = configService;
        _effectiveProfileResolver = effectiveProfileResolver;
        _profileProvider = profileProvider;
        _loader = loader;
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
        return ToInfo(
            issue,
            project,
            await _loader.LoadProjectDefaultProfileAsync(db, projectId),
            await _profileProvider.GetDisabledProfileIdsAsync(projectId));
    }

    public async Task<Domain.Issue?> GetDomainAsync(string projectId, int number)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await LoadIssueAsync(db, projectId, number);
    }

    public async Task<ParentIssueContext?> GetParentIssueContextAsync(string projectId, int issueNumber)
    {
        if (string.IsNullOrWhiteSpace(projectId) || issueNumber <= 0) return null;

        await using var db = await _dbFactory.CreateDbContextAsync();
        var parentNumber = await db.Issues.AsNoTracking()
            .Where(row => row.ProjectId == projectId && row.Number == issueNumber)
            .Select(row => row.ParentIssueNumber)
            .FirstOrDefaultAsync();
        if (parentNumber is null) return null;

        var parentState = await db.Issues.AsNoTracking()
            .Where(row => row.ProjectId == projectId && row.Number == parentNumber)
            .Select(row => row.State)
            .FirstOrDefaultAsync();
        if (string.IsNullOrEmpty(parentState)) return null;

        var parent = IssueStore.Deserialize(parentState);
        return parent is null ? null : new ParentIssueContext(parent.Title, parent.Body);
    }

    public async Task<IReadOnlyList<IssueParentCandidate>> ListParentCandidatesAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return [];

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Issues.AsNoTracking()
            .Where(row => row.ProjectId == projectId
                && row.Number != null
                && row.Status == "backlog"
                && row.IsArchived != true
                && row.ParentIssueNumber == null)
            .OrderBy(row => row.Number)
            .Select(row => new { row.Number, row.Title, row.State })
            .ToListAsync(cancellationToken);

        return rows
            .Where(row => row.Number is not null)
            .Select(row => string.IsNullOrWhiteSpace(row.State) ? null : IssueStore.Deserialize(row.State))
            .Where(issue => issue is not null
                && issue.Status == IssueStatus.Backlog
                && !issue.HasWorkflowStarted
                && issue.ParentIssueNumber is null)
            .Select(issue => new IssueParentCandidate(issue!.Number, issue.Title))
            .ToList();
    }

    /// <summary>
    /// Reverse lookup: returns the project-scoped issue reference
    /// bound to <paramref name="workflowRunId"/>, or <c>null</c> when no
    /// in-progress issue is bound. Used by
    /// <c>Issue/Subscriptions/IssueWorkflowCompletionHandler</c> to
    /// resolve the owning issue from a <c>com.mohist.workflow.run.completed</c>
    /// CloudEvent (whose payload carries no issue context).
    /// <para>
    /// The query rides the existing indexed <c>IssueRow.WorkflowRunId</c>
    /// computed column plus the <c>Status</c> index, so it is a single
    /// cheap indexed query — no schema change, no new index. Filtering
    /// to <c>Status = 'inProgress'</c> enforces a documented invariant:
    /// a preserved <c>WorkflowRunId</c> on <c>Done</c>/archived issues is
    /// execution history, not a stuck-run signal, so an unfiltered lookup
    /// could match a stale binding. The status filter also makes the
    /// post-<c>Done</c> idempotent path explicit at the handler level
    /// (lookup returns <c>null</c> → no-op) instead of relying solely on
    /// the grain guard.
    /// </para>
    /// </summary>
    public async Task<IssueWorkflowRef?> GetIssueForWorkflowRunAsync(string workflowRunId)
    {
        if (string.IsNullOrWhiteSpace(workflowRunId)) return null;

        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.Issues.AsNoTracking()
            .Where(r => r.WorkflowRunId == workflowRunId && r.Status == "inProgress")
            .Select(r => new { r.ProjectId, r.Number })
            .FirstOrDefaultAsync();
        return row?.ProjectId is { Length: > 0 } && row.Number is > 0
            ? new IssueWorkflowRef(row.ProjectId, row.Number.Value)
            : null;
    }

    /// <summary>
    /// Loads the children snapshot the parent grain needs to drive
    /// composite advancement. Returns each child's <see cref="IssueStatus"/>,
    /// draft state, prerequisite numbers, currently-bound workflow run id,
    /// and target repository name. Filters out archived children (archive
    /// is its own lifecycle, separate from composite advancement) and
    /// orders by issue number for deterministic fan-out. Used by:
    /// <list type="bullet">
    /// <item><c>IssueGrain.StartCompositeAsync</c> — to enumerate startable
    /// children after the parent's aggregate transition.</item>
    /// <item><c>IssueGrain.RecomputeCompositeStatusAsync</c> — to decide
    /// the parent's aggregated status and to fan-out newly-unlocked children.</item>
    /// </list>
    /// <para>
    /// The <see cref="IssueRow.State"/> JSON is deserialized to recover the
    /// domain-level fields (status, is-draft, prerequisites, workflow run
    /// id, repository ref) since the read-model projection strips some
    /// attributes that the parent grain needs for startability checks.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<IssueChildCompositeInfo>> ListChildrenForCompositeAsync(string projectId, int parentNumber)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            return [];

        await using var db = await _dbFactory.CreateDbContextAsync();
        var rows = await db.Issues.AsNoTracking()
            .Where(r => r.ProjectId == projectId
                && r.ParentIssueNumber == parentNumber
                && r.IsArchived != true)
            .OrderBy(r => r.Number)
            .Select(r => new { r.ProjectId, r.Number, r.State })
            .ToListAsync();

        var children = new List<IssueChildCompositeInfo>(rows.Count);
        foreach (var row in rows)
        {
            if (row.Number is null) continue;
            var issue = string.IsNullOrEmpty(row.State) ? null : IssueStore.Deserialize(row.State);
            if (issue is null) continue;
            children.Add(new IssueChildCompositeInfo(
                Number: issue.Number,
                Status: issue.Status,
                IsDraft: issue.IsDraft,
                PrerequisiteNumbers: issue.PrerequisiteNumbers,
                WorkflowRunId: issue.WorkflowRunId,
                RepositoryRef: issue.RepositoryRef,
                IsArchived: issue.ArchivedAt is not null));
        }
        return children;
    }

    /// <summary>
    /// Reverse lookup that returns the human-numbered handle plus the
    /// title of the issue bound to <paramref name="workflowRunId"/>, or
    /// <c>null</c> when no issue row is bound. Used by
    /// <c>GET /api/workflow-runs/{workflowRunId}</c> to
    /// attach an issue ref to the read model without requiring the
    /// caller to know an issue number. The result is intentionally
    /// minimal — number + title only — so the read surface does not
    /// grow into a full <see cref="IssueReadModel"/> companion.
    /// <para>
    /// This lookup is intentionally status-independent: the detail read model is
    /// correlation context for scripts and agents, so completed issues with
    /// preserved run history still need to resolve to their issue handle.
    /// </para>
    /// </summary>
    public async Task<WorkflowRunIssueRef?> GetIssueRefForWorkflowRunAsync(string workflowRunId)
    {
        if (string.IsNullOrWhiteSpace(workflowRunId)) return null;

        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.Issues.AsNoTracking()
            .Where(r => r.WorkflowRunId == workflowRunId)
            .Select(r => new { r.ProjectId, r.Number, r.Title })
            .FirstOrDefaultAsync();
        if (row is null
            || string.IsNullOrWhiteSpace(row.ProjectId)
            || row.Number is null
            || row.Title is null)
            return null;
        return new WorkflowRunIssueRef(row.ProjectId, row.Number.Value, row.Title);
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
        bool? all = null,
        string? repositoryName = null,
        int? parentIssueNumber = null,
        int? epicNumber = null) =>
        ListReadModelsWithLabelFiltersAsync(projectId, project, stage, LabelFilterTokens(label), priority, archived, all, repositoryName, parentIssueNumber, epicNumber);

    public async Task<List<IssueListItem>> ListWithLabelFiltersAsync(
        string projectId,
        ProjectInfo? project,
        string? stage,
        IReadOnlyList<string>? labels,
        string? priority,
        bool? archived,
        bool? all,
        string? repositoryName = null,
        int? parentIssueNumber = null,
        int? epicNumber = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var list = await _loader.LoadListProjectedAsync(db, projectId, project);
        list.Sort((a, b) => a.Number.CompareTo(b.Number));

        var issues = FilterIssueModels(list, stage, labels, priority, archived, all, repositoryName)
            .OrderBy(i => i.Number)
            .ToList();
        await ApplyRelationshipProjectionsAsync(db, issues);

        if (epicNumber is not null)
            issues = issues.Where(i => i.Epic?.Number == epicNumber).ToList();

        if (parentIssueNumber is not null)
            issues = issues.Where(i => i.ParentIssueRef?.Number == parentIssueNumber).ToList();

        return issues.Select(IssueListItem.FromReadModel).ToList();
    }

    public Task<List<IssueReadModel>> ListReadModelsAsync(
        string projectId,
        ProjectInfo? project = null,
        string? stage = null,
        string? label = null,
        string? priority = null,
        bool? archived = null,
        bool? all = null,
        string? repositoryName = null,
        int? parentIssueNumber = null,
        int? epicNumber = null) =>
        ListReadModelsWithLabelFiltersAsync(projectId, project, stage, LabelFilterTokens(label), priority, archived, all, repositoryName, parentIssueNumber, epicNumber);

    public async Task<List<IssueReadModel>> ListReadModelsWithLabelFiltersAsync(
        string projectId,
        ProjectInfo? project,
        string? stage,
        IReadOnlyList<string>? labels,
        string? priority,
        bool? archived,
        bool? all,
        string? repositoryName = null,
        int? parentIssueNumber = null,
        int? epicNumber = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var list = await _loader.LoadProjectedAsync(db, projectId, project);
        list.Sort((a, b) => a.Number.CompareTo(b.Number));

        var issues = FilterIssueModels(list, stage, labels, priority, archived, all, repositoryName)
            .OrderBy(i => i.Number)
            .ToList();
        await EnrichAsync(db, issues);
        if (epicNumber is not null)
            issues = issues.Where(i => i.Epic?.Number == epicNumber).ToList();
        return parentIssueNumber is null
            ? issues
            : issues.Where(i => i.ParentIssueRef?.Number == parentIssueNumber).ToList();
    }

    private static IEnumerable<IssueReadModel> FilterIssueModels(
        IEnumerable<IssueReadModel> list,
        string? stage,
        IReadOnlyList<string>? labels,
        string? priority,
        bool? archived,
        bool? all,
        string? repositoryName)
    {
        var query = list;

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

        if (!string.IsNullOrWhiteSpace(repositoryName))
        {
            var requested = repositoryName.Trim();
            query = query.Where(i => i.RepositoryName is not null
                && string.Equals(i.RepositoryName, requested, StringComparison.OrdinalIgnoreCase));
        }

        return query;
    }

    public async Task<IReadOnlyList<IssueReadModel>> ListInProgressWithApprovalGateAsync(string projectId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var list = await _loader.LoadProjectedAsync(db, projectId, project: null);

        return list
            .Where(IsPausedOnApprovalGate)
            .OrderBy(i => i.Number)
            .ToList();
    }

    private static bool IsPausedOnApprovalGate(IssueReadModel issue) =>
        string.Equals(issue.Status, "in_progress", StringComparison.OrdinalIgnoreCase)
        && string.Equals(issue.WorkflowStatus, "awaiting-approval", StringComparison.OrdinalIgnoreCase);

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
        var projectDefaultProfileId = await _loader.LoadProjectDefaultProfileAsync(db, issue.ProjectId);
        var model = IssueReadModelLoader.ToReadModel(await ToInfoAsync(issue, project, projectDefaultProfileId));
        await _loader.ApplyProjectionsToSingleAsync(db, model);
        return model;
    }

    public async Task<IssueInfo> ToInfoAsync(Domain.Issue issue, ProjectInfo? project, string? projectDefaultProfileId)
    {
        var disabledIds = await _profileProvider.GetDisabledProfileIdsAsync(issue.ProjectId);
        return ToInfo(issue, project, projectDefaultProfileId, disabledIds);
    }

    /// <summary>
    /// Instance projection that uses the centralized effective-profile
    /// resolver. Prefer this over the static overloads in any code path
    /// that has access to the scoped <see cref="IssueQuerier"/> so the
    /// profile id agrees across every read surface.
    /// </summary>
    public IssueInfo ToInfo(Domain.Issue issue, ProjectInfo? project, string? projectDefaultProfileId) =>
        ToInfo(issue, project, projectDefaultProfileId, null);

    public IssueInfo ToInfo(Domain.Issue issue, ProjectInfo? project, string? projectDefaultProfileId, IReadOnlySet<string>? disabledIds)
    {
        var resolved = _effectiveProfileResolver.Resolve(issue.WorkflowProfileId, projectDefaultProfileId, disabledIds);
        return IssueReadModelLoader.BuildInfo(issue, project, resolved);
    }

    private async Task ApplyRelationshipProjectionsAsync(MohistDbContext db, List<IssueReadModel> issues)
    {
        if (issues.Count == 0) return;

        var projectId = issues[0].ProjectId;
        var numbers = issues.Select(i => i.Number).ToArray();
        var byNumber = issues.ToDictionary(i => i.Number);

        var parentLinks = await db.Issues.AsNoTracking()
            .Where(row => row.ProjectId == projectId
                && row.Number != null
                && numbers.Contains(row.Number.Value)
                && row.ParentIssueNumber != null)
            .Join(
                db.Issues.AsNoTracking().Where(row => row.ProjectId == projectId && row.Number != null),
                child => new { child.ProjectId, Number = child.ParentIssueNumber!.Value },
                parent => new { parent.ProjectId, Number = parent.Number!.Value },
                (child, parent) => new { ChildNumber = child.Number!.Value, ParentNumber = parent.Number!.Value, ParentTitle = parent.Title })
            .ToListAsync();
        foreach (var link in parentLinks)
        {
            if (byNumber.TryGetValue(link.ChildNumber, out var issue))
                issue.ParentIssueRef = new IssueParentRef { Number = link.ParentNumber, Title = link.ParentTitle ?? "" };
        }

        await ApplyCompositeChildProjectionAsync(db, issues);

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
                prereqIssues[issue.Number] = IssueReadModelLoader.ToReadModel(IssueReadModelLoader.ToInfo(issue));
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
            issue.Prereq = summaries;
            var summariesByNumber = summaries.ToDictionary(s => s.Number);
            var undelivered = new HashSet<int>(summaries.Where(s => !s.Completed).Select(s => s.Number));
            var hasChildren = issue.ChildIssuesSummary?.HasChildren == true;
            var blocker = ComputeBlockerForReadModel(issue, undelivered, hasChildren);
            issue.Blocker = IssueStartBlockerDto.FromDomain(blocker, summariesByNumber);
            issue.CanStart = blocker is null;
        }

        var issueEpicNumbers = await db.Issues.AsNoTracking()
            .Where(row => row.ProjectId == projectId
                && row.Number != null
                && numbers.Contains(row.Number.Value)
                && row.EpicNumber != null)
            .Select(row => new { IssueNumber = row.Number!.Value, EpicNumber = row.EpicNumber!.Value })
            .ToListAsync();
        if (issueEpicNumbers.Count > 0)
        {
            var epicNumbers = issueEpicNumbers.Select(link => link.EpicNumber).Distinct().ToArray();
            var epics = await db.Epics.AsNoTracking()
                .Where(epic => epic.ProjectId == projectId && epicNumbers.Contains(epic.Number))
                .ToDictionaryAsync(e => e.Number);
            foreach (var link in issueEpicNumbers)
            {
                if (byNumber.TryGetValue(link.IssueNumber, out var issue) && epics.TryGetValue(link.EpicNumber, out var epic))
                {
                    // primaryEpic reflects the issue's NON-TERMINAL
                    // epic membership. An issue may belong to at
                    // most one non-terminal epic, so filtering terminal
                    // owners leaves at most one candidate per issue. The
                    // "last write wins" loop naturally resolves to that
                    // single non-terminal epic; an issue with only terminal
                    // memberships leaves PrimaryEpic null.
                    if (EpicProgress.IsTerminal(epic.Status)) continue;
                    issue.Epic = new IssuePrimaryEpic
                    {
                        Number = epic.Number,
                        Title = epic.Title,
                        Status = epic.Status,
                        Priority = epic.Priority,
                    };
                }
            }
        }

        await ApplyWatchProjectionAsync(db, projectId, numbers, issues);
    }

    private async Task<List<IssueReadModel>> EnrichAsync(MohistDbContext db, List<IssueReadModel> issues)
    {
        if (issues.Count == 0) return issues;

        var projectId = issues[0].ProjectId;
        var numbers = issues.Select(i => i.Number).ToArray();
        var byNumber = issues.ToDictionary(i => i.Number);

        var parentLinks = await db.Issues.AsNoTracking()
            .Where(row => row.ProjectId == projectId
                && row.Number != null
                && numbers.Contains(row.Number.Value)
                && row.ParentIssueNumber != null)
            .Join(
                db.Issues.AsNoTracking().Where(row => row.ProjectId == projectId && row.Number != null),
                child => new { child.ProjectId, Number = child.ParentIssueNumber!.Value },
                parent => new { parent.ProjectId, Number = parent.Number!.Value },
                (child, parent) => new { ChildNumber = child.Number!.Value, ParentNumber = parent.Number!.Value, ParentTitle = parent.Title })
            .ToListAsync();
        foreach (var link in parentLinks)
        {
            if (byNumber.TryGetValue(link.ChildNumber, out var issue))
                issue.ParentIssueRef = new IssueParentRef { Number = link.ParentNumber, Title = link.ParentTitle ?? "" };
        }

        await ApplyCompositeChildProjectionAsync(db, issues);

        var comments = await db.IssueComments.AsNoTracking()
            .Where(c => c.ProjectId == projectId && numbers.Contains(c.IssueNumber))
            .ToListAsync();
        comments = comments.OrderBy(c => c.CreatedAt).ToList();

        var commentIds = comments.Select(c => c.Id).ToArray();
        var attachmentRows = await db.Attachments.AsNoTracking()
            .Where(a => a.ProjectId == projectId
                && a.OwnerKind != null
                && ((a.OwnerKind == AttachmentService.OwnerKindIssue && a.OwnerIssueNumber.HasValue && numbers.Contains(a.OwnerIssueNumber.Value))
                    || (a.OwnerKind == AttachmentService.OwnerKindComment && a.OwnerId != null && commentIds.Contains(a.OwnerId))))
            .ToListAsync();

        var issueAttachments = attachmentRows
            .Where(a => a.OwnerKind == AttachmentService.OwnerKindIssue && a.OwnerIssueNumber.HasValue)
            .GroupBy(a => a.OwnerIssueNumber!.Value)
            .ToDictionary(group => group.Key, group => group.Select(ToAttachmentInfo).ToArray());
        var commentAttachments = attachmentRows
            .Where(a => a.OwnerKind == AttachmentService.OwnerKindComment && a.OwnerId is not null)
            .GroupBy(a => a.OwnerId!)
            .ToDictionary(group => group.Key, group => group.Select(ToAttachmentInfo).ToArray());

        foreach (var issue in issues)
        {
            if (issueAttachments.TryGetValue(issue.Number, out var attachments))
                issue.Attachments = attachments;
        }

        foreach (var group in comments.GroupBy(c => c.IssueNumber))
        {
            if (byNumber.TryGetValue(group.Key, out var issue))
            {
                issue.Comments = group.Select(comment => ToCommentDto(comment, commentAttachments)).ToArray();
            }
        }

        await ApplyWatchProjectionAsync(db, projectId, numbers, issues);

        var profileRows = await db.IssueWorkflowProfiles.AsNoTracking()
            .Where(profile => profile.ProjectId == projectId && numbers.Contains(profile.IssueNumber))
            .ToDictionaryAsync(profile => profile.IssueNumber, profile => profile.Variables);

        // Resolve the effective agent config for display by merging the live
        // global + project layers with each issue's snapshot (which now holds
        // only built-in context + explicit issue overrides). This keeps the
        // displayed model/agent in sync with project edits; see
        // WorkflowVariableResolver provides the dispatch equivalent.
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
            profileRows.TryGetValue(issue.Number, out var variablesJson);
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
                prereqIssues[issue.Number] = IssueReadModelLoader.ToReadModel(IssueReadModelLoader.ToInfo(issue));
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
            issue.Prereq = summaries;
            var summariesByNumber = summaries.ToDictionary(s => s.Number);
            var undelivered = new HashSet<int>(summaries.Where(s => !s.Completed).Select(s => s.Number));
            var hasChildren = await db.Issues.AsNoTracking().AnyAsync(row =>
                row.ProjectId == projectId && row.ParentIssueNumber == issue.Number);
            var blocker = ComputeBlockerForReadModel(issue, undelivered, hasChildren);
            issue.Blocker = IssueStartBlockerDto.FromDomain(blocker, summariesByNumber);
            issue.CanStart = blocker is null;
        }

        var issueEpicNumbers = await db.Issues.AsNoTracking()
            .Where(row => row.ProjectId == projectId
                && row.Number != null
                && numbers.Contains(row.Number.Value)
                && row.EpicNumber != null)
            .Select(row => new { IssueNumber = row.Number!.Value, EpicNumber = row.EpicNumber!.Value })
            .ToListAsync();
        if (issueEpicNumbers.Count > 0)
        {
            var epicNumbers = issueEpicNumbers.Select(link => link.EpicNumber).Distinct().ToArray();
            var epics = await db.Epics.AsNoTracking()
                .Where(epic => epic.ProjectId == projectId && epicNumbers.Contains(epic.Number))
                .ToDictionaryAsync(e => e.Number);
            foreach (var link in issueEpicNumbers)
            {
                if (byNumber.TryGetValue(link.IssueNumber, out var issue) && epics.TryGetValue(link.EpicNumber, out var epic))
                {
                    // primaryEpic reflects the issue's NON-TERMINAL
                    // epic membership. An issue may belong to at
                    // most one non-terminal epic, so filtering terminal
                    // owners leaves at most one candidate per issue. The
                    // "last write wins" loop naturally resolves to that
                    // single non-terminal epic; an issue with only terminal
                    // memberships leaves PrimaryEpic null.
                    if (EpicProgress.IsTerminal(epic.Status)) continue;
                    issue.Epic = new IssuePrimaryEpic
                    {
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
        (await EnrichAsync(db, [IssueReadModelLoader.ToReadModel(issue)]))[0];

    private async Task<IssueReadModel> EnrichAsync(MohistDbContext db, IssueReadModel issue) =>
        (await EnrichAsync(db, [issue]))[0];

    public static IssueCommentDto ToCommentDto(IssueCommentRow comment) =>
        ToCommentDto(comment, new Dictionary<string, AttachmentInfo[]>());

    private static IssueCommentDto ToCommentDto(
        IssueCommentRow comment,
        IReadOnlyDictionary<string, AttachmentInfo[]> attachmentsByComment) =>
        new(
            comment.Id,
            comment.ProjectId,
            comment.IssueNumber,
            comment.Body,
            comment.CreatedAt.ToString("o"),
            attachmentsByComment.TryGetValue(comment.Id, out var attachments) ? attachments : [],
            comment.Author);

    private static AttachmentInfo ToAttachmentInfo(AttachmentRow row) => new(
        row.Id,
        row.OriginalFileName,
        string.IsNullOrWhiteSpace(row.ContentType) ? "application/octet-stream" : row.ContentType,
        row.Size);

    /// <summary>
    /// Batched <c>WatchEntry</c> projection for the per-issue read surface.
    /// Loads every <c>WatchEntryRow</c> scoped to <paramref name="projectId"/>
    /// and the supplied <paramref name="numbers"/>, groups by
    /// <c>IssueNumber</c>, then assigns the two ordered groups to each
    /// issue's <c>Watching</c> / <c>Muted</c> arrays. Issues without
    /// entries receive empty arrays so the field is always present on the
    /// wire (matches every other projected relation convention).
    /// </summary>
    private static async Task ApplyWatchProjectionAsync(
        MohistDbContext db,
        string projectId,
        int[] numbers,
        List<IssueReadModel> issues)
    {
        if (issues.Count == 0) return;

        var watchRows = await db.WatchEntries.AsNoTracking()
            .Where(w => w.ProjectId == projectId && numbers.Contains(w.IssueNumber))
            .ToListAsync();
        var watchingByNumber = watchRows
            .Where(w => w.State == WatchEntryState.Watching)
            .GroupBy(w => w.IssueNumber)
            .ToDictionary(g => g.Key, g => g
                .OrderBy(w => w.CreatedAt)
                .Select(ToWatchEntryDto)
                .ToArray());
        var mutedByNumber = watchRows
            .Where(w => w.State == WatchEntryState.Muted)
            .GroupBy(w => w.IssueNumber)
            .ToDictionary(g => g.Key, g => g
                .OrderBy(w => w.CreatedAt)
                .Select(ToWatchEntryDto)
                .ToArray());
        foreach (var issue in issues)
        {
            issue.Watching = watchingByNumber.TryGetValue(issue.Number, out var entries) ? entries : [];
            issue.Muted = mutedByNumber.TryGetValue(issue.Number, out var muted) ? muted : [];
        }
    }

    private static IssueWatchEntryDto ToWatchEntryDto(WatchEntryRow row) => new(
        row.AgentId,
        row.State,
        row.CreatedAt.ToString("o"),
        row.UpdatedAt.ToString("o"));

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

        return AgentConfigSchema.Filter(
            JsonSerializer.Deserialize<Dictionary<string, object?>>(agent.GetRawText(), JSON.Options));
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

    private static IssueStartBlocker? ComputeBlockerForReadModel(
        IssueReadModel issue,
        IReadOnlySet<int> undeliveredPrerequisites,
        bool hasChildren)
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

    /// <summary>
    /// Composite-child projection. Loads every current
    /// (non-archived) child of every parent present in <paramref name="issues"/>
    /// in one project-scoped query, applies the same workflow projection
    /// the full read model uses so canonical child health reflects the
    /// bound workflow state, and derives both the additive
    /// <see cref="IssueReadModel.Children"/> array and the existing
    /// <see cref="ChildIssuesSummary"/> (now including
    /// <see cref="ChildIssuesSummary.BlockedCount"/>) from the same row
    /// set so the two cannot drift.
    /// </summary>
    private async Task ApplyCompositeChildProjectionAsync(
        MohistDbContext db,
        List<IssueReadModel> issues)
    {
        if (issues.Count == 0) return;

        var projectId = issues[0].ProjectId;
        var parentNumbers = issues.Select(i => i.Number).Distinct().ToArray();

        var childRows = await db.Issues.AsNoTracking()
            .Where(row => row.ProjectId == projectId
                && row.ParentIssueNumber != null
                && parentNumbers.Contains(row.ParentIssueNumber.Value)
                && row.IsArchived != true)
            .OrderBy(row => row.Number)
            .ToListAsync();

        if (childRows.Count == 0)
        {
            foreach (var issue in issues)
                issue.Children = [];
            return;
        }

        var childModels = new List<IssueReadModel>(childRows.Count);
        var projectDefaultProfileId = await _loader.LoadProjectDefaultProfileAsync(db, projectId);
        var disabledIds = await _profileProvider.GetDisabledProfileIdsAsync(projectId);
        foreach (var row in childRows)
        {
            var domain = IssueStore.Deserialize(row.State);
            if (domain is null) continue;
            var resolvedProfileId = _effectiveProfileResolver.Resolve(
                domain.WorkflowProfileId,
                projectDefaultProfileId,
                disabledIds);
            var info = IssueReadModelLoader.BuildInfo(domain, project: null, resolvedProfileId);
            childModels.Add(IssueReadModelLoader.ToReadModel(info));
        }

        await _loader.ApplyWorkflowProjectionsBatchAsync(db, childModels);

        var childrenByParent = new Dictionary<int, List<IssueChildRef>>();
        foreach (var child in childModels)
        {
            var parent = childRows.First(r => r.Number == child.Number).ParentIssueNumber!.Value;
            if (!childrenByParent.TryGetValue(parent, out var list))
            {
                list = [];
                childrenByParent[parent] = list;
            }
            list.Add(IssueChildRef.FromReadModel(child));
        }

        var issueByNumber = issues.ToDictionary(i => i.Number);
        foreach (var issue in issues)
        {
            if (childrenByParent.TryGetValue(issue.Number, out var list))
            {
                issue.Children = list.ToArray();
                issue.ChildIssuesSummary = SummarizeChildren(list);
            }
            else
            {
                issue.Children = [];
                issue.ChildIssuesSummary = null;
            }
        }
    }

    private static ChildIssuesSummary SummarizeChildren(IReadOnlyList<IssueChildRef> children)
    {
        var summary = new ChildIssuesSummary
        {
            HasChildren = children.Count > 0,
            Count = children.Count,
        };
        foreach (var child in children)
        {
            if (string.Equals(child.Status, "backlog", StringComparison.OrdinalIgnoreCase))
                summary.BacklogCount++;
            else if (string.Equals(child.Status, "in_progress", StringComparison.OrdinalIgnoreCase)
                || string.Equals(child.Status, "inProgress", StringComparison.OrdinalIgnoreCase))
                summary.InProgressCount++;
            else if (string.Equals(child.Status, "done", StringComparison.OrdinalIgnoreCase))
                summary.DoneCount++;
            else if (string.Equals(child.Status, "cancelled", StringComparison.OrdinalIgnoreCase))
                summary.CancelledCount++;

            if (string.Equals(child.Health, "blocked", StringComparison.OrdinalIgnoreCase))
                summary.BlockedCount++;
        }
        return summary;
    }

}

public sealed record IssueWorkflowRef(string ProjectId, int Number);

/// <summary>
/// Child-issue snapshot used by composite-advancement decisions on the
/// parent grain. The fields mirror what <c>Issue.LoadIssueSummaryAsync</c>
/// returns for prerequisites, plus the per-child workflow run id and
/// repository ref used by the child <c>StartWorkAsync</c> path. None of
/// these are projections: they are read from the persisted IssueState JSON
/// so the values are transactional with the child's last save.
/// </summary>
[GenerateSerializer]
public sealed record IssueChildCompositeInfo(
    [property: Id(0)] int Number,
    [property: Id(1)] IssueStatus Status,
    [property: Id(2)] bool IsDraft,
    [property: Id(3)] int[] PrerequisiteNumbers,
    [property: Id(4)] string? WorkflowRunId,
    [property: Id(5)] string? RepositoryRef,
    [property: Id(6)] bool IsArchived);
