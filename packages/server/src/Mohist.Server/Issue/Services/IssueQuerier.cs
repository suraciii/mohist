using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Epic.Services;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Config;
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
    private readonly ProjectWorkflowProfileManager _projectProfileManager;
    private readonly IssueReadModelLoader _loader;

    public IssueQuerier(
        IDbContextFactory<MohistDbContext> dbFactory,
        ProjectQuerier projects,
        ConfigService configService,
        EffectiveWorkflowProfileResolver effectiveProfileResolver,
        ProjectWorkflowProfileManager projectProfileManager,
        IssueReadModelLoader loader)
    {
        _dbFactory = dbFactory;
        _projects = projects;
        _configService = configService;
        _effectiveProfileResolver = effectiveProfileResolver;
        _projectProfileManager = projectProfileManager;
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
            await _loader.LoadProjectDefaultTemplateAsync(db, projectId),
            await _projectProfileManager.GetDisabledWorkflowProfileIdsAsync(projectId));
    }

    public async Task<Domain.Issue?> GetDomainAsync(string projectId, int number)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await LoadIssueAsync(db, projectId, number);
    }

    /// <summary>
    /// Reverse lookup: returns the project-scoped issue reference
    /// bound to <paramref name="workflowRunId"/>, or <c>null</c> when no
    /// in-progress issue is bound. Used by
    /// <c>Events/Subscriptions/IssueWorkflowCompletionHandler</c> to
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
    /// Reverse lookup that returns the human-numbered handle plus the
    /// title of the issue bound to <paramref name="workflowRunId"/>, or
    /// <c>null</c> when no issue row is bound. Used by
    /// <c>GET /api/workflow-runs/{workflowRunId}</c> (issue-381 T-002) to
    /// attach an issue ref to the read model without requiring the
    /// caller to know an issue number. The result is intentionally
    /// minimal — number + title only — so the read surface does not
    /// grow into a full <see cref="IssueReadModel"/> companion.
    /// <para>
    /// Unlike <see cref="GetIssueIdForWorkflowRunAsync"/>, this lookup is
    /// intentionally status-independent: the detail read model is
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
            .Select(r => new { r.Number, r.Title })
            .FirstOrDefaultAsync();
        if (row is null || row.Number is null || row.Title is null) return null;
        return new WorkflowRunIssueRef(row.Number.Value, row.Title);
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
        await using var db = await _dbFactory.CreateDbContextAsync();
        var list = await _loader.LoadProjectedAsync(db, projectId, project);
        list.Sort((a, b) => a.Number.CompareTo(b.Number));

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
        var projectDefaultTemplateId = await _loader.LoadProjectDefaultTemplateAsync(db, issue.ProjectId);
        var model = IssueReadModelLoader.ToReadModel(await ToInfoAsync(issue, project, projectDefaultTemplateId));
        await _loader.ApplyProjectionsToSingleAsync(db, model);
        return model;
    }

    public async Task<IssueInfo> ToInfoAsync(Domain.Issue issue, ProjectInfo? project, string? projectDefaultTemplateId)
    {
        var disabledIds = await _projectProfileManager.GetDisabledWorkflowProfileIdsAsync(issue.ProjectId);
        return ToInfo(issue, project, projectDefaultTemplateId, disabledIds);
    }

    /// <summary>
    /// Instance projection that uses the centralized effective-profile
    /// resolver. Prefer this over the static overloads in any code path
    /// that has access to the scoped <see cref="IssueQuerier"/> so the
    /// profile id agrees across every read surface.
    /// </summary>
    public IssueInfo ToInfo(Domain.Issue issue, ProjectInfo? project, string? projectDefaultTemplateId) =>
        ToInfo(issue, project, projectDefaultTemplateId, null);

    public IssueInfo ToInfo(Domain.Issue issue, ProjectInfo? project, string? projectDefaultTemplateId, IReadOnlySet<string>? disabledIds)
    {
        var resolved = _effectiveProfileResolver.Resolve(issue.WorkflowProfileId, projectDefaultTemplateId, disabledIds);
        return IssueReadModelLoader.BuildInfo(issue, project, resolved);
    }

    private async Task<List<IssueReadModel>> EnrichAsync(MohistDbContext db, List<IssueReadModel> issues)
    {
        if (issues.Count == 0) return issues;

        var projectId = issues[0].ProjectId;
        var numbers = issues.Select(i => i.Number).ToArray();
        var byNumber = issues.ToDictionary(i => i.Number);

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

        var profileRows = await db.IssueWorkflowProfiles.AsNoTracking()
            .Where(profile => profile.ProjectId == projectId && numbers.Contains(profile.IssueNumber))
            .ToDictionaryAsync(profile => profile.IssueNumber, profile => profile.Variables);

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
            issue.Prerequisites = summaries;
            var summariesByNumber = summaries.ToDictionary(s => s.Number);
            var undelivered = new HashSet<int>(summaries.Where(s => !s.Completed).Select(s => s.Number));
            var blocker = ComputeBlockerForReadModel(issue, undelivered);
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
                    // Issue-179: primaryEpic reflects the issue's NON-TERMINAL
                    // epic membership. After T-001, an issue may belong to at
                    // most one non-terminal epic, so filtering terminal
                    // owners leaves at most one candidate per issue. The
                    // "last write wins" loop naturally resolves to that
                    // single non-terminal epic; an issue with only terminal
                    // memberships leaves PrimaryEpic null.
                    if (EpicProgress.IsTerminal(epic.Status)) continue;
                    issue.PrimaryEpic = new IssuePrimaryEpic
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

public sealed record IssueWorkflowRef(string ProjectId, int Number);
