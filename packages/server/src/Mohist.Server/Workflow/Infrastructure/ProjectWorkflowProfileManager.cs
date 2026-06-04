using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Persistence.Db;
using Mohist.Server.Issue.WorkflowProfiles;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Prompts;
using Mohist.Server.Workflow.Prompts.Domain;
using Mohist.Server.Workflow.Prompts.Infrastructure;
using Mohist.Server.Workflow.Storage;

namespace Mohist.Server.Workflow.Infrastructure;

/// <summary>
/// Project-scope template + variables + prompts write endpoint.
/// 管理: 项目模板 CRUD + 项目级变量 Set/Patch + 系统模板 catalog 读取 + 项目默认模板设置 + 项目提示词。
/// </summary>
public class ProjectWorkflowProfileManager
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IPromptLoader _promptLoader;
    private readonly PromptTemplateEngine _engine;

    /// <summary>
    /// Hardcoded system templates (in-binary, read-only).
    /// </summary>
    private static readonly SystemTemplateInfo[] SystemTemplates =
    [
        new(
            Id: "mohist/default",
            Name: "Mohist Default",
            Description: "Plan, build, check, and integrate an issue using OpenSpec artifacts.")
    ];

    public ProjectWorkflowProfileManager(
        IDbContextFactory<MohistDbContext> dbFactory,
        IPromptLoader promptLoader,
        PromptTemplateEngine engine)
    {
        _dbFactory = dbFactory;
        _promptLoader = promptLoader;
        _engine = engine;
    }

    // =======================================================================
    // System templates (read-only catalog)
    // =======================================================================

    public Task<IReadOnlyList<SystemTemplateInfo>> ListSystemTemplatesAsync()
    {
        return Task.FromResult<IReadOnlyList<SystemTemplateInfo>>(SystemTemplates);
    }

    public static WorkflowDefinition? GetSystemTemplateDefinition(string templateId)
    {
        if (string.Equals(templateId, "mohist/default", StringComparison.Ordinal))
            return MohistWorkflow.Definition;
        return null;
    }

    // =======================================================================
    // Project templates (CRUD)
    // =======================================================================

    public async Task<IReadOnlyList<ProjectTemplateInfo>> ListTemplatesAsync(string projectId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var rows = await db.ProjectTemplates.AsNoTracking()
            .Where(x => x.ProjectId == projectId)
            .OrderBy(x => x.TemplateId)
            .ToListAsync();

        return rows.Select(x => new ProjectTemplateInfo(x.ProjectId, x.TemplateId, x.CreatedAt, x.UpdatedAt)).ToList();
    }

    public async Task<WorkflowDefinition?> GetTemplateAsync(string projectId, string templateId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.ProjectTemplates.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.TemplateId == templateId);
        return row is null ? null : DeserializeDefinition(row.TemplateJson);
    }

    public async Task<ProjectTemplateInfo> CreateTemplateAsync(string projectId, string yaml)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("projectId is required", nameof(projectId));
        if (string.IsNullOrWhiteSpace(yaml))
            throw new ArgumentException("yaml is required", nameof(yaml));

        var def = WorkflowYamlSerializer.FromYaml(yaml);
        if (string.IsNullOrWhiteSpace(def.Id))
            throw new InvalidOperationException("Template YAML must include an id");

        await using var db = await _dbFactory.CreateDbContextAsync();

        var exists = await db.ProjectTemplates.AnyAsync(x => x.ProjectId == projectId && x.TemplateId == def.Id);
        if (exists)
            throw new InvalidOperationException($"Template '{def.Id}' already exists in project '{projectId}'");

        var now = DateTimeOffset.UtcNow;
        var row = new ProjectTemplateRow
        {
            ProjectId = projectId,
            TemplateId = def.Id,
            TemplateJson = SerializeDefinition(def),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.ProjectTemplates.Add(row);
        await db.SaveChangesAsync();

        return new ProjectTemplateInfo(row.ProjectId, row.TemplateId, row.CreatedAt, row.UpdatedAt);
    }

    public async Task<ProjectTemplateInfo?> UpdateTemplateAsync(string projectId, string templateId, string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            throw new ArgumentException("yaml is required", nameof(yaml));

        var def = WorkflowYamlSerializer.FromYaml(yaml);
        if (!string.Equals(def.Id, templateId, StringComparison.Ordinal))
            throw new InvalidOperationException($"Template id mismatch: expected '{templateId}' but YAML declares '{def.Id}'");

        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.ProjectTemplates
            .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.TemplateId == templateId);
        if (row is null) return null;

        row.TemplateJson = SerializeDefinition(def);
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        return new ProjectTemplateInfo(row.ProjectId, row.TemplateId, row.CreatedAt, row.UpdatedAt);
    }

    public async Task<bool> DeleteTemplateAsync(string projectId, string templateId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.ProjectTemplates
            .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.TemplateId == templateId);
        if (row is null) return false;

        db.ProjectTemplates.Remove(row);
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
            row = new ProjectWorkflowProfileRow
            {
                ProjectId = projectId,
                DefaultTemplateId = templateId,
                VariablesJson = VariableBundle.Empty.ToJson(),
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
    // Project variables (Set + Patch)
    // =======================================================================

    public async Task<VariableBundle> GetVariablesAsync(string projectId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.ProjectWorkflowProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectId == projectId);
        return row is null ? VariableBundle.Empty : VariableBundle.FromJson(row.VariablesJson);
    }

    public async Task<VariableBundle> SetVariablesAsync(string projectId, VariableBundle bundle)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.ProjectWorkflowProfiles
            .FirstOrDefaultAsync(x => x.ProjectId == projectId);

        if (row is null)
        {
            row = new ProjectWorkflowProfileRow
            {
                ProjectId = projectId,
                VariablesJson = bundle.ToJson(),
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.ProjectWorkflowProfiles.Add(row);
        }
        else
        {
            row.VariablesJson = bundle.ToJson();
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync();
        return bundle;
    }

    public async Task<VariableBundle> PatchVariablesAsync(string projectId, VariableBundle patch)
    {
        var current = await GetVariablesAsync(projectId);
        var merged = VariableBundle.Patch(current, patch);
        return await SetVariablesAsync(projectId, merged);
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
        var projectRows = await LoadProjectPromptRowsAsync(projectId);
        var projectByKey = new Dictionary<string, Prompts.Storage.ProjectTemplateRow>(StringComparer.Ordinal);
        foreach (var row in projectRows)
            projectByKey[row.Key] = row;

        var keys = new SortedSet<string>(systemTemplates.Keys, StringComparer.Ordinal);
        foreach (var row in projectRows)
            keys.Add(row.Key);

        return keys.Select(key =>
        {
            if (projectByKey.TryGetValue(key, out var row))
            {
                var source = systemTemplates.ContainsKey(key) ? "project" : "project-new";
                return ToEffectivePrompt(row, source);
            }

            var sys = systemTemplates[key];
            return new EffectivePrompt(key, sys.DisplayName, sys.Description, sys.Tags, sys.Stage, sys.Body, "system");
        }).ToList();
    }

    public async Task<EffectivePrompt?> GetPromptAsync(string projectId, string key)
    {
        var row = await FindProjectPromptRowAsync(projectId, key);
        if (row is not null)
        {
            var systemTemplates = _promptLoader.LoadAllTemplates();
            var source = systemTemplates.ContainsKey(key) ? "project" : "project-new";
            return ToEffectivePrompt(row, source);
        }

        var systemTemplatesMap = _promptLoader.LoadAllTemplates();
        if (systemTemplatesMap.TryGetValue(key, out var sys))
            return new EffectivePrompt(key, sys.DisplayName, sys.Description, sys.Tags, sys.Stage, sys.Body, "system");

        return null;
    }

    public async Task<EffectivePrompt> SetPromptAsync(
        string projectId,
        string key,
        string body,
        string displayName = "",
        string description = "",
        IReadOnlyList<string>? tags = null,
        string? stage = null)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("key is required", nameof(key));

        var now = DateTime.UtcNow;
        var tagsJson = SerializeTags(tags ?? Array.Empty<string>());

        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.ProjectPromptTemplates
            .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.Key == key);

        if (row is null)
        {
            row = new Prompts.Storage.ProjectTemplateRow
            {
                ProjectId = projectId,
                Key = key,
                DisplayName = displayName,
                Description = description,
                TagsJson = tagsJson,
                Stage = stage,
                Body = body,
                UpdatedAt = now,
            };
            db.ProjectPromptTemplates.Add(row);
        }
        else
        {
            row.DisplayName = displayName;
            row.Description = description;
            row.TagsJson = tagsJson;
            row.Stage = stage;
            row.Body = body;
            row.UpdatedAt = now;
        }

        await db.SaveChangesAsync();
        return ToEffectivePrompt(row, "project");
    }

    public async Task DeletePromptAsync(string projectId, string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("key is required", nameof(key));

        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.ProjectPromptTemplates
            .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.Key == key);
        if (row is null) return;

        db.ProjectPromptTemplates.Remove(row);
        await db.SaveChangesAsync();
    }

    public async Task<PromptPreviewResult> PreviewPromptAsync(
        string projectId, string key, JsonElement variables)
    {
        var effective = await GetPromptAsync(projectId, key)
            ?? throw new ArgumentException($"Prompt '{key}' not found");

        var (rendered, missing, depth) = _engine.Render(effective.Body, variables);
        return new PromptPreviewResult(rendered, missing, depth);
    }

    // =======================================================================
    // Helpers
    // =======================================================================

    private async Task<IReadOnlyList<Prompts.Storage.ProjectTemplateRow>> LoadProjectPromptRowsAsync(string projectId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.ProjectPromptTemplates.AsNoTracking()
            .Where(x => x.ProjectId == projectId)
            .OrderBy(x => x.Key)
            .ToListAsync();
    }

    private async Task<Prompts.Storage.ProjectTemplateRow?> FindProjectPromptRowAsync(string projectId, string key)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.ProjectPromptTemplates.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.Key == key);
    }

    internal static EffectivePrompt ToEffectivePrompt(Prompts.Storage.ProjectTemplateRow row, string source) =>
        new(row.Key, row.DisplayName, row.Description, DeserializeTags(row.TagsJson), row.Stage, row.Body, source);

    internal static IReadOnlyList<string> DeserializeTags(string tagsJson)
    {
        if (string.IsNullOrWhiteSpace(tagsJson)) return Array.Empty<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(tagsJson) ?? new List<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    internal static string SerializeTags(IReadOnlyList<string> tags) =>
        JsonSerializer.Serialize(tags.ToList());

    private async Task<bool> ProjectTemplateExistsAsync(string projectId, string templateId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.ProjectTemplates.AnyAsync(x => x.ProjectId == projectId && x.TemplateId == templateId);
    }

    private static string SerializeDefinition(WorkflowDefinition def) =>
        JsonSerializer.Serialize(def, WorkflowYamlSerializer.JsonOptions);

    private static WorkflowDefinition? DeserializeDefinition(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<WorkflowDefinition>(json, WorkflowYamlSerializer.JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}
