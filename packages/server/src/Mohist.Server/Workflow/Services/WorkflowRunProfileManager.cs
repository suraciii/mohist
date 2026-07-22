using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Workflow.Domain;

namespace Mohist.Server.Workflow.Services;

public class WorkflowRunProfileManager : IScopedService
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

    public WorkflowRunProfileManager(IDbContextFactory<MohistDbContext> dbFactory)
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
        var snapshot = await LoadRowAsync(workflowRunId);
        var explicitVars = snapshot is null
            ? VariableBundle.Empty
            : VariableBundle.FromJson(snapshot.Variables);
        if (HasArchiveKey(explicitVars.Vars))
        {
            return;
        }

        var defaults = snapshot is null
            ? VariableBundle.Empty
            : VariableBundle.FromJson(snapshot.DefaultVariables);
        if (HasArchiveKey(defaults.DefaultVars))
        {
            return;
        }

        var seed = new VariableBundle(
            DefaultVars: BuildArchiveDefaultElement());
        var mergedDefaultsJson = VariableBundle.Patch(defaults, seed).ToJson();

        await using var db = await _dbFactory.CreateDbContextAsync();
        // Tracked read so ETag becomes the OriginalValue EF writes into the
        // UPDATE WHERE clause. Combined with only assigning DefaultVariables,
        // an interleaved explicit Variables write is neither clobbered (this
        // column is untouched) nor silently lost (its own save bumps ETag and
        // makes this one raise DbUpdateConcurrencyException).
        var row = await db.WorkflowRunProfiles
            .FirstOrDefaultAsync(x => x.WorkflowRunId == workflowRunId);

        if (row is null)
        {
            var inserted = new WorkflowRunProfileRow
            {
                WorkflowRunId = workflowRunId,
                Variables = explicitVars.ToJson(),
                DefaultVariables = mergedDefaultsJson,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.WorkflowRunProfiles.Add(inserted);
            db.Entry(inserted).Property<long>("ETag").CurrentValue = 1;
            await db.SaveChangesAsync();
            return;
        }

        row.DefaultVariables = mergedDefaultsJson;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        BumpETag(db.Entry(row));
        await db.SaveChangesAsync();
    }

    public async Task<VariableBundle> PatchVariablesAsync(string workflowRunId, VariableBundle patch)
    {
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
