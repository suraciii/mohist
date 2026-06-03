using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Persistence.Db;
using Mohist.Server.Issue.WorkflowProfiles;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Storage;

namespace Mohist.Server.Workflow.Infrastructure;

/// <summary>
/// Project-scope template + variables write endpoint.
/// 管理: 项目模板 CRUD + 项目级变量 Set/Patch + 系统模板 catalog 读取 + 项目默认模板设置。
/// </summary>
public class ProjectWorkflowProfileManager
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

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

    public ProjectWorkflowProfileManager(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
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
        // Currently only mohist/default exists.
        // Add more branches here when adding more system templates.
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
        // templateId = null means "clear default" - caller can reset.
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
    // Helpers
    // =======================================================================

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
