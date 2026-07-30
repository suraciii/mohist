using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Workflow.Services;
using Mohist.Workflow.Definition;

namespace Mohist.Server.Infrastructure.Data.Workflow;

/// <summary>
/// data migration that converts the legacy
/// project-templates / Issue-templates / Issue-default-cascade into the
/// new Project-scoped WorkflowProfile collection. The migration:
///
/// 1. Renders canonical YAML for every legacy custom or inline
///    Definition that was stored only as semantic JSON, and persists
///    the result with <c>canonical-legacy</c> provenance.
/// 2. Renames every legacy custom template whose ID lives in the
///    reserved <c>mohist/*</c> namespace to
///    <c>legacy-reserved/{base64url-utf8(originalProfileId)}</c> and
///    rewrites every Project default, Issue selection, Issue
///    inline-derived reference, and WorkflowRun binding to the new
///    target. The rename is atomic: a target collision fails the
///    whole migration with the Project / source / target triple.
/// 3. Initializes every newly created Project default to
///    <c>mohist/local</c>; Projects that previously resolved to the
///    system fallback also receive that explicit default.
/// 4. Persists WorkflowRun bindings: active custom Runs receive the
///    backing key; terminal custom Runs receive only the public
///    Profile ID with a null backing key.
/// </summary>
public static class WorkflowProfileDataMigrator
{
    public const string ReservedIdPrefix = "legacy-reserved/";
    public const string BuiltInLocalId = "mohist/local";

    public static async Task<WorkflowProfileMigrationResult> MigrateAsync(
        MohistDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<string>();
        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        var countsMutable = new WorkflowProfileMigrationCountsMutable();

        var projectTemplateRows = await db.ProjectWorkflowTemplates
            .ToListAsync(cancellationToken);

        var projectProfileRows = await db.ProjectWorkflowProfiles
            .ToListAsync(cancellationToken);
        var projectRows = await db.Projects.AsNoTracking().ToListAsync(cancellationToken);

        var issueRows = await db.IssueWorkflowProfiles
            .ToListAsync(cancellationToken);
        var inlineSelections = new Dictionary<(string ProjectId, int IssueNumber), string>();

        var issueStateRows = await db.Issues
            .ToListAsync(cancellationToken);

        var workflowRunRows = await db.WorkflowRuns
            .ToListAsync(cancellationToken);

        var existingRecordKeys = await db.WorkflowProfileRecords
            .Select(r => new { r.ProjectId, r.ProfileId, r.SourceProvenance })
            .ToListAsync(cancellationToken);
        var existingRecordSet = new HashSet<(string ProjectId, string ProfileId)>(
            existingRecordKeys.Select(r => (r.ProjectId, r.ProfileId)));
        var migratedRecordSet = new HashSet<(string ProjectId, string ProfileId)>(
            existingRecordKeys
                .Where(r => string.Equals(
                    r.SourceProvenance,
                    nameof(WorkflowProfileSourceProvenance.CanonicalLegacy),
                    StringComparison.Ordinal))
                .Select(r => (r.ProjectId, r.ProfileId)));

        // Detect target-id collisions per project before any write. The
        // (ProjectId, ProfileId) unique key is the failure surface, so a
        // collision is a conflict within the same Project — two different
        // legacy custom Profiles in different Projects resolving to the
        // same target ID is allowed. A legacy target that resolves to an
        // already-present custom Profile it did not itself produce is also
        // a collision: skipping it would silently drop the legacy
        // Definition, so the migration fails atomically with the
        // Project / source / target triple instead.
        var targetOccupancyByProject = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.Ordinal);
        foreach (var row in projectTemplateRows)
        {
            var target = ResolveTargetId(row.TemplateId, renames);
            if (!targetOccupancyByProject.TryGetValue(row.ProjectId, out var perProject))
            {
                perProject = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                targetOccupancyByProject[row.ProjectId] = perProject;
            }
            if (!perProject.TryGetValue(target, out var sources))
            {
                sources = new List<string>();
                perProject[target] = sources;
            }
            sources.Add(row.ProjectId + "/" + row.TemplateId);
        }

        var collisions = targetOccupancyByProject
            .SelectMany(kv => kv.Value, (project, kv) => new { ProjectId = project.Key, kv.Key, kv.Value })
            .Where(entry => entry.Value.Count > 1)
            .ToList();
        if (collisions.Count > 0)
        {
            foreach (var collision in collisions)
            {
                diagnostics.Add(
                    $"Project '{collision.ProjectId}' WorkflowProfile '{collision.Key}' target is occupied by multiple legacy custom Profiles: {string.Join(", ", collision.Value)}");
            }
            throw new InvalidOperationException(
                "WorkflowProfile reserved-ID migration failed with target collisions:\n" + string.Join("\n", diagnostics));
        }

        var externalCollisions = new List<string>();
        foreach (var row in projectTemplateRows)
        {
            var sourceId = row.TemplateId;
            var targetId = ResolveTargetId(sourceId, renames);
            // Only reserved renames (target != source) can collide with an
            // already-present Profile: a non-reserved legacy ID is stable
            // across migration, so an existing record at that ID is the
            // idempotent re-run of a prior migration. A reserved target
            // already occupied by an existing record is a genuine conflict
            // — skipping it would silently drop the legacy Definition.
            if (targetId == sourceId) continue;
            if (!existingRecordSet.Contains((row.ProjectId, targetId))
                || migratedRecordSet.Contains((row.ProjectId, targetId)))
                continue;
            externalCollisions.Add(
                $"Project '{row.ProjectId}' legacy Profile '{sourceId}' target '{targetId}' is already occupied by an existing WorkflowProfile");
        }
        if (externalCollisions.Count > 0)
        {
            throw new InvalidOperationException(
                "WorkflowProfile reserved-ID migration failed: target IDs already occupied by existing Profiles:\n"
                + string.Join("\n", externalCollisions));
        }

        var now = timeProvider.GetUtcNow();

        // 1. Migrate each legacy custom ProjectWorkflowTemplate to a WorkflowProfileRecordRow.
        var projectIdReservedSource = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var row in projectTemplateRows)
        {
            var sourceId = row.TemplateId;
            var targetId = ResolveTargetId(sourceId, renames);
            if (targetId != sourceId)
            {
                renames[sourceId] = targetId;
                if (!projectIdReservedSource.TryGetValue(row.ProjectId, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    projectIdReservedSource[row.ProjectId] = set;
                }
                set.Add(sourceId);
            }

            var profile = ConvertProfileJson(
                row.Template,
                $"Project '{row.ProjectId}' legacy template '{sourceId}'",
                diagnostics);
            if (profile is null)
                continue;

            var yamlSource = WorkflowProfileCanonicalYamlRenderer.Render(profile with { Id = targetId });
            var record = new WorkflowProfileRecordRow
            {
                ProjectId = row.ProjectId,
                ProfileId = targetId,
                Name = profile.Name,
                Description = profile.Description,
                DefinitionSource = yamlSource,
                SourceProvenance = nameof(WorkflowProfileSourceProvenance.CanonicalLegacy),
                CreatedAt = row.CreatedAt == default ? now : row.CreatedAt,
                UpdatedAt = now,
            };
            var exists = await db.WorkflowProfileRecords.AnyAsync(
                r => r.ProjectId == record.ProjectId && r.ProfileId == record.ProfileId,
                cancellationToken);
            if (!exists)
            {
                db.WorkflowProfileRecords.Add(record);
                countsMutable.CustomProfilesMigrated++;
            }
        }

        // 2. Migrate each legacy inline Issue Definition to a custom Profile (re-using the
        //    inline-derived ID; the YAML source is canonical-legacy).
        foreach (var issue in issueRows)
        {
            if (string.IsNullOrWhiteSpace(issue.Template))
                continue;

            var profile = ConvertProfileJson(
                issue.Template,
                $"Project '{issue.ProjectId}' Issue '{issue.IssueNumber}' inline Definition",
                diagnostics);
            if (profile is null)
                continue;

            var inlineId = $"issue-custom:{issue.ProjectId}#{issue.IssueNumber}";
            var targetId = ResolveTargetId(inlineId, renames);
            if (targetId != inlineId)
                renames[inlineId] = targetId;
            issue.SourceTemplateId = targetId;
            issue.Template = null;
            inlineSelections[(issue.ProjectId, issue.IssueNumber)] = targetId;

            if (await db.WorkflowProfileRecords.AsNoTracking()
                .AnyAsync(r => r.ProjectId == issue.ProjectId && r.ProfileId == targetId, cancellationToken))
            {
                continue;
            }

            var yamlSource = WorkflowProfileCanonicalYamlRenderer.Render(profile with { Id = targetId });
            var record = new WorkflowProfileRecordRow
            {
                ProjectId = issue.ProjectId,
                ProfileId = targetId,
                Name = profile.Name,
                Description = profile.Description,
                DefinitionSource = yamlSource,
                SourceProvenance = nameof(WorkflowProfileSourceProvenance.CanonicalLegacy),
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.WorkflowProfileRecords.Add(record);
            countsMutable.InlineIssueProfilesMigrated++;
        }

        if (diagnostics.Count > 0)
        {
            throw new InvalidOperationException(
                "WorkflowProfile Definition migration failed:\n" + string.Join("\n", diagnostics));
        }

        foreach (var issue in issueRows.Where(i => string.IsNullOrWhiteSpace(i.Template)))
        {
            if (string.IsNullOrWhiteSpace(issue.SourceTemplateId))
                continue;
            issue.SourceTemplateId = renames.TryGetValue(issue.SourceTemplateId, out var renamed)
                ? renamed
                : issue.SourceTemplateId;
        }

        foreach (var project in projectRows.Where(p => projectProfileRows.All(r => r.ProjectId != p.Id)))
        {
            db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
            {
                ProjectId = project.Id,
                DefaultTemplateId = BuiltInLocalId,
                DefaultWorkflowProfileId = BuiltInLocalId,
                UpdatedAt = now,
            });
            projectProfileRows.Add(new ProjectWorkflowProfile
            {
                ProjectId = project.Id,
                DefaultTemplateId = BuiltInLocalId,
                DefaultWorkflowProfileId = BuiltInLocalId,
                UpdatedAt = now,
            });
            countsMutable.ProjectDefaultsSeeded++;
        }

        // 3. Rewrite the legacy default only when the new default has not been
        // populated. The legacy column remains unchanged and is therefore not
        // a safe source after the first migration.
        foreach (var profile in projectProfileRows)
        {
            var existing = !string.IsNullOrWhiteSpace(profile.DefaultWorkflowProfileId)
                ? profile.DefaultWorkflowProfileId
                : profile.DefaultTemplateId;
            string? resolvedDefault = null;
            if (!string.IsNullOrWhiteSpace(existing))
            {
                resolvedDefault = renames.TryGetValue(existing, out var renamed) ? renamed : existing;
            }
            else
            {
                resolvedDefault = BuiltInLocalId;
            }

            profile.DefaultWorkflowProfileId = resolvedDefault;
            profile.DefaultWorkflowProfileIdKey = WorkflowProfileCatalog.IsSystemProfile(resolvedDefault)
                ? null
                : resolvedDefault;
            countsMutable.ProjectDefaultsSeeded++;
        }

        // 4. Rewrite Issue WorkflowProfileIdKey from State JSON. The selection
        //    resolves from the canonical state field, then an inline-derived
        //    Profile, then a legacy SourceTemplateId reference (a non-inline
        //    Issue selection of a Project template) so that selection is not
        //    lost on migration.
        var sourceTemplateSelections = issueRows
            .Where(i => string.IsNullOrWhiteSpace(i.Template)
                && !string.IsNullOrWhiteSpace(i.SourceTemplateId))
            .ToDictionary(
                i => (ProjectId: i.ProjectId ?? string.Empty, IssueNumber: i.IssueNumber),
                i => i.SourceTemplateId!,
                EqualityComparer<(string ProjectId, int IssueNumber)>.Default);
        foreach (var issue in issueStateRows)
        {
            var selection = ReadWorkflowProfileIdFromState(issue.State);
            var key = (ProjectId: issue.ProjectId ?? string.Empty, IssueNumber: issue.Number ?? 0);
            if (string.IsNullOrWhiteSpace(selection)
                && inlineSelections.TryGetValue(key, out var inlineSelection))
            {
                selection = inlineSelection;
            }
            if (string.IsNullOrWhiteSpace(selection)
                && sourceTemplateSelections.TryGetValue(key, out var templateSelection))
            {
                selection = templateSelection;
            }
            if (string.IsNullOrWhiteSpace(selection))
                continue;

            var renamed = renames.TryGetValue(selection, out var next) ? next : selection;
            issue.State = RewriteProperty(issue.State, "workflowProfileId", renamed) ?? issue.State;
            issue.WorkflowProfileIdKey = WorkflowProfileCatalog.IsSystemProfile(renamed)
                ? null
                : renamed;
            countsMutable.IssueSelectionsRewritten++;
        }

        // 5. Rewrite the public Run binding in the same state shape in which
        //    it was found. The backing key remains authoritative for the
        //    relationship constraint, but terminal Runs intentionally have
        //    no backing key and still need their public history rewritten.
        foreach (var run in workflowRunRows)
        {
            var binding = ReadWorkflowProfileBinding(run.State);
            var selection = binding.ProfileId ?? run.WorkflowProfileIdKey;
            if (string.IsNullOrWhiteSpace(selection))
                continue;

            var renamed = renames.TryGetValue(selection, out var next) ? next : selection;
            var rewrittenState = run.State;
            if (binding.Location != WorkflowProfileBindingLocation.None
                && !string.Equals(binding.ProfileId, renamed, StringComparison.Ordinal))
            {
                rewrittenState = binding.Location switch
                {
                    WorkflowProfileBindingLocation.Root => RewriteProperty(run.State, "workflowProfileId", renamed) ?? run.State,
                    WorkflowProfileBindingLocation.LegacyAnnotation => RewriteNestedProperty(run.State, "metadata", "annotations", "workflowProfileId", renamed) ?? run.State,
                    _ => run.State,
                };
            }
            var stateChanged = !string.Equals(run.State, rewrittenState, StringComparison.Ordinal);
            if (stateChanged)
                run.State = rewrittenState;

            var status = ReadRunStatus(run.State);
            var isTerminal = status is "completed" or "done" or "stopped";
            var targetKey = isTerminal || WorkflowProfileCatalog.IsSystemProfile(renamed)
                ? null
                : renamed;
            var keyChanged = !string.Equals(run.WorkflowProfileIdKey, targetKey, StringComparison.Ordinal);
            if (keyChanged)
                run.WorkflowProfileIdKey = targetKey;

            if (stateChanged)
            {
                var etag = db.Entry(run).Property<long>("ETag");
                etag.CurrentValue = etag.OriginalValue + 1;
            }

            if (stateChanged || keyChanged)
                countsMutable.WorkflowRunBindingsRewritten++;
        }

        await db.SaveChangesAsync(cancellationToken);

        return new WorkflowProfileMigrationResult(
            countsMutable.ToRecord(),
            Renames: renames.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
            Diagnostics: diagnostics);
    }

    private static string ResolveTargetId(string sourceId, IReadOnlyDictionary<string, string> renames)
    {
        if (renames.TryGetValue(sourceId, out var aliased))
            return aliased;
        if (sourceId.StartsWith("mohist/", StringComparison.Ordinal))
            return ReservedIdPrefix + Base64UrlEncode(sourceId);
        return sourceId;
    }

    private static string Base64UrlEncode(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var b64 = Convert.ToBase64String(bytes);
        return b64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static WorkflowProfile? ConvertProfileJson(
        string? json,
        string identity,
        ICollection<string> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            diagnostics.Add($"{identity} is empty");
            return null;
        }

        try
        {
            var profile = WorkflowProfilePersistence.Deserialize(json);
            if (profile is null)
                diagnostics.Add($"{identity} converted to null");
            return profile;
        }
        catch (Exception ex)
        {
            diagnostics.Add($"{identity} could not be converted: {ex.Message}");
            return null;
        }
    }

    private static string? ReadWorkflowProfileIdFromState(string? stateJson)
    {
        if (string.IsNullOrWhiteSpace(stateJson))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(stateJson);
            if (!doc.RootElement.TryGetProperty("workflowProfileId", out var value))
                return null;
            return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private static WorkflowProfileBinding ReadWorkflowProfileBinding(string? stateJson)
    {
        if (string.IsNullOrWhiteSpace(stateJson))
            return new(null, WorkflowProfileBindingLocation.None);
        try
        {
            using var doc = JsonDocument.Parse(stateJson);
            if (doc.RootElement.TryGetProperty("workflowProfileId", out var rootValue)
                && rootValue.ValueKind == JsonValueKind.String)
            {
                return new(rootValue.GetString(), WorkflowProfileBindingLocation.Root);
            }

            if (doc.RootElement.TryGetProperty("metadata", out var metadata)
                && metadata.TryGetProperty("annotations", out var annotations)
                && annotations.TryGetProperty("workflowProfileId", out var annotationValue)
                && annotationValue.ValueKind == JsonValueKind.String)
            {
                return new(annotationValue.GetString(), WorkflowProfileBindingLocation.LegacyAnnotation);
            }
        }
        catch
        {
            // Invalid legacy state is left for the normal run recovery path.
        }

        return new(null, WorkflowProfileBindingLocation.None);
    }

    private sealed record WorkflowProfileBinding(string? ProfileId, WorkflowProfileBindingLocation Location);

    private enum WorkflowProfileBindingLocation
    {
        None,
        Root,
        LegacyAnnotation,
    }

    private static string? RewriteProperty(string? stateJson, string property, string value)
    {
        if (string.IsNullOrWhiteSpace(stateJson)) return stateJson;
        try
        {
            var root = JsonNode.Parse(stateJson)?.AsObject();
            if (root is null) return stateJson;
            root[property] = value;
            return root.ToJsonString(JSON.Options);
        }
        catch (JsonException)
        {
            return stateJson;
        }
    }

    private static string? RewriteNestedProperty(
        string? stateJson, string first, string second, string property, string value)
    {
        if (string.IsNullOrWhiteSpace(stateJson)) return stateJson;
        try
        {
            var root = JsonNode.Parse(stateJson)?.AsObject();
            var nested = root?[first]?[second]?.AsObject();
            if (nested is null) return stateJson;
            nested[property] = value;
            return root!.ToJsonString(JSON.Options);
        }
        catch (JsonException)
        {
            return stateJson;
        }
    }

    private static string LowercaseOrEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.ToLowerInvariant();

    private static string ReadRunStatus(string? stateJson)
    {
        if (string.IsNullOrWhiteSpace(stateJson))
            return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(stateJson);
            if (doc.RootElement.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String)
            {
                return status.GetString()?.ToLowerInvariant() ?? string.Empty;
            }
        }
        catch
        {
            // fall through
        }
        return string.Empty;
    }
}

public sealed record WorkflowProfileMigrationResult(
    WorkflowProfileMigrationCounts Counts,
    IReadOnlyDictionary<string, string> Renames,
    IReadOnlyList<string> Diagnostics);

public sealed record WorkflowProfileMigrationCounts(
    int CustomProfilesMigrated,
    int InlineIssueProfilesMigrated,
    int ProjectDefaultsSeeded,
    int IssueSelectionsRewritten,
    int WorkflowRunBindingsRewritten);

internal sealed class WorkflowProfileMigrationCountsMutable
{
    public int CustomProfilesMigrated { get; set; }
    public int InlineIssueProfilesMigrated { get; set; }
    public int ProjectDefaultsSeeded { get; set; }
    public int IssueSelectionsRewritten { get; set; }
    public int WorkflowRunBindingsRewritten { get; set; }

    public WorkflowProfileMigrationCounts ToRecord() =>
        new(
            CustomProfilesMigrated,
            InlineIssueProfilesMigrated,
            ProjectDefaultsSeeded,
            IssueSelectionsRewritten,
            WorkflowRunBindingsRewritten);
}
