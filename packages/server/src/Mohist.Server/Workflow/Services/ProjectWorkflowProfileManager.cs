using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Workflow.Domain;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Services.Prompts;
using Mohist.Server.Workflow.Domain.Prompts;
using Mohist.Server.Infrastructure.Data.Workflow.Prompts;
using Mohist.Server.Infrastructure.Data.Workflow;

namespace Mohist.Server.Workflow.Services;

/// <summary>
/// Project-scope template and prompt endpoint.
/// 管理: 项目模板 CRUD + 系统模板 catalog 读取 + 项目默认模板设置 + 项目提示词。
/// </summary>
public class ProjectWorkflowProfileManager : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IPromptLoader _promptLoader;
    private readonly PromptTemplateEngine _engine;
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
        IPromptLoader promptLoader,
        PromptTemplateEngine engine,
        IActionCatalogSource catalogSource)
    {
        _dbFactory = dbFactory;
        _promptLoader = promptLoader;
        _engine = engine;
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
    // Prompts
    // =======================================================================

    public async Task<IReadOnlyList<SystemTemplate>> ListSystemPromptsAsync()
    {
        return _promptLoader.LoadAllTemplates().Values
            .OrderBy(t => t.Key, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<IReadOnlyList<EffectivePrompt>> ListPromptsAsync(string projectId)
    {
        var systemTemplates = _promptLoader.LoadAllTemplates();
        var projectPrompts = await LoadProjectPromptsAsync(projectId);

        var keys = new SortedSet<string>(systemTemplates.Keys, StringComparer.Ordinal);
        foreach (var k in projectPrompts.Keys)
            keys.Add(k);

        return keys.Select(key =>
        {
            if (projectPrompts.TryGetValue(key, out var body))
            {
                var source = systemTemplates.ContainsKey(key) ? "project" : "project-new";
                return new EffectivePrompt(key, key, string.Empty, Array.Empty<string>(), null, body, source);
            }

            var sys = systemTemplates[key];
            return new EffectivePrompt(key, sys.DisplayName, sys.Description, sys.Tags, sys.Stage, sys.Body, "system");
        }).ToList();
    }

    public async Task<EffectivePrompt?> GetPromptAsync(string projectId, string key)
    {
        var projectPrompts = await LoadProjectPromptsAsync(projectId);
        if (projectPrompts.TryGetValue(key, out var body))
        {
            var systemTemplates = _promptLoader.LoadAllTemplates();
            var source = systemTemplates.ContainsKey(key) ? "project" : "project-new";
            return new EffectivePrompt(key, key, string.Empty, Array.Empty<string>(), null, body, source);
        }

        var systemTemplatesMap = _promptLoader.LoadAllTemplates();
        if (systemTemplatesMap.TryGetValue(key, out var sys))
            return new EffectivePrompt(key, sys.DisplayName, sys.Description, sys.Tags, sys.Stage, sys.Body, "system");

        return null;
    }

    public async Task SetPromptAsync(string projectId, string key, string body)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("key is required", nameof(key));

        await using var db = await _dbFactory.CreateDbContextAsync();
        var profile = await db.ProjectWorkflowProfiles
            .FirstOrDefaultAsync(x => x.ProjectId == projectId);

        if (profile is null)
        {
            profile = new ProjectWorkflowProfile
            {
                ProjectId = projectId,
                Variables = VariableBundle.Empty.ToJson(),
                Prompts = new Dictionary<string, string>(StringComparer.Ordinal) { [key] = body },
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.ProjectWorkflowProfiles.Add(profile);
        }
        else
        {
            var prompts = new Dictionary<string, string>(profile.Prompts, StringComparer.Ordinal)
            {
                [key] = body,
            };
            profile.Prompts = prompts;
            profile.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync();
    }

    public async Task DeletePromptAsync(string projectId, string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("key is required", nameof(key));

        await using var db = await _dbFactory.CreateDbContextAsync();
        var profile = await db.ProjectWorkflowProfiles
            .FirstOrDefaultAsync(x => x.ProjectId == projectId);
        if (profile is null) return;

        var prompts = new Dictionary<string, string>(profile.Prompts, StringComparer.Ordinal);
        if (!prompts.Remove(key)) return;
        profile.Prompts = prompts;
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    public async Task<EffectivePrompt?> GetProjectPromptOverrideAsync(string projectId, string key)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.ProjectPromptTemplates.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.Key == key);
        return row is null
            ? null
            : new EffectivePrompt(
                row.Key,
                string.IsNullOrWhiteSpace(row.DisplayName) ? row.Key : row.DisplayName,
                row.Description,
                DeserializeTags(row.TagsJson),
                row.Stage,
                row.Body,
                "project-override");
    }

    public async Task<EffectivePrompt> SetProjectPromptOverrideAsync(
        string projectId,
        string key,
        string? displayName,
        string? description,
        string[]? tags,
        string? stage,
        string body)
    {
        await SetPromptAsync(projectId, key, body);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.ProjectPromptTemplates
            .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.Key == key);

        if (row is null)
        {
            row = new ProjectPromptTemplateRow { ProjectId = projectId, Key = key };
            db.ProjectPromptTemplates.Add(row);
        }

        row.DisplayName = string.IsNullOrWhiteSpace(displayName) ? key : displayName!;
        row.Description = description ?? string.Empty;
        row.TagsJson = JSON.Serialize(tags ?? Array.Empty<string>());
        row.Stage = stage;
        row.Body = body;
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        return new EffectivePrompt(row.Key, row.DisplayName, row.Description, tags ?? Array.Empty<string>(), row.Stage, row.Body, "project-override");
    }

    public async Task<bool> DeleteProjectPromptOverrideAsync(string projectId, string key)
    {
        var existing = await GetProjectPromptOverrideAsync(projectId, key);
        await DeletePromptAsync(projectId, key);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.ProjectPromptTemplates
            .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.Key == key);
        if (row is not null)
        {
            db.ProjectPromptTemplates.Remove(row);
            await db.SaveChangesAsync();
        }

        return existing is not null;
    }

    public async Task<PromptPreviewResult> PreviewPromptAsync(
        string projectId, string key, JsonElement variables)
    {
        var effective = await GetPromptAsync(projectId, key)
            ?? throw new ArgumentException($"Prompt '{key}' not found");

        var result = _engine.Render(effective.Body, variables);
        return new PromptPreviewResult(result.Rendered, result.MissingVariables, result.Depth, result.Errors);
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

    private async Task<Dictionary<string, string>> LoadProjectPromptsAsync(string projectId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var profile = await db.ProjectWorkflowProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectId == projectId);
        return profile?.Prompts ?? new(StringComparer.Ordinal);
    }

    private static string[] DeserializeTags(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JSON.Deserialize<string[]>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

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
