using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Workflow.Domain;
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
        return CreateOrUpdateAsync(projectId, request, isUpdate: false, expectedRevision: null, ct);
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
        string expectedRevision,
        CancellationToken ct = default)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(expectedRevision))
            throw new ArgumentException(
                "An update requires the revision read with the content it replaces", nameof(expectedRevision));
        return CreateOrUpdateAsync(projectId, request, isUpdate: true, expectedRevision, ct);
    }

    /// <summary>
    /// Coherent single-version read behind the detail API: one row query
    /// supplies content, revision, and the source the definition is parsed
    /// from, so no save can interleave between the facts.
    /// </summary>
    public async Task<WorkflowProfileDetail?> GetDetailAsync(
        string projectId, string profileId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(profileId))
            return null;

        if (WorkflowProfileCatalog.IsSystemProfile(profileId))
        {
            var source = WorkflowProfileCatalog.GetProfile(profileId);
            if (source is null) return null;
            var entry = WorkflowProfileCollectionEntry.BuiltIn(profileId, projectId);
            return new WorkflowProfileDetail(entry, source.Definition);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.WorkflowProfileRecords.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ProjectId == projectId && r.ProfileId == profileId, ct);
        if (row is null)
            return null;

        return new WorkflowProfileDetail(
            ToEntry(row),
            WorkflowProfileYamlParser.Parse(row.DefinitionSource, profileId).Definition);
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
        string? expectedRevision,
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
        var (_, validation) = EvaluateDefinition(request.DefinitionSource, request.ProfileId, catalog);
        if (!validation.IsValid)
            return SaveResult(projectId, request, validation);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var now = _timeProvider.GetUtcNow();
        if (isUpdate)
        {
            // Authoritative compare-and-set: the caller-read revision gates
            // the single UPDATE statement, so a stale or missing
            // precondition fails without any partial effect, whatever the
            // API or coordinator checked earlier. Active runs are
            // unaffected by design — they execute their own binding
            // snapshot, never this row.
            var newRevision = NewRevision();
            var affected = await db.WorkflowProfileRecords
                .Where(r => r.ProjectId == projectId
                    && r.ProfileId == request.ProfileId
                    && r.Revision == expectedRevision)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(r => r.Name, request.Name)
                    .SetProperty(r => r.Description, request.Description)
                    .SetProperty(r => r.DefinitionSource, request.DefinitionSource)
                    .SetProperty(r => r.SourceProvenance, nameof(WorkflowProfileSourceProvenance.Verbatim))
                    .SetProperty(r => r.Revision, newRevision)
                    .SetProperty(r => r.UpdatedAt, now), ct);
            if (affected == 0)
            {
                var current = await db.WorkflowProfileRecords.AsNoTracking()
                    .Where(r => r.ProjectId == projectId && r.ProfileId == request.ProfileId)
                    .Select(r => new { r.Revision })
                    .FirstOrDefaultAsync(ct);
                if (current is null)
                    throw new WorkflowProfileNotFoundException(projectId, request.ProfileId);
                throw new WorkflowProfileRevisionConflictException(
                    projectId, request.ProfileId, expectedRevision!, current.Revision);
            }

            return SaveResult(projectId, request, validation, newRevision);
        }

        var exists = await db.WorkflowProfileRecords.AsNoTracking()
            .AnyAsync(r => r.ProjectId == projectId && r.ProfileId == request.ProfileId, ct);
        if (exists)
            throw new WorkflowProfileAlreadyExistsException(projectId, request.ProfileId);

        var createdRevision = NewRevision();
        db.WorkflowProfileRecords.Add(new WorkflowProfileRecordRow
        {
            ProjectId = projectId,
            ProfileId = request.ProfileId,
            Name = request.Name,
            Description = request.Description,
            DefinitionSource = request.DefinitionSource,
            SourceProvenance = nameof(WorkflowProfileSourceProvenance.Verbatim),
            Revision = createdRevision,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync(ct);
        return SaveResult(projectId, request, validation, createdRevision);
    }

    /// <summary>
    /// Fresh opaque revision token. Random rather than derived: a token
    /// must never validate again once superseded, including when content
    /// changes away and back or the identity is deleted and recreated.
    /// </summary>
    private static string NewRevision() => WorkflowProfileRevisionSeed.Next();

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
        WorkflowDefinitionValidationResult validation,
        string? revision = null) => new(
        new WorkflowProfileCollectionEntry(
            ProjectId: projectId,
            ProfileId: request.ProfileId,
            Name: request.Name,
            Description: request.Description,
            SourceProvenance: WorkflowProfileSourceProvenance.Verbatim,
            IsBuiltIn: false,
            DefinitionSource: request.DefinitionSource,
            Revision: revision),
        validation);

    private static WorkflowProfileCollectionEntry ToEntry(WorkflowProfileRecordRow row) => new(
        ProjectId: row.ProjectId,
        ProfileId: row.ProfileId,
        Name: row.Name,
        Description: row.Description,
        SourceProvenance: ParseProvenance(row.SourceProvenance),
        IsBuiltIn: false,
        DefinitionSource: row.DefinitionSource,
        Revision: row.Revision);

    private static WorkflowProfileSourceProvenance ParseProvenance(string value) =>
        Enum.TryParse<WorkflowProfileSourceProvenance>(value, ignoreCase: false, out var parsed)
            ? parsed
            : WorkflowProfileSourceProvenance.Verbatim;
}
