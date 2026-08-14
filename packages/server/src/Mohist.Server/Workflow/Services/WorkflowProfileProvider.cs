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
        var settings = await db.ProjectWorkflowProfiles.AsNoTracking()
            .FirstOrDefaultAsync(row => row.ProjectId == projectId, ct);
        var overrides = settings?.AgentActionOverrides
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var builtins = WorkflowProfileCatalog.SystemProfileIds
            .Select(profileId => WorkflowProfileCollectionEntry.BuiltIn(
                profileId,
                projectId,
                overrides.GetValueOrDefault(profileId)))
            .ToList();
        var customRows = await db.WorkflowProfileRecords.AsNoTracking()
            .Where(r => r.ProjectId == projectId)
            .OrderBy(r => r.ProfileId)
            .ToListAsync(ct);

        var customs = customRows.Select(row => ToEntry(row, overrides.GetValueOrDefault(row.ProfileId))).ToList();

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
        var overrides = await db.ProjectWorkflowProfiles.AsNoTracking()
            .Where(row => row.ProjectId == projectId)
            .Select(row => row.AgentActionOverrides)
            .FirstOrDefaultAsync(ct);
        var agentActionOverride = overrides?.GetValueOrDefault(profileId);
        if (WorkflowProfileCatalog.IsSystemProfile(profileId))
            return WorkflowProfileCollectionEntry.BuiltIn(profileId, projectId, agentActionOverride);

        var row = await db.WorkflowProfileRecords.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ProjectId == projectId && r.ProfileId == profileId, ct);
        return row is null ? null : ToEntry(row, agentActionOverride);
    }

    public async Task<WorkflowDefinition?> GetDefinitionAsync(
        string projectId, string profileId, CancellationToken ct = default)
    {
        var agentActionOverride = await GetAgentActionOverrideAsync(projectId, profileId, ct);
        return await GetDefinitionAsync(projectId, profileId, agentActionOverride, ct);
    }

    public async Task<WorkflowDefinition?> GetDefinitionAsync(
        string projectId,
        string profileId,
        string? boundAgentAction,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(profileId))
            return null;

        if (WorkflowProfileCatalog.IsSystemProfile(profileId))
        {
            var source = WorkflowProfileCatalog.GetProfile(profileId);
            if (source is null) return null;
            return boundAgentAction is null || string.Equals(boundAgentAction, source.AgentAction, StringComparison.Ordinal)
                ? source.Definition
                : WorkflowProfileYamlParser.Parse(
                    WorkflowProfileCatalog.GetDefinitionSource(profileId)!,
                    profileId,
                    agentActionOverride: boundAgentAction).Definition;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.WorkflowProfileRecords.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ProjectId == projectId && r.ProfileId == profileId, ct);
        if (row is null)
            return null;

        return WorkflowProfileYamlParser.Parse(
            row.DefinitionSource,
            profileId,
            agentActionOverride: boundAgentAction).Definition;
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

    public async Task<string?> GetAgentActionOverrideAsync(
        string projectId,
        string profileId,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var overrides = await db.ProjectWorkflowProfiles.AsNoTracking()
            .Where(row => row.ProjectId == projectId)
            .Select(row => row.AgentActionOverrides)
            .FirstOrDefaultAsync(ct);
        return overrides?.GetValueOrDefault(profileId);
    }

    public async Task ValidateAgentActionOverrideAsync(
        string projectId,
        string profileId,
        string? agentAction,
        CancellationToken ct = default)
    {
        var source = await GetDefinitionSourceAsync(projectId, profileId, ct)
            ?? throw new WorkflowProfileNotFoundException(projectId, profileId);
        var declared = WorkflowProfileYamlParser.Parse(source, profileId);
        if (declared.AgentAction is null)
            throw new WorkflowDefinitionValidationException(
                [new ValidationError("agentAction", $"WorkflowProfile '{profileId}' does not expose an Agent Action binding")]);

        var catalog = await _catalogSource.GetCatalogAsync()
            ?? throw new WorkflowDefinitionValidationException(
                [new ValidationError("agentAction", "Agent Action binding requires an available Runner Action catalog", ValidationSource.Action)]);
        _ = WorkflowProfileYamlParser.Parse(
            source,
            profileId,
            catalog,
            agentActionOverride: agentAction ?? declared.AgentAction);
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

        var sourceProfile = WorkflowProfileYamlParser.Parse(
            request.DefinitionSource,
            request.ProfileId);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.WorkflowProfileRecords
            .FirstOrDefaultAsync(r => r.ProjectId == projectId && r.ProfileId == request.ProfileId, ct);
        if (isUpdate && existing is null)
            throw new WorkflowProfileNotFoundException(projectId, request.ProfileId);
        if (!isUpdate && existing is not null)
            throw new WorkflowProfileAlreadyExistsException(projectId, request.ProfileId);

        var settings = await db.ProjectWorkflowProfiles.AsNoTracking()
            .FirstOrDefaultAsync(row => row.ProjectId == projectId, ct);
        var futureAction = settings?.AgentActionOverrides.GetValueOrDefault(request.ProfileId)
            ?? sourceProfile.AgentAction;
        var profile = WorkflowProfileYamlParser.Parse(
            request.DefinitionSource,
            request.ProfileId,
            agentActionOverride: futureAction == sourceProfile.AgentAction ? null : futureAction);

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

        var definitionErrors = new List<ValidationError>();
        foreach (var run in runBindings)
        {
            var updated = WorkflowProfileYamlParser.Parse(
                request.DefinitionSource,
                request.ProfileId,
                agentActionOverride: run.AgentAction);
            var updatedStages = updated.Definition.Stages
                .ToDictionary(stage => stage.Stage, StringComparer.Ordinal);
            foreach (var stage in run.Stages)
            {
                if (!updatedStages.TryGetValue(stage.Id, out var updatedStage))
                {
                    definitionErrors.Add(new ValidationError(
                        "stages",
                        $"Active WorkflowRun '{run.Id}' requires stage '{stage.Id}'"));
                    continue;
                }

                if (updatedStage.RequiresApproval != stage.RequiresApproval)
                {
                    definitionErrors.Add(new ValidationError(
                        "stages",
                        $"Active WorkflowRun '{run.Id}' requires stage '{stage.Id}' to retain requiresApproval={stage.RequiresApproval.ToString().ToLowerInvariant()}"));
                }
            }
        }

        var catalog = await _catalogSource.GetCatalogAsync();
        var materializedProfiles = new[] { profile }
            .Concat(runBindings
                .Select(run => WorkflowProfileYamlParser.Parse(
                    request.DefinitionSource,
                    request.ProfileId,
                    agentActionOverride: run.AgentAction)))
            .GroupBy(item => item.AgentAction, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        var requiresCatalog = materializedProfiles.Any(item => item.AgentAction is not null);
        var actionErrors = catalog is null
            ? !requiresCatalog
                ? Array.Empty<ValidationError>()
                : [new ValidationError("agentAction", "Agent Action binding requires an available Runner Action catalog", ValidationSource.Action)]
            : materializedProfiles
                .SelectMany(item => ActionContractValidator.Validate(item.Definition, catalog)
                    .Concat(item.AgentAction is null
                        ? []
                        : ActionContractValidator.ValidateAgentAction(item.Definition, item.AgentAction, catalog)))
                .Distinct()
                .OrderBy(error => error.Path, StringComparer.Ordinal)
                .ThenBy(error => error.Message, StringComparer.Ordinal)
                .ToArray();

        var validation = new WorkflowDefinitionValidationResult(
            DefinitionErrors: definitionErrors.Select(WorkflowProfileValidationError.From).ToArray(),
            ActionErrors: actionErrors.Select(WorkflowProfileValidationError.From).ToArray(),
            ActionValidationStatus: catalog is null
                ? ActionValidationStatus.Skipped
                : ActionValidationStatus.Performed);

        if (validation.HasDefinitionErrors || validation.HasActionErrors)
        {
            return new WorkflowProfileSaveResult(
                new WorkflowProfileCollectionEntry(
                    ProjectId: projectId,
                    ProfileId: request.ProfileId,
                    Name: request.Name,
                    Description: request.Description,
                    SourceProvenance: WorkflowProfileSourceProvenance.Verbatim,
                    IsBuiltIn: false,
                    DefinitionSource: request.DefinitionSource),
                validation);
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

        return new WorkflowProfileSaveResult(
            new WorkflowProfileCollectionEntry(
                ProjectId: projectId,
                ProfileId: request.ProfileId,
                Name: request.Name,
                Description: request.Description,
                SourceProvenance: WorkflowProfileSourceProvenance.Verbatim,
                IsBuiltIn: false,
                DefinitionSource: request.DefinitionSource)
            {
                AgentAction = profile.AgentAction,
                AgentRuntime = WorkflowProfileAgentRuntimeProjection.Project(profile.AgentAction)
                    ?? WorkflowProfileAgentRuntimeProjection.Project(profile.Definition),
            },
            validation);
    }

    private static WorkflowProfileCollectionEntry ToEntry(WorkflowProfileRecordRow row, string? agentActionOverride)
    {
        var profile = TryParseProfile(row.DefinitionSource, row.ProfileId, agentActionOverride);
        return new(
            ProjectId: row.ProjectId,
            ProfileId: row.ProfileId,
            Name: row.Name,
            Description: row.Description,
            SourceProvenance: ParseProvenance(row.SourceProvenance),
            IsBuiltIn: false,
            DefinitionSource: row.DefinitionSource)
        {
            AgentAction = profile?.AgentAction,
            AgentRuntime = WorkflowProfileAgentRuntimeProjection.Project(profile?.AgentAction)
                ?? WorkflowProfileAgentRuntimeProjection.Project(profile?.Definition),
        };
    }

    private static WorkflowProfile? TryParseProfile(
        string definitionSource,
        string profileId,
        string? agentActionOverride)
    {
        try
        {
            return WorkflowProfileYamlParser.Parse(
                definitionSource,
                profileId,
                agentActionOverride: agentActionOverride);
        }
        catch (WorkflowDefinitionValidationException)
        {
            return null;
        }
    }

    private static WorkflowProfileSourceProvenance ParseProvenance(string value) =>
        Enum.TryParse<WorkflowProfileSourceProvenance>(value, ignoreCase: false, out var parsed)
            ? parsed
            : WorkflowProfileSourceProvenance.Verbatim;
}
