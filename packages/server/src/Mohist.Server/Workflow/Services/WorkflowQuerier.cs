using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Workflow.Domain;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Artifacts;
using WorkspaceIdentity = Mohist.Server.Workflow.Domain.Run.WorkspaceIdentity;

namespace Mohist.Server.Workflow.Services;

public interface IWorkflowStatusReader
{
    Task<WorkflowStatusView?> GetStatusAsync(string workflowRunId);
}

public class WorkflowQuerier : IScopedService, IWorkflowStatusReader
{
    private readonly IDbContextFactory<MohistDbContext> _db;
    private readonly WorkflowDefinitionResolver _definitionResolver;
    private readonly WorkflowVariableResolver _variableResolver;
    private readonly IWorkflowArtifactQuerier _artifactQuerier;
    private readonly WorkflowRunStatusCache _statusCache;
    private readonly IWorkflowRunDeserializer _runDeserializer;

    public WorkflowQuerier(
        IDbContextFactory<MohistDbContext> db,
        WorkflowDefinitionResolver definitionResolver,
        WorkflowVariableResolver variableResolver,
        IWorkflowArtifactQuerier artifactQuerier,
        WorkflowRunStatusCache statusCache,
        IWorkflowRunDeserializer runDeserializer)
    {
        _db = db;
        _definitionResolver = definitionResolver;
        _variableResolver = variableResolver;
        _artifactQuerier = artifactQuerier;
        _statusCache = statusCache;
        _runDeserializer = runDeserializer;
    }

    public virtual async Task<WorkflowStatusView?> GetStatusAsync(string workflowRunId)
    {
        await using var db = await _db.CreateDbContextAsync();

        var etag = await db.WorkflowRuns.AsNoTracking()
            .Where(e => e.WorkflowRunId == workflowRunId)
            .Select(e => (long?)EF.Property<long>(e, "ETag"))
            .FirstOrDefaultAsync();
        if (etag is null) return null;

        var run = _statusCache.TryGet(workflowRunId, etag.Value, out var cachedRun)
            ? cachedRun
            : await LoadAndCacheAsync(db, workflowRunId, etag.Value);
        if (run is null) return null;

        WorkflowDefinition? definition;
        try
        {
            definition = (await _definitionResolver.LoadTemplateAsync(workflowRunId)).Structure;
        }
        catch (WorkflowDefinitionResolutionException ex)
            when (ex.Reason == WorkflowDefinitionResolutionException.ResolutionReason.NoCurrentDefinition)
        {
            // Historical runs without a durable definition snapshot remain
            // readable, but their status must not be reconstructed from a
            // mutable live profile.
            definition = null;
        }

        var view = WorkflowStatusMapper.BuildStatusView(run, definition);
        if (view is null) return null;

        await AttachArtifactSummariesAsync(view, workflowRunId);

        return view;
    }

    /// <summary>
    /// On-demand actual-binding read (issue #1099). Returns only the
    /// start-time facts the run itself retains — identity, bound Profile,
    /// timing, and the bound semantic definition snapshot. The definition,
    /// when present, comes from the run's own
    /// <see cref="WorkflowRun.BoundWorkflowDefinitionJson"/>; a later
    /// Profile edit never substitutes for it, and no original YAML source
    /// or historical revision is reconstructed.
    /// </summary>
    public virtual async Task<WorkflowRunBindingView?> GetBindingAsync(string workflowRunId)
    {
        await using var db = await _db.CreateDbContextAsync();
        var row = await db.WorkflowRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.WorkflowRunId == workflowRunId);
        if (row is null) return null;

        WorkflowRun? run;
        try
        {
            run = _runDeserializer.Deserialize(row.State);
        }
        catch
        {
            // Schema-incompatible or truncated state deserializes no
            // better than undecodable JSON: the row's retained identity
            // columns are the honest answer.
            run = null;
        }
        if (run is null)
        {
            // The stored state cannot be decoded. The row still carries
            // retained identity columns, so the read reports those facts and
            // marks the definition unavailable rather than failing or
            // guessing from the live Profile cascade.
            return WorkflowRunBindingView.FromUnreadableState(row);
        }

        return new WorkflowRunBindingView(
            run.Id,
            run.Metadata.ProjectId,
            run.Metadata.IssueNumber,
            WorkflowStatusMapper.WireStatus(run.Status),
            run.WorkflowProfileId,
            run.ExplicitWorkflowProfileId,
            run.Metadata.CreatedAt,
            run.StartedAt,
            ReadBoundDefinition(run));
    }

    private static WorkflowRunBindingDefinitionView ReadBoundDefinition(WorkflowRun run)
    {
        if (string.IsNullOrWhiteSpace(run.BoundWorkflowDefinitionJson))
            return WorkflowRunBindingDefinitionView.Unavailable(
                WorkflowRunBindingDefinitionView.ReasonNoSnapshot);

        try
        {
            return new WorkflowRunBindingDefinitionView(
                Available: true,
                Source: WorkflowRunBindingDefinitionView.SourceRunSnapshot,
                Reason: null,
                Content: WorkflowYamlSerializer.FromJson(run.BoundWorkflowDefinitionJson));
        }
        catch (Exception)
        {
            return WorkflowRunBindingDefinitionView.Unavailable(
                WorkflowRunBindingDefinitionView.ReasonUnreadableSnapshot);
        }
    }

    private async Task<WorkflowRun?> LoadAndCacheAsync(
        MohistDbContext db,
        string workflowRunId,
        long etag)
    {
        var row = await db.WorkflowRuns.AsNoTracking()
            .FirstOrDefaultAsync(e => e.WorkflowRunId == workflowRunId);
        if (row is null) return null;

        var run = Hydrate(row, _runDeserializer.Deserialize);
        if (run is not null)
            _statusCache.Store(workflowRunId, etag, run);
        return run;
    }

    private async Task AttachArtifactSummariesAsync(WorkflowStatusView view, string workflowRunId)
    {
        var artifacts = await _artifactQuerier.ListAsync(workflowRunId);
        if (artifacts.Count == 0) return;

        var byWorkflowActionAttempt = artifacts
            .GroupBy(a => a.ActionAttemptId)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        foreach (var stage in view.Stages)
        {
            for (var i = 0; i < stage.Tasks.Count; i++)
            {
                var task = stage.Tasks[i];
                if (!byWorkflowActionAttempt.TryGetValue(task.Id, out var taskArtifacts)) continue;

                var summaries = taskArtifacts
                    .Select(a => new ArtifactSummaryView(
                        a.ArtifactId,
                        a.Path,
                        a.Kind,
                        a.DisplayName,
                        a.RecordedAt,
                        a.Size))
                    .ToList();

                stage.Tasks[i] = new TaskStatusView(
                    task.Id,
                    task.Title,
                    task.Uses,
                    task.Status,
                    task.RequiredFiles,
                    task.Classification,
                    SessionName: task.SessionName,
                    ArtifactSummaries: summaries,
                    StartedAt: task.StartedAt,
                    CompletedAt: task.CompletedAt,
                    DurationMs: task.DurationMs,
                    Output: task.Output,
                    Error: task.Error,
                    AgentJobId: task.AgentJobId,
                    AgentSessionId: task.AgentSessionId);
            }
        }
    }

    public async Task<WorkspaceIdentity?> GetWorkspaceAsync(string workflowRunId)
    {
        await using var db = await _db.CreateDbContextAsync();

        var row = await db.WorkflowRuns.AsNoTracking()
            .FirstOrDefaultAsync(e => e.WorkflowRunId == workflowRunId);
        var run = row is null ? null : Hydrate(row, DeserializeWorkflowRun);
        return run?.Workspace;
    }

    /// <summary>
    /// returns the immutable repository context
    /// the run captured at start time, or <c>null</c> when the run
    /// has none (generic / non-Issue-backed runs) or when the run
    /// state cannot be loaded. The rebase / review / cleanup routes
    /// load this and never recurse into the live Project metadata,
    /// so a terminal Issue whose repository declaration is later
    /// removed can still drive cleanup against its original
    /// snapshot.
    /// </summary>
    public async Task<WorkflowRepositoryContext?> GetRepositoryContextAsync(string workflowRunId)
    {
        await using var db = await _db.CreateDbContextAsync();

        var runJson = await db.WorkflowRuns.AsNoTracking()
            .Where(e => e.WorkflowRunId == workflowRunId)
            .Select(e => e.State)
            .FirstOrDefaultAsync();
        if (runJson is null) return null;

        var run = DeserializeWorkflowRun(runJson);
        return run?.Repository;
    }

    public async Task<JsonElement> GetEffectiveVariablesAsync(string workflowRunId, string? stage = null)
    {
        return await _variableResolver.ResolveEffectiveVariablesAsync(workflowRunId, stage);
    }

    public async Task<JsonElement> GetEffectiveVariableAsync(string workflowRunId, string keyPath, string? stage = null)
    {
        var variables = await GetEffectiveVariablesAsync(workflowRunId, stage);
        return VariableBundle.GetByKeyPath(variables, keyPath);
    }

    public async Task<string?> GetDefinitionYamlAsync(string workflowRunId)
    {
        var definition = (await _definitionResolver.LoadTemplateAsync(workflowRunId)).Structure;
        return definition is null ? null : WorkflowYamlSerializer.ToYaml(definition);
    }

    public async Task<RecoveryDefinition?> GetRecoveryAsync(string workflowRunId, string name)
    {
        var definition = (await _definitionResolver.LoadTemplateAsync(workflowRunId)).Structure;
        if (definition?.Recoveries is null || !definition.Recoveries.TryGetValue(name, out var recovery))
            return null;
        return recovery;
    }

    public async Task<bool> HasIncompleteTaskWithUsesAsync(string workflowRunId, string uses)
    {
        await using var db = await _db.CreateDbContextAsync();
        var row = await db.WorkflowRuns.AsNoTracking()
            .FirstOrDefaultAsync(e => e.WorkflowRunId == workflowRunId);
        var run = row is null ? null : Hydrate(row, DeserializeWorkflowRun);
        return run?.HasIncompleteTaskWithUses(uses) ?? false;
    }

    public async Task<bool> HasIncompleteTaskByIdAsync(string workflowRunId, string id)
    {
        await using var db = await _db.CreateDbContextAsync();
        var row = await db.WorkflowRuns.AsNoTracking()
            .FirstOrDefaultAsync(e => e.WorkflowRunId == workflowRunId);
        var run = row is null ? null : Hydrate(row, DeserializeWorkflowRun);
        return run?.HasIncompleteTaskById(id) ?? false;
    }

    private static WorkflowRun? DeserializeWorkflowRun(string json) =>
        JsonSerializer.Deserialize<WorkflowRun>(json, JSON.Options);

    private static WorkflowRun? Hydrate(
        WorkflowRunRow row,
        Func<string, WorkflowRun?> deserialize)
    {
        var run = deserialize(row.State);
        if (run is not null)
            WorkflowRunLineage.RestoreStoredEpicNumber(run, row.EpicNumber);
        return run;
    }

}

/// <summary>
/// The structured actual-binding read for one WorkflowRun (issue #1099):
/// run/Project/Issue identity, the bound Profile identities retained at
/// start, the retained timing facts, and the bound semantic definition.
/// Every fact comes from the run's own stored state; nothing is resolved
/// against the latest Profile, and no snapshot id or original YAML is
/// invented.
/// </summary>
public sealed record WorkflowRunBindingView(
    string WorkflowRunId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ProjectId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? IssueNumber,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? WorkflowProfileId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ExplicitWorkflowProfileId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? CreatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? StartedAt,
    WorkflowRunBindingDefinitionView Definition)
{
    /// <summary>
    /// Fallback for a run whose stored state cannot be decoded: identity
    /// columns the row still retains survive, while status, Profile, timing,
    /// and the definition are reported unknown rather than guessed.
    /// </summary>
    internal static WorkflowRunBindingView FromUnreadableState(WorkflowRunRow row) => new(
        row.WorkflowRunId,
        row.MetadataProjectId,
        row.IssueNumber,
        Status: null,
        WorkflowProfileId: null,
        ExplicitWorkflowProfileId: null,
        CreatedAt: null,
        StartedAt: null,
        WorkflowRunBindingDefinitionView.Unavailable(
            WorkflowRunBindingDefinitionView.ReasonUnreadableRunState));
}
/// <summary>
/// Availability and content of the definition one run actually bound at
/// start time. When <see cref="Available"/> is true the content is the
/// run's retained semantic snapshot — not the user's original text and not
/// the latest Profile — and the read returns it complete, without
/// truncation. When it is false, <see cref="Reason"/> states the known
/// cause and nothing is substituted.
/// </summary>
public sealed record WorkflowRunBindingDefinitionView(
    bool Available,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Source,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] WorkflowDefinition? Content)
{
    /// <summary>The content belongs to the run's start-time snapshot.</summary>
    public const string SourceRunSnapshot = "run-snapshot";
    public const string ReasonNoSnapshot = "no-snapshot";
    public const string ReasonUnreadableSnapshot = "unreadable-snapshot";
    public const string ReasonUnreadableRunState = "unreadable-run-state";

    public static WorkflowRunBindingDefinitionView Unavailable(string reason) =>
        new(false, Source: null, Reason: reason, Content: null);
}
