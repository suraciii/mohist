using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Workflow.Definition;

namespace Mohist.Server.Workflow.Services;

/// <summary>
/// collection-aware WorkflowProfile provider. Built-in
/// Profiles are served in-memory from <see cref="WorkflowProfileCatalog"/>;
/// custom Profiles are persisted to <see cref="WorkflowProfileRecordRow"/>.
/// The provider is the only authority for membership, so the coordinator
/// and the deletion blocker query can call a single
/// <see cref="IWorkflowProfileProvider.ContainsAsync"/> probe instead of
/// branching on catalog vs. table.
/// </summary>
public sealed class WorkflowProfileProvider : IWorkflowProfileProvider, IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IActionCatalogSource _catalogSource;
    private readonly TimeProvider _timeProvider;

    public WorkflowProfileProvider(
        IDbContextFactory<MohistDbContext> dbFactory,
        IActionCatalogSource catalogSource,
        TimeProvider? timeProvider = null)
    {
        _dbFactory = dbFactory;
        _catalogSource = catalogSource;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<WorkflowProfileCollectionEntry>> ListAsync(
        string projectId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("projectId is required", nameof(projectId));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var builtins = WorkflowProfileCatalog.SystemProfileIds
            .Select(profileId => WorkflowProfileCollectionEntry.BuiltIn(profileId, projectId))
            .ToList();
        var customRows = await db.WorkflowProfileRecords.AsNoTracking()
            .Where(r => r.ProjectId == projectId)
            .OrderBy(r => r.ProfileId)
            .ToListAsync(ct);

        var customs = customRows.Select(ToEntry).ToList();

        var combined = new List<WorkflowProfileCollectionEntry>(builtins.Count + customs.Count);
        combined.AddRange(builtins);
        combined.AddRange(customs);
        return combined;
    }

    public async Task<WorkflowProfileCollectionEntry?> GetAsync(
        string projectId, string profileId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(profileId))
            return null;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        if (WorkflowProfileCatalog.IsSystemProfile(profileId))
            return WorkflowProfileCollectionEntry.BuiltIn(profileId, projectId);

        var row = await db.WorkflowProfileRecords.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ProjectId == projectId && r.ProfileId == profileId, ct);
        return row is null ? null : ToEntry(row);
    }

    public async Task<WorkflowDefinition?> GetDefinitionAsync(
        string projectId, string profileId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(profileId))
            return null;

        if (WorkflowProfileCatalog.IsSystemProfile(profileId))
        {
            var source = WorkflowProfileCatalog.GetProfile(profileId);
            if (source is null) return null;
            return source.Definition;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.WorkflowProfileRecords.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ProjectId == projectId && r.ProfileId == profileId, ct);
        if (row is null)
            return null;

        return WorkflowProfileYamlParser.Parse(row.DefinitionSource, profileId).Definition;
    }

    public async Task<string?> GetDefinitionSourceAsync(
        string projectId, string profileId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(profileId))
            return null;

        if (WorkflowProfileCatalog.IsSystemProfile(profileId))
            return WorkflowProfileCatalog.GetDefinitionSource(profileId);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.WorkflowProfileRecords.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ProjectId == projectId && r.ProfileId == profileId, ct);
        return row?.DefinitionSource;
    }

    public async Task<WorkflowProfileSourceProvenance?> GetSourceProvenanceAsync(
        string projectId, string profileId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(profileId))
            return null;

        if (WorkflowProfileCatalog.IsSystemProfile(profileId))
            return WorkflowProfileSourceProvenance.BuiltIn;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.WorkflowProfileRecords.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ProjectId == projectId && r.ProfileId == profileId, ct);
        if (row is null) return null;
        return ParseProvenance(row.SourceProvenance);
    }

    public Task<WorkflowProfileSaveResult> CreateAsync(
        string projectId,
        WorkflowProfileCollectionEntry request,
        CancellationToken ct = default)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));
        return CreateOrUpdateAsync(projectId, request, isUpdate: false, ct);
    }

    /// <summary>
    /// No-write twin of the save validation: the same parse, runtime-Action,
    /// and catalog rules over the same input. It never touches Profile,
    /// selection, Issue, or Run state.
    /// </summary>
    public async Task<WorkflowDefinitionValidationResult> ValidateAsync(
        string definitionSource,
        string? profileId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(definitionSource))
            throw new ArgumentException("Definition source is required", nameof(definitionSource));

        var catalog = await _catalogSource.GetCatalogAsync();
        return EvaluateDefinition(definitionSource, profileId ?? string.Empty, catalog).Validation;
    }

    public Task<WorkflowProfileSaveResult> UpdateAsync(
        string projectId,
        WorkflowProfileCollectionEntry request,
        CancellationToken ct = default)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));
        return CreateOrUpdateAsync(projectId, request, isUpdate: true, ct);
    }

    public async Task<bool> DeleteAsync(
        string projectId, string profileId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(profileId))
            return false;
        if (WorkflowProfileCatalog.IsSystemProfile(profileId))
            throw new WorkflowProfileReadOnlyException(profileId);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.WorkflowProfileRecords
            .FirstOrDefaultAsync(r => r.ProjectId == projectId && r.ProfileId == profileId, ct);
        if (row is null) return false;

        db.WorkflowProfileRecords.Remove(row);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> ContainsAsync(
        string projectId, string profileId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(profileId))
            return false;
        if (WorkflowProfileCatalog.IsSystemProfile(profileId))
            return true;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.WorkflowProfileRecords.AsNoTracking()
            .AnyAsync(r => r.ProjectId == projectId && r.ProfileId == profileId, ct);
    }

    public async Task<string?> GetDefaultProfileIdAsync(
        string projectId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("projectId is required", nameof(projectId));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.ProjectWorkflowProfiles.AsNoTracking()
            .Where(x => x.ProjectId == projectId)
            .Select(x => x.DefaultWorkflowProfileId)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlySet<string>> GetDisabledProfileIdsAsync(
        string projectId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("projectId is required", nameof(projectId));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.ProjectWorkflowProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectId == projectId, ct);
        return row?.DisabledWorkflowProfileIds?.ToHashSet(WorkflowProfileCatalog.IdComparer)
            ?? new HashSet<string>(WorkflowProfileCatalog.IdComparer);
    }

    public async Task SetProfileEnabledAsync(
        string projectId, string profileId, bool enabled, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("projectId is required", nameof(projectId));
        if (string.IsNullOrWhiteSpace(profileId))
            throw new ArgumentException("profileId is required", nameof(profileId));

        var canonicalProfileId = ResolveCanonicalBuiltInId(profileId);
        if (canonicalProfileId is null)
            throw new ArgumentException($"Unknown workflow profile '{profileId}'", nameof(profileId));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.ProjectWorkflowProfiles
            .FirstOrDefaultAsync(x => x.ProjectId == projectId, ct);

        if (row is null)
        {
            if (enabled)
                return;

            if (WorkflowProfileCatalog.SystemProfileIds.Count <= 1)
                throw new InvalidOperationException(
                    $"Cannot disable '{profileId}': at least one workflow profile must remain enabled. " +
                    "Enable a different profile first or leave the current profile enabled.");

            row = new ProjectWorkflowProfile
            {
                ProjectId = projectId,
                Variables = VariableBundle.Empty.ToJson(),
                DisabledWorkflowProfileIds = [canonicalProfileId],
                UpdatedAt = _timeProvider.GetUtcNow(),
            };
            db.ProjectWorkflowProfiles.Add(row);
        }
        else
        {
            var disabled = new HashSet<string>(row.DisabledWorkflowProfileIds, WorkflowProfileCatalog.IdComparer);

            if (enabled)
                disabled.Remove(canonicalProfileId);
            else
                disabled.Add(canonicalProfileId);

            if (!enabled)
            {
                var enabledCount = WorkflowProfileCatalog.SystemProfileIds
                    .Count(id => !disabled.Contains(id));
                if (enabledCount == 0)
                    throw new InvalidOperationException(
                        $"Cannot disable '{profileId}': at least one workflow profile must remain enabled. " +
                        "Enable a different profile first or leave the current profile enabled.");
            }

            row.DisabledWorkflowProfileIds = [..disabled];
            row.UpdatedAt = _timeProvider.GetUtcNow();
        }

        await db.SaveChangesAsync(ct);
    }

    private static string? ResolveCanonicalBuiltInId(string profileId)
    {
        foreach (var systemId in WorkflowProfileCatalog.SystemProfileIds)
        {
            if (WorkflowProfileCatalog.IdComparer.Equals(systemId, profileId))
                return systemId;
        }
        return null;
    }

    private async Task<WorkflowProfileSaveResult> CreateOrUpdateAsync(
        string projectId,
        WorkflowProfileCollectionEntry request,
        bool isUpdate,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("projectId is required", nameof(projectId));
        if (string.IsNullOrWhiteSpace(request.ProfileId))
            throw new ArgumentException("Profile id is required", nameof(request));
        if (string.IsNullOrWhiteSpace(request.DefinitionSource))
            throw new ArgumentException("Definition source is required", nameof(request));
        if (WorkflowProfileCatalog.IsSystemProfile(request.ProfileId))
            throw new WorkflowProfileReadOnlyException(request.ProfileId);

        // Definition and Action rules are shared with ValidateAsync so the
        // same input and catalog context produce the same judgment. A
        // definition that fails them is rejected without any database work.
        var catalog = await _catalogSource.GetCatalogAsync();
        var (profile, validation) = EvaluateDefinition(request.DefinitionSource, request.ProfileId, catalog);
        if (!validation.IsValid)
            return SaveResult(projectId, request, validation);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.WorkflowProfileRecords
            .FirstOrDefaultAsync(r => r.ProjectId == projectId && r.ProfileId == request.ProfileId, ct);
        if (isUpdate && existing is null)
            throw new WorkflowProfileNotFoundException(projectId, request.ProfileId);
        if (!isUpdate && existing is not null)
            throw new WorkflowProfileAlreadyExistsException(projectId, request.ProfileId);

        // Save-only rule: an active WorkflowRun's bound structure must
        // survive the edit. These errors belong to the save, not to the
        // definition, so they extend the same definition-error list the
        // no-write validation reports.
        var activeRuns = isUpdate
            ? await db.WorkflowRuns.AsNoTracking()
                .Where(row => row.MetadataProjectId == projectId
                    && row.WorkflowProfileIdKey == request.ProfileId
                    && row.Status != "completed"
                    && row.Status != "stopped")
                .Select(row => row.State)
                .ToListAsync(ct)
            : [];
        var runBindings = activeRuns
            .Select(state => System.Text.Json.JsonSerializer.Deserialize<WorkflowRun>(state, JSON.Options))
            .Where(run => run is not null)
            .Cast<WorkflowRun>()
            .ToList();

        if (runBindings.Count > 0)
        {
            var updatedStages = profile!.Definition.Stages
                .ToDictionary(stage => stage.Stage, StringComparer.Ordinal);
            var activeRunErrors = new List<WorkflowProfileValidationError>();
            foreach (var run in runBindings)
            {
                foreach (var stage in run.Stages)
                {
                    if (!updatedStages.TryGetValue(stage.Id, out var updatedStage))
                    {
                        activeRunErrors.Add(WorkflowProfileValidationError.From(new ValidationError(
                            "stages",
                            $"Active WorkflowRun '{run.Id}' requires stage '{stage.Id}'")));
                        continue;
                    }

                    if (updatedStage.RequiresApproval != stage.RequiresApproval)
                    {
                        activeRunErrors.Add(WorkflowProfileValidationError.From(new ValidationError(
                            "stages",
                            $"Active WorkflowRun '{run.Id}' requires stage '{stage.Id}' to retain requiresApproval={stage.RequiresApproval.ToString().ToLowerInvariant()}")));
                    }
                }
            }

            if (activeRunErrors.Count > 0)
                return SaveResult(
                    projectId,
                    request,
                    validation with { DefinitionErrors = [.. validation.DefinitionErrors, .. activeRunErrors] });
        }

        var now = _timeProvider.GetUtcNow();
        if (isUpdate)
        {
            existing!.Name = request.Name;
            existing.Description = request.Description;
            existing.DefinitionSource = request.DefinitionSource;
            existing.SourceProvenance = nameof(WorkflowProfileSourceProvenance.Verbatim);
            existing.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
        }
        else
        {
            var row = new WorkflowProfileRecordRow
            {
                ProjectId = projectId,
                ProfileId = request.ProfileId,
                Name = request.Name,
                Description = request.Description,
                DefinitionSource = request.DefinitionSource,
                SourceProvenance = nameof(WorkflowProfileSourceProvenance.Verbatim),
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.WorkflowProfileRecords.Add(row);
            await db.SaveChangesAsync(ct);
        }

        return SaveResult(projectId, request, validation);
    }

    /// <summary>
    /// The one definition/Action evaluation shared by validation and save:
    /// parse (reported, never thrown here), removed-runtime-Action
    /// rejections, and Action-contract checks against the current catalog.
    /// The Action check is Skipped, with its reason, when no Runner has
    /// reported a catalog; a skipped check is not an error, so an otherwise
    /// valid definition may still be saved under existing policy.
    /// </summary>
    private static (WorkflowProfile? Profile, WorkflowDefinitionValidationResult Validation) EvaluateDefinition(
        string definitionSource,
        string profileId,
        ActionCatalog? catalog)
    {
        var (profile, parseErrors) = WorkflowProfileYamlParser.TryParse(definitionSource, profileId);
        var definitionErrors = Sort(parseErrors);
        var actionErrors = profile is null || catalog is null
            ? Array.Empty<WorkflowProfileValidationError>()
            : Sort(ActionContractValidator.Validate(profile.Definition, catalog).Distinct());
        var skipped = catalog is null;
        return (profile, new WorkflowDefinitionValidationResult(
            definitionErrors,
            actionErrors,
            skipped ? ActionValidationStatus.Skipped : ActionValidationStatus.Performed,
            skipped ? WorkflowDefinitionValidationResult.CatalogUnavailableSkipReason : null));

        static WorkflowProfileValidationError[] Sort(IEnumerable<ValidationError> errors) => errors
            .Select(WorkflowProfileValidationError.From)
            .OrderBy(error => error.Path, StringComparer.Ordinal)
            .ThenBy(error => error.Message, StringComparer.Ordinal)
            .ToArray();
    }

    private static WorkflowProfileSaveResult SaveResult(
        string projectId,
        WorkflowProfileCollectionEntry request,
        WorkflowDefinitionValidationResult validation) => new(
        new WorkflowProfileCollectionEntry(
            ProjectId: projectId,
            ProfileId: request.ProfileId,
            Name: request.Name,
            Description: request.Description,
            SourceProvenance: WorkflowProfileSourceProvenance.Verbatim,
            IsBuiltIn: false,
            DefinitionSource: request.DefinitionSource),
        validation);

    private static WorkflowProfileCollectionEntry ToEntry(WorkflowProfileRecordRow row) => new(
        ProjectId: row.ProjectId,
        ProfileId: row.ProfileId,
        Name: row.Name,
        Description: row.Description,
        SourceProvenance: ParseProvenance(row.SourceProvenance),
        IsBuiltIn: false,
        DefinitionSource: row.DefinitionSource);

    private static WorkflowProfileSourceProvenance ParseProvenance(string value) =>
        Enum.TryParse<WorkflowProfileSourceProvenance>(value, ignoreCase: false, out var parsed)
            ? parsed
            : WorkflowProfileSourceProvenance.Verbatim;
}
