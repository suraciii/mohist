using Microsoft.EntityFrameworkCore;
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

        var builtins = WorkflowProfileCatalog.SystemProfileIds
            .Select(WorkflowProfileCollectionEntry.BuiltIn)
            .ToList();

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
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

        if (WorkflowProfileCatalog.IsSystemProfile(profileId))
            return WorkflowProfileCollectionEntry.BuiltIn(profileId);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
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
            return WorkflowProfileCatalog.GetDefinition(profileId);

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
        {
            var profile = WorkflowProfileCatalog.GetProfile(profileId);
            return profile is null ? null : WorkflowProfileCanonicalYamlRenderer.Render(profile);
        }

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

        var profile = WorkflowProfileYamlParser.Parse(
            request.DefinitionSource,
            request.ProfileId);

        var catalog = await _catalogSource.GetCatalogAsync();
        var actionErrors = catalog is null
            ? Array.Empty<ValidationError>()
            : ActionContractValidator.Validate(profile.Definition, catalog);

        var validation = new WorkflowDefinitionValidationResult(
            DefinitionErrors: Array.Empty<ValidationError>(),
            ActionErrors: actionErrors,
            ActionValidationStatus: catalog is null
                ? ActionValidationStatus.Skipped
                : ActionValidationStatus.Performed);

        if (validation.HasDefinitionErrors || validation.HasActionErrors)
        {
            return new WorkflowProfileSaveResult(
                new WorkflowProfileCollectionEntry(
                    ProjectId: projectId,
                    ProfileId: request.ProfileId,
                    Name: profile.Name,
                    Description: profile.Description,
                    SourceProvenance: WorkflowProfileSourceProvenance.Verbatim,
                    IsBuiltIn: false,
                    DefinitionSource: request.DefinitionSource),
                validation);
        }

        var now = _timeProvider.GetUtcNow();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.WorkflowProfileRecords
            .FirstOrDefaultAsync(r => r.ProjectId == projectId && r.ProfileId == request.ProfileId, ct);

        if (isUpdate)
        {
            if (existing is null)
                throw new WorkflowProfileNotFoundException(projectId, request.ProfileId);

            existing.Name = profile.Name;
            existing.Description = profile.Description;
            existing.DefinitionSource = request.DefinitionSource;
            existing.SourceProvenance = nameof(WorkflowProfileSourceProvenance.Verbatim);
            existing.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
        }
        else
        {
            if (existing is not null)
                throw new WorkflowProfileAlreadyExistsException(projectId, request.ProfileId);

            var row = new WorkflowProfileRecordRow
            {
                ProjectId = projectId,
                ProfileId = request.ProfileId,
                Name = profile.Name,
                Description = profile.Description,
                DefinitionSource = request.DefinitionSource,
                SourceProvenance = nameof(WorkflowProfileSourceProvenance.Verbatim),
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.WorkflowProfileRecords.Add(row);
            await db.SaveChangesAsync(ct);
        }

        return new WorkflowProfileSaveResult(
            new WorkflowProfileCollectionEntry(
                ProjectId: projectId,
                ProfileId: request.ProfileId,
                Name: profile.Name,
                Description: profile.Description,
                SourceProvenance: WorkflowProfileSourceProvenance.Verbatim,
                IsBuiltIn: false,
                DefinitionSource: request.DefinitionSource),
            validation);
    }

    private static WorkflowProfileCollectionEntry ToEntry(WorkflowProfileRecordRow row) =>
        new(
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
