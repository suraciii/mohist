using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Workflow.Domain;

namespace Mohist.Server.Workflow.Services;

public class WorkflowRunVariablesStore : IScopedService
{
    /// <summary>
    /// Top-level key seeded as a WorkflowRun initialization default. The
    /// value resolves below explicit Project, Issue, and selected-stage
    /// values; once any explicit write (setVars / PUT / PATCH) targets the
    /// same key the marker is cleared and the explicit value follows the
    /// standard WorkflowRun top-level precedence rules.
    /// </summary>
    public const string ArchiveDefaultKey = "archive";

    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public WorkflowRunVariablesStore(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<VariableBundle> GetVariablesAsync(string workflowRunId)
    {
        var row = await LoadRowAsync(workflowRunId);
        return row is null
            ? VariableBundle.Empty
            : VariableBundle.FromJson(row.Variables);
    }

    public async Task<VariableBundle> GetDefaultVariablesAsync(string workflowRunId)
    {
        var row = await LoadRowAsync(workflowRunId);
        return row is null
            ? VariableBundle.Empty
            : VariableBundle.FromJson(row.DefaultVariables);
    }

    public async Task<VariableBundle> SetVariablesAsync(string workflowRunId, VariableBundle bundle)
    {
        VariableBundleShapeValidator.Validate(bundle);
        return await MutateVariablesAsync(workflowRunId, _ => bundle);
    }

    /// <summary>
    /// Idempotent: ensures the <c>archive</c> default is recorded as an
    /// initialization default for the run when it has never been written
    /// explicitly. A no-op when the run already carries an explicit
    /// <c>archive</c> value or when the default has already been seeded.
    /// Safe to call from the WorkflowRun creation path before work can be
    /// dispatched.
    /// </summary>
    public async Task EnsureArchiveDefaultAsync(string workflowRunId)
    {
        // Fast-path no-op from a detached snapshot. The authoritative
        // decision is re-evaluated against the tracked row below so a
        // concurrent explicit archive write that lands between this read and
        // the save is honored, not overwritten by a restored default marker.
        var snapshot = await LoadRowAsync(workflowRunId);
        if (snapshot is not null)
        {
            if (HasArchiveKey(VariableBundle.FromJson(snapshot.Variables).Vars)) return;
            if (HasArchiveKey(VariableBundle.FromJson(snapshot.DefaultVariables).DefaultVars)) return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.WorkflowRunProfiles
            .FirstOrDefaultAsync(x => x.WorkflowRunId == workflowRunId);

        var currentExplicit = row is null
            ? VariableBundle.Empty
            : VariableBundle.FromJson(row.Variables);
        // An explicit archive write supersedes the initialization default; do
        // not seed (or restore) the marker once it exists.
        if (HasArchiveKey(currentExplicit.Vars))
        {
            return;
        }

        var currentDefaults = row is null
            ? VariableBundle.Empty
            : VariableBundle.FromJson(row.DefaultVariables);
        if (HasArchiveKey(currentDefaults.DefaultVars))
        {
            return;
        }

        var mergedDefaultsJson = VariableBundle.Patch(
            currentDefaults,
            new VariableBundle(DefaultVars: BuildArchiveDefaultElement())).ToJson();

        if (row is null)
        {
            var inserted = new WorkflowRunProfileRow
            {
                WorkflowRunId = workflowRunId,
                Variables = currentExplicit.ToJson(),
                DefaultVariables = mergedDefaultsJson,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.WorkflowRunProfiles.Add(inserted);
            db.Entry(inserted).Property<long>("ETag").CurrentValue = 1;
            await db.SaveChangesAsync();
            return;
        }

        // Only DefaultVariables is assigned, so an interleaved explicit
        // Variables write is neither clobbered nor silently lost; its own
        // ETag bump makes this save raise DbUpdateConcurrencyException.
        row.DefaultVariables = mergedDefaultsJson;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        BumpETag(db.Entry(row));
        await db.SaveChangesAsync();
    }

    public async Task<VariableBundle> PatchVariablesAsync(string workflowRunId, VariableBundle patch)
    {
        VariableBundleShapeValidator.Validate(patch);
        return await MutateVariablesAsync(
            workflowRunId,
            current => VariableBundle.Patch(
                current is null ? VariableBundle.Empty : VariableBundle.FromJson(current.Variables),
                patch));
    }

    /// <summary>
    /// Single tracked read-modify-write for explicit Run variables. The
    /// <paramref name="buildDesiredExplicit"/> strategy runs against the row
    /// read under the same ETag snapshot that is later written, so the desired
    /// explicit bundle and the cleared defaults both derive from the current
    /// row instead of a detached snapshot taken before it. An interleaved
    /// writer either bumps ETag (raising DbUpdateConcurrencyException here) or
    /// is itself rejected by this write's token — the defaults column is
    /// never silently restored to a stale value.
    /// </summary>
    private async Task<VariableBundle> MutateVariablesAsync(
        string workflowRunId,
        Func<WorkflowRunProfileRow?, VariableBundle> buildDesiredExplicit)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.WorkflowRunProfiles
            .FirstOrDefaultAsync(x => x.WorkflowRunId == workflowRunId);

        var desiredExplicit = buildDesiredExplicit(row);
        var currentDefaults = row is null
            ? VariableBundle.Empty
            : VariableBundle.FromJson(row.DefaultVariables);
        var clearedDefaults = currentDefaults.ClearDefaultsCoveredByExplicit(desiredExplicit);

        if (row is null)
        {
            row = new WorkflowRunProfileRow
            {
                WorkflowRunId = workflowRunId,
                Variables = desiredExplicit.ToJson(),
                DefaultVariables = clearedDefaults.ToJson(),
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.WorkflowRunProfiles.Add(row);
            db.Entry(row).Property<long>("ETag").CurrentValue = 1;
        }
        else
        {
            row.Variables = desiredExplicit.ToJson();
            row.DefaultVariables = clearedDefaults.ToJson();
            row.UpdatedAt = DateTimeOffset.UtcNow;
            BumpETag(db.Entry(row));
        }

        await db.SaveChangesAsync();

        return new VariableBundle(
            desiredExplicit.Vars,
            desiredExplicit.Stages,
            clearedDefaults.DefaultVars,
            clearedDefaults.DefaultStages);
    }

    private static void BumpETag(EntityEntry<WorkflowRunProfileRow> entry)
    {
        var etag = entry.Property<long>("ETag");
        etag.CurrentValue = etag.OriginalValue + 1;
    }

    private async Task<WorkflowRunProfileRow?> LoadRowAsync(string workflowRunId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.WorkflowRunProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.WorkflowRunId == workflowRunId);
    }

    private static bool HasArchiveKey(JsonElement? vars)
    {
        if (vars is not { ValueKind: JsonValueKind.Object }) return false;
        return vars.Value.TryGetProperty(ArchiveDefaultKey, out _);
    }

    private static JsonElement BuildArchiveDefaultElement()
    {
        var dict = new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
        {
            [ArchiveDefaultKey] = JsonSerializer.SerializeToElement(string.Empty, VariableBundle.JsonOptions),
        };
        return JsonSerializer.SerializeToElement(dict, VariableBundle.JsonOptions);
    }
}
