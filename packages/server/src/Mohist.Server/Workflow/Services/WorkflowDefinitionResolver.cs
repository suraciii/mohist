using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Workflow.Domain;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Infrastructure.Data.Workflow;

namespace Mohist.Server.Workflow.Services;

/// <summary>
/// Workflow definition resolution entrypoint.
///
/// Definition resolution precedence (highest first):
///   1. WorkflowRun's bound Profile
///   2. Issue's explicit Profile selection
///   3. Project default Profile
///   4. First enabled system Profile
///
/// Variable and prompt resolution are separate responsibilities.
/// </summary>
public class WorkflowDefinitionResolver : IScopedService
{
    internal const string NoEnabledWorkflowProfileMessage =
        "No enabled workflow profile is available. Enable a workflow first.";

    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IWorkflowProfileProvider _profileProvider;

    public WorkflowDefinitionResolver(
        IDbContextFactory<MohistDbContext> dbFactory,
        ConfigService configService,
        IWorkflowProfileProvider profileProvider)
    {
        _dbFactory = dbFactory;
        _profileProvider = profileProvider;
    }

    public async Task<ResolvedTemplate> LoadTemplateAsync(
        string runId,
        string? projectId = null,
        int? issueNumber = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var resolvedContext = await ResolveRunContextAsync(db, runId);
        var context = new RunContext(
            string.IsNullOrWhiteSpace(projectId) ? resolvedContext.ProjectId : projectId,
            issueNumber ?? resolvedContext.IssueNumber,
            resolvedContext.RunExists);

        var boundProfileId = await LoadBoundProfileIdAsync(db, runId);
        if (!string.IsNullOrWhiteSpace(boundProfileId)
            && !string.IsNullOrWhiteSpace(context.ProjectId))
        {
            var boundAgentAction = await LoadBoundAgentActionAsync(db, runId);
            var boundProfile = await LoadProfileAsync(context.ProjectId!, boundProfileId!, boundAgentAction);
            if (boundProfile is null)
                throw new WorkflowDefinitionResolutionException(
                    WorkflowDefinitionResolutionException.ResolutionReason.NoCurrentDefinition,
                    $"Workflow '{runId}' has no current definition for bound Profile '{boundProfileId}'");
            return boundProfile;
        }

        if (string.IsNullOrWhiteSpace(context.ProjectId))
            return ResolvedTemplate.FromProfile(WorkflowProfileCatalog.Profile);

        var issueSelection = await LoadIssueSelectionAsync(db, context);
        var projectDefault = await _profileProvider.GetDefaultProfileIdAsync(context.ProjectId);
        var disabledIds = context.RunExists
            ? null
            : await _profileProvider.GetDisabledProfileIdsAsync(context.ProjectId);

        foreach (var profileId in CandidateProfileIds(issueSelection, projectDefault, disabledIds))
        {
            var profile = await LoadProfileAsync(context.ProjectId, profileId);
            if (profile is not null)
                return profile;
        }

        throw new InvalidOperationException(NoEnabledWorkflowProfileMessage);
    }

    // =======================================================================
    // Narrow APIs — encapsulate the full template selection cascade so the
    // control-plane grain never has to touch a WorkflowDefinition. Each call
    // re-runs LoadTemplateAsync so profile mutations (issue/profile template
    // edits) become visible to subsequent callers. For stage-init callers this
    // is the hot-reload hook; for Create and RequestChanges it costs the same
    // one extra cascade the control plane already paid.
    // =======================================================================

    /// <summary>
    /// Returns the per-stage spec (tasks + checks + lock behavior) for a
    /// single stage. Snapshot-backed runs read from
    /// <c>WorkflowRun.BoundWorkflowDefinitionJson</c> and never from the live
    /// profile provider, so a profile edit after binding cannot change a
    /// run's task definitions, commands, or per-lane timeouts. Pre-snapshot
    /// runs (no <c>BoundWorkflowDefinitionJson</c>) fall back to the live
    /// cascade for backward compatibility; the affected built-in profiles
    /// keep their pre-change aggregate path through
    /// <c>RetainedLegacyAggregate</c>. Throws if neither the snapshot nor
    /// the resolved template contains <paramref name="stageId"/>.
    /// </summary>
    public async Task<StageDefinition> LoadStageSpecsAsync(
        string runId,
        string stageId,
        string? projectId = null,
        int? issueNumber = null,
        string? boundProfileId = null)
    {
        var inMemory = await TryLoadRunAsync(runId);
        var fromMemory = ResolveFromBoundSnapshot(inMemory, stageId);
        if (fromMemory is not null) return fromMemory;

        var template = string.IsNullOrWhiteSpace(boundProfileId)
            ? await LoadTemplateAsync(runId, projectId, issueNumber)
            : await LoadBoundTemplateAsync(runId, boundProfileId!, projectId);
        var definition = template.Structure
            ?? throw new InvalidOperationException(
                $"Workflow '{runId}' has no effective workflow template");
        var resolved = definition.Stages.FirstOrDefault(s => string.Equals(s.Stage, stageId, StringComparison.Ordinal))
            ?? throw new WorkflowDefinitionResolutionException(
                WorkflowDefinitionResolutionException.ResolutionReason.NoStageDefinition,
                $"Workflow '{runId}' has no definition for stage '{stageId}'");
        return resolved;
    }

    private static StageDefinition? ResolveFromBoundSnapshot(WorkflowRun? run, string stageId)
    {
        if (run is null) return null;
        if (string.IsNullOrWhiteSpace(run.BoundWorkflowDefinitionJson)) return null;
        try
        {
            var definition = WorkflowYamlSerializer.FromJson(run.BoundWorkflowDefinitionJson);
            return definition.Stages
                .FirstOrDefault(s => string.Equals(s.Stage, stageId, StringComparison.Ordinal));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Snapshot-only stage resolver used by the Workflow grain's stage
    /// initializer and stage-lock coordinator. Reads the bound definition
    /// from the in-memory run first, falling back to a database load, and
    /// never calls the live profile provider for snapshot-backed runs. A run
    /// without a snapshot resolves its stage from the retained pre-change
    /// aggregate definition for the affected built-in profiles; legacy
    /// aggregate state must not be made to wait for synthesized lane state.
    /// </summary>
    public async Task<StageDefinition> ResolveStageFromBoundSnapshotAsync(
        string runId,
        string stageId,
        WorkflowRun? inMemoryRun)
    {
        var fromMemory = ResolveFromBoundSnapshot(inMemoryRun, stageId);
        if (fromMemory is not null) return fromMemory;

        if (inMemoryRun is not null
            && !string.IsNullOrWhiteSpace(inMemoryRun.WorkflowProfileId)
            && string.IsNullOrWhiteSpace(inMemoryRun.BoundWorkflowDefinitionJson))
        {
            var legacy = RetainedLegacyAggregate.TryGetLegacyDefinition(
                inMemoryRun.WorkflowProfileId,
                stageId);
            if (legacy is not null) return legacy;
        }

        return await LoadStageSpecsAsync(runId, stageId);
    }

    private async Task<WorkflowRun?> TryLoadRunAsync(string runId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.WorkflowRuns.AsNoTracking()
            .FirstOrDefaultAsync(x => x.WorkflowRunId == runId);
        if (row is null) return null;
        try
        {
            return JSON.Deserialize<WorkflowRun>(row.State);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns just the workflow's stage sequence and approval flags — enough
    /// to construct a <see cref="WorkflowRun"/> aggregate without pulling tasks,
    /// checks, or lock configuration across the grain boundary. Used by the
    /// grain's <c>StartAsync</c> path.
    /// </summary>
    public async Task<WorkflowStructure> LoadStructureAsync(
        string runId,
        string? projectId = null,
        int? issueNumber = null)
    {
        var template = await LoadTemplateAsync(runId, projectId, issueNumber);
        var definition = template.Structure
            ?? throw new InvalidOperationException(
                $"Workflow '{runId}' has no effective workflow template");
        if (definition.Stages.Count == 0)
            throw new InvalidOperationException(
                $"Workflow '{runId}' has no stages in its effective template");
        return new WorkflowStructure(
            template.Id ?? throw new InvalidOperationException("Resolved Workflow Profile has no id"),
            definition.Stages.Select(s => new StageStructure(s.Stage, s.RequiresApproval)).ToList());
    }

    public async Task<WorkflowStructure> LoadStartupStructureAsync(
        string runId,
        string? projectId,
        int? issueNumber)
    {
        var template = await LoadTemplateAsync(runId, projectId, issueNumber);
        var definition = template.Structure
            ?? throw new InvalidOperationException($"Workflow '{runId}' has no effective workflow Profile");
        if (definition.Stages.Count == 0)
            throw new InvalidOperationException($"Workflow '{runId}' has no stages in Profile '{template.Id}'");
        return new WorkflowStructure(
            template.Id ?? throw new InvalidOperationException("Resolved Workflow Profile has no id"),
            definition.Stages.Select(s => new StageStructure(s.Stage, s.RequiresApproval)).ToList());
    }

    /// <summary>
    /// Returns the approval configuration (currently the feedback task config)
    /// from the resolved template. Used by the grain's
    /// <c>RequestChangesAsync</c> path.
    /// </summary>
    public async Task<ApprovalConfig?> LoadApprovalConfigAsync(string runId)
    {
        var template = await LoadTemplateAsync(runId);
        return template.Structure?.Approval;
    }

    private static async Task<string?> LoadIssueSelectionAsync(MohistDbContext db, RunContext context)
    {
        if (context.IssueNumber is > 0 && !string.IsNullOrWhiteSpace(context.ProjectId))
        {
            var row = await db.Issues.AsNoTracking()
                .FirstOrDefaultAsync(r => r.ProjectId == context.ProjectId && r.Number == context.IssueNumber);
            return ReadWorkflowProfileId(row?.State);
        }

        return null;
    }

    private static IEnumerable<string> CandidateProfileIds(
        string? issueSelection,
        string? projectDefault,
        IReadOnlySet<string>? disabledIds)
    {
        if (!string.IsNullOrWhiteSpace(issueSelection)
            && !IsDisabledSystemProfile(issueSelection, disabledIds))
        {
            yield return issueSelection;
        }

        if (!string.IsNullOrWhiteSpace(projectDefault)
            && !string.Equals(projectDefault, issueSelection, StringComparison.Ordinal)
            && !IsDisabledSystemProfile(projectDefault, disabledIds))
        {
            yield return projectDefault;
        }

        foreach (var systemProfileId in WorkflowProfileCatalog.SystemProfileIds)
        {
            if (string.Equals(systemProfileId, issueSelection, StringComparison.Ordinal)
                || string.Equals(systemProfileId, projectDefault, StringComparison.Ordinal)
                || IsDisabledSystemProfile(systemProfileId, disabledIds))
            {
                continue;
            }

            yield return systemProfileId;
        }
    }

    private static bool IsDisabledSystemProfile(string profileId, IReadOnlySet<string>? disabledIds) =>
        disabledIds is not null
        && WorkflowProfileCatalog.IsSystemProfile(profileId)
        && disabledIds.Contains(profileId);

    private static async Task<RunContext> ResolveRunContextAsync(MohistDbContext db, string runId)
    {
        var workflowRun = await db.WorkflowRuns.AsNoTracking()
            .FirstOrDefaultAsync(x => x.WorkflowRunId == runId);

        var projectId = workflowRun?.MetadataProjectId;
        var issueNumber = workflowRun?.IssueNumber;
        var issue = await FindIssueForRunAsync(db, runId);
        projectId = string.IsNullOrWhiteSpace(projectId) ? issue?.ProjectId : projectId;
        issueNumber ??= issue?.Number;

        return new RunContext(projectId, issueNumber, workflowRun is not null);
    }

    private static async Task<string?> LoadBoundProfileIdAsync(MohistDbContext db, string runId)
    {
        var state = await db.WorkflowRuns.AsNoTracking()
            .Where(x => x.WorkflowRunId == runId)
            .Select(x => x.State)
            .FirstOrDefaultAsync();
        return ReadWorkflowProfileId(state);
    }

    private async Task<ResolvedTemplate> LoadBoundTemplateAsync(string runId, string profileId, string? projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            return await LoadTemplateAsync(runId, projectId);
        await using var db = await _dbFactory.CreateDbContextAsync();
        var boundAgentAction = await LoadBoundAgentActionAsync(db, runId);
        var profile = await LoadProfileAsync(projectId!, profileId, boundAgentAction);
        if (profile is null)
            throw new WorkflowDefinitionResolutionException(
                WorkflowDefinitionResolutionException.ResolutionReason.NoCurrentDefinition,
                $"Workflow '{runId}' has no current definition for bound Profile '{profileId}'");
        return profile;
    }

    private static string? ReadWorkflowProfileId(string? stateJson)
    {
        if (string.IsNullOrWhiteSpace(stateJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(stateJson);
            return doc.RootElement.TryGetProperty("workflowProfileId", out var value)
                && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> LoadBoundAgentActionAsync(MohistDbContext db, string runId)
    {
        var state = await db.WorkflowRuns.AsNoTracking()
            .Where(x => x.WorkflowRunId == runId)
            .Select(x => x.State)
            .FirstOrDefaultAsync();
        if (string.IsNullOrWhiteSpace(state)) return null;
        try
        {
            using var doc = JsonDocument.Parse(state);
            return doc.RootElement.TryGetProperty("agentAction", out var value)
                && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<IssueRunRef?> FindIssueForRunAsync(MohistDbContext db, string runId)
    {
        var rows = await db.Issues.AsNoTracking()
            .Where(x => x.WorkflowRunId == runId)
            .ToListAsync();

        foreach (var row in rows)
        {
            var issue = TryParseIssueRunRef(row.State, runId);
            if (issue is not null)
                return issue;
        }

        return null;
    }

    private static IssueRunRef? TryParseIssueRunRef(string json, string runId)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            if (!root.TryGetProperty("workflowRunId", out var workflowRunId)
                || workflowRunId.GetString() != runId)
                return null;
            if (!root.TryGetProperty("projectId", out var projectIdEl)
                || string.IsNullOrWhiteSpace(projectIdEl.GetString()))
                return null;
            if (!root.TryGetProperty("number", out var numberEl)
                || !numberEl.TryGetInt32(out var number))
                return null;

            return new IssueRunRef(projectIdEl.GetString()!, number);
        }
        catch
        {
            return null;
        }
    }

    private async Task<ResolvedTemplate?> LoadProfileAsync(
        string projectId,
        string profileId,
        string? boundAgentAction = null)
    {
        var entry = await _profileProvider.GetAsync(projectId, profileId);
        if (entry is null)
            return null;

        var definition = await _profileProvider.GetDefinitionAsync(
            projectId,
            profileId,
            boundAgentAction ?? entry.AgentAction);
        return definition is null
            ? null
            : ResolvedTemplate.FromProfile(new WorkflowProfile(
                entry.ProfileId,
                entry.Name,
                entry.Description,
                definition,
                boundAgentAction ?? entry.AgentAction));
    }


    private sealed record RunContext(string? ProjectId, int? IssueNumber, bool RunExists = false);
    private sealed record IssueRunRef(string ProjectId, int Number);
}
