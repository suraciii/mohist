using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Workflow.Domain;
using Mohist.Workflow.Definition;
using Mohist.Server.Infrastructure.Data.Workflow;

namespace Mohist.Server.Workflow.Services;

/// <summary>
/// Project-scope template and prompt endpoint.
/// 管理: 项目模板 CRUD + 系统模板 catalog 读取 + 项目默认模板设置 + 项目提示词。
/// </summary>
public class ProjectWorkflowProfileManager : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IActionCatalogSource _catalogSource;

    /// <summary>
    /// Hardcoded system templates (in-binary, read-only).
    /// </summary>
    private static readonly SystemTemplateInfo[] SystemTemplates = BuildSystemTemplates();

    private static SystemTemplateInfo[] BuildSystemTemplates()
    {
        return
        [
            new SystemTemplateInfo(
                Id: WorkflowProfileCatalog.LocalId,
                Name: WorkflowProfileCatalog.Profile.Name,
                Description: WorkflowProfileCatalog.Profile.Description,
                IsDefault: true),
            new SystemTemplateInfo(
                Id: WorkflowProfileCatalog.GithubPrId,
                Name: WorkflowProfileCatalog.GithubPrProfileAsset.Name,
                Description: WorkflowProfileCatalog.GithubPrProfileAsset.Description,
                IsDefault: false),
        ];
    }

    public ProjectWorkflowProfileManager(
        IDbContextFactory<MohistDbContext> dbFactory,
        IActionCatalogSource catalogSource)
    {
        _dbFactory = dbFactory;
        _catalogSource = catalogSource;
    }

    // =======================================================================
    // System templates (read-only catalog)
    // =======================================================================

    public Task<IReadOnlyList<SystemTemplateInfo>> ListSystemTemplatesAsync()
    {
        return Task.FromResult<IReadOnlyList<SystemTemplateInfo>>(SystemTemplates);
    }

    public static SystemTemplateInfo? GetSystemTemplateInfo(string templateId)
    {
        foreach (var template in SystemTemplates)
            if (WorkflowProfileCatalog.IdComparer.Equals(template.Id, templateId))
                return template;
        return null;
    }

    public static WorkflowDefinition? GetSystemTemplateDefinition(string templateId)
    {
        return WorkflowProfileCatalog.GetDefinition(templateId);
    }

    // =======================================================================
    // Project templates (CRUD)
    // =======================================================================

    public async Task<IReadOnlyList<ProjectTemplateInfo>> ListTemplatesAsync(string projectId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var rows = await db.ProjectWorkflowTemplates.AsNoTracking()
            .Where(x => x.ProjectId == projectId)
            .OrderBy(x => x.TemplateId)
            .ToListAsync();

        return rows.Select(x =>
        {
            var profile = WorkflowProfilePersistence.Deserialize(x.Template);
            return new ProjectTemplateInfo(x.ProjectId, x.TemplateId, x.CreatedAt, x.UpdatedAt, profile?.Name, profile?.Description);
        }).ToList();
    }

    public async Task<WorkflowDefinition?> GetTemplateAsync(string projectId, string templateId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.ProjectWorkflowTemplates.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.TemplateId == templateId);
        return row is null ? null : WorkflowProfilePersistence.Deserialize(row.Template).Definition;
    }

    public async Task<WorkflowProfile?> GetTemplateProfileAsync(string projectId, string templateId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.ProjectWorkflowTemplates.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.TemplateId == templateId);
        return row is null ? null : WorkflowProfilePersistence.Deserialize(row.Template);
    }

    public async Task<ProjectTemplateSaveResult> CreateTemplateAsync(string projectId, string yaml)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("projectId is required", nameof(projectId));
        if (string.IsNullOrWhiteSpace(yaml))
            throw new ArgumentException("yaml is required", nameof(yaml));

        var catalog = await _catalogSource.GetCatalogAsync();
        var profile = WorkflowProfileYamlParser.Parse(yaml, "workflow", catalog);
        var templateId = profile.Id;
        if (string.IsNullOrWhiteSpace(templateId))
            throw new InvalidOperationException("Template id is required");

        await using var db = await _dbFactory.CreateDbContextAsync();

        var exists = await db.ProjectWorkflowTemplates.AnyAsync(x => x.ProjectId == projectId && x.TemplateId == templateId);
        if (exists)
            throw new InvalidOperationException($"Template '{templateId}' already exists in project '{projectId}'");

        var now = DateTimeOffset.UtcNow;
        var row = new ProjectWorkflowTemplateRow
        {
            ProjectId = projectId,
            TemplateId = templateId,
            Template = WorkflowProfilePersistence.Serialize(profile),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.ProjectWorkflowTemplates.Add(row);
        await db.SaveChangesAsync();

        var info = new ProjectTemplateInfo(row.ProjectId, row.TemplateId, row.CreatedAt, row.UpdatedAt, profile.Name, profile.Description);
        return new ProjectTemplateSaveResult(info, BuildActionValidationStatus(catalog));
    }

    public async Task<ProjectTemplateSaveResult?> UpdateTemplateAsync(string projectId, string templateId, string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            throw new ArgumentException("yaml is required", nameof(yaml));

        var catalog = await _catalogSource.GetCatalogAsync();
        var profile = WorkflowProfileYamlParser.Parse(yaml, templateId, catalog);
        if (!string.Equals(profile.Id, templateId, StringComparison.Ordinal))
            throw new InvalidOperationException($"Template id mismatch: expected '{templateId}' but YAML declares '{profile.Id}'");

        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.ProjectWorkflowTemplates
            .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.TemplateId == templateId);
        if (row is null) return null;

        row.Template = WorkflowProfilePersistence.Serialize(profile);
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var info = new ProjectTemplateInfo(row.ProjectId, row.TemplateId, row.CreatedAt, row.UpdatedAt, profile.Name, profile.Description);
        return new ProjectTemplateSaveResult(info, BuildActionValidationStatus(catalog));
    }

    public async Task<bool> DeleteTemplateAsync(string projectId, string templateId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.ProjectWorkflowTemplates
            .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.TemplateId == templateId);
        if (row is null) return false;

        db.ProjectWorkflowTemplates.Remove(row);
        await db.SaveChangesAsync();
        return true;
    }

    // =======================================================================
    // Project default template
    // =======================================================================

    public async Task<string?> SetDefaultTemplateAsync(string projectId, string? templateId)
    {
        if (!string.IsNullOrWhiteSpace(templateId))
        {
            var existsInProject = await ProjectTemplateExistsAsync(projectId, templateId);
            if (!existsInProject && GetSystemTemplateDefinition(templateId) is null)
                throw new InvalidOperationException(
                    $"Template '{templateId}' must exist in project templates or be a system template before setting as default");
        }

        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.ProjectWorkflowProfiles
            .FirstOrDefaultAsync(x => x.ProjectId == projectId);

        if (row is null)
        {
            row = new ProjectWorkflowProfile
            {
                ProjectId = projectId,
                DefaultTemplateId = templateId,
                Variables = VariableBundle.Empty.ToJson(),
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.ProjectWorkflowProfiles.Add(row);
        }
        else
        {
            row.DefaultTemplateId = templateId;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync();
        return row.DefaultTemplateId;
    }

    public async Task<string?> GetDefaultTemplateAsync(string projectId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.ProjectWorkflowProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectId == projectId);
        return row?.DefaultTemplateId;
    }

    // =======================================================================
    // Disabled workflow profiles
    // =======================================================================

    public async Task<IReadOnlySet<string>> GetDisabledWorkflowProfileIdsAsync(string projectId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.ProjectWorkflowProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectId == projectId);
        return row?.DisabledWorkflowProfileIds?.ToHashSet(WorkflowProfileCatalog.IdComparer)
            ?? new HashSet<string>(WorkflowProfileCatalog.IdComparer);
    }

    public async Task SetProfileEnabledAsync(string projectId, string profileId, bool enabled)
    {
        var canonicalProfileId = GetSystemTemplateInfo(profileId)?.Id;
        if (canonicalProfileId is null)
            throw new ArgumentException($"Unknown workflow profile '{profileId}'", nameof(profileId));

        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.ProjectWorkflowProfiles
            .FirstOrDefaultAsync(x => x.ProjectId == projectId);

        if (row is null)
        {
            if (enabled)
                return;

            var allSystemIds = SystemTemplates.Select(t => t.Id).ToHashSet(WorkflowProfileCatalog.IdComparer);
            if (allSystemIds.Count <= 1)
                throw new InvalidOperationException(
                    $"Cannot disable '{profileId}': at least one workflow profile must remain enabled. " +
                    "Enable a different profile first or leave the current profile enabled.");

            row = new ProjectWorkflowProfile
            {
                ProjectId = projectId,
                Variables = VariableBundle.Empty.ToJson(),
                DisabledWorkflowProfileIds = [canonicalProfileId],
                UpdatedAt = DateTimeOffset.UtcNow,
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
                var allSystemIds = SystemTemplates.Select(t => t.Id).ToHashSet(WorkflowProfileCatalog.IdComparer);
                var enabledCount = allSystemIds.Count(id => !disabled.Contains(id));
                if (enabledCount == 0)
                    throw new InvalidOperationException(
                        $"Cannot disable '{profileId}': at least one workflow profile must remain enabled. " +
                        "Enable a different profile first or leave the current profile enabled.");
            }

            row.DisabledWorkflowProfileIds = [..disabled];
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync();
    }

    // =======================================================================
    // Helpers
    // =======================================================================

    private async Task<bool> ProjectTemplateExistsAsync(string projectId, string templateId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.ProjectWorkflowTemplates.AnyAsync(x => x.ProjectId == projectId && x.TemplateId == templateId);
    }

    private static ActionValidationStatus BuildActionValidationStatus(ActionCatalog? catalog) =>
        catalog is null
            ? ActionValidationStatus.Skipped
            : ActionValidationStatus.Performed;

}
