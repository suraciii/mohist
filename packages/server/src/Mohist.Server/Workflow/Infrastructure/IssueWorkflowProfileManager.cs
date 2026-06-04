using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Persistence.Db;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Storage;

namespace Mohist.Server.Workflow.Infrastructure;

/// <summary>
/// Issue-scope template + variables write endpoint.
/// 
/// UpdateTemplateAsync 入参:
///   ProjectTemplateId: 指向项目模板 (引用, SourceTemplateId 设置, Template 清空)
///   Template:          自定义 YAML 字符串 (Template 设置, SourceTemplateId 清空)
///   两个都 null:       清空 issue 级模板 (继承项目默认)
/// </summary>
public class IssueWorkflowProfileManager
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public IssueWorkflowProfileManager(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    // =======================================================================
    // Template
    // =======================================================================

    public async Task<WorkflowDefinition?> GetTemplateAsync(string issueId, string? legacyIssueKey = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await FindProfileAsync(db, issueId, legacyIssueKey);
        if (row is null) return null;
        if (!string.IsNullOrWhiteSpace(row.Template))
            return DeserializeDefinition(row.Template);
        // SourceTemplateId case - caller should resolve via ProjectWorkflowProfileManager.GetTemplateAsync
        return null;
    }

    public async Task<IssueWorkflowProfile?> GetProfileAsync(string issueId, string? legacyIssueKey = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await FindProfileAsync(db, issueId, legacyIssueKey);
    }

    public async Task<IssueWorkflowProfileState> GetStateAsync(string issueId, string? legacyIssueKey = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await FindProfileAsync(db, issueId, legacyIssueKey);

        return row is null
            ? new IssueWorkflowProfileState(issueId, null, false, null, VariableBundle.Empty, null)
            : new IssueWorkflowProfileState(
                row.IssueKey,
                row.SourceTemplateId,
                !string.IsNullOrWhiteSpace(row.Template),
                string.IsNullOrWhiteSpace(row.Template) ? null : DeserializeDefinition(row.Template),
                VariableBundle.FromJson(row.Variables),
                row.UpdatedAt);
    }

    /// <summary>
    /// Update issue template choice.
    /// - request.ProjectTemplateId set:  reference a project template, clear custom
    /// - request.Template set:           upload custom YAML, clear reference
    /// - both null:                      clear issue-level template (inherit project default)
    /// - both set:                       invalid
    /// </summary>
    public async Task<IssueWorkflowProfile> UpdateTemplateAsync(
        string issueId,
        IssueTemplateUpdateRequest request,
        string? legacyIssueKey = null)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));

        if (!string.IsNullOrWhiteSpace(request.ProjectTemplateId) && !string.IsNullOrWhiteSpace(request.Template))
            throw new InvalidOperationException("Cannot set both ProjectTemplateId and custom Template at the same time");

        WorkflowDefinition? parsed = null;
        if (!string.IsNullOrWhiteSpace(request.Template))
        {
            parsed = WorkflowYamlSerializer.FromYaml(request.Template);
        }

        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.IssueWorkflowProfiles
            .FirstOrDefaultAsync(x => x.IssueKey == issueId);

        if (row is null)
        {
            row = await MigrateLegacyProfileAsync(db, issueId, legacyIssueKey);
        }

        if (row is null)
        {
            row = new IssueWorkflowProfile
            {
                IssueKey = issueId,
                SourceTemplateId = request.ProjectTemplateId,
                Template = parsed is null ? null : SerializeDefinition(parsed),
                Variables = VariableBundle.Empty.ToJson(),
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.IssueWorkflowProfiles.Add(row);
        }
        else
        {
            row.SourceTemplateId = request.ProjectTemplateId;
            row.Template = parsed is null ? null : SerializeDefinition(parsed);
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync();
        return row;
    }

    // =======================================================================
    // Variables (Set + Patch)
    // =======================================================================

    public async Task<VariableBundle> GetVariablesAsync(string issueId, string? legacyIssueKey = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await FindProfileAsync(db, issueId, legacyIssueKey);
        return row is null ? VariableBundle.Empty : VariableBundle.FromJson(row.Variables);
    }

    public async Task<VariableBundle> SetVariablesAsync(string issueId, VariableBundle bundle, string? legacyIssueKey = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.IssueWorkflowProfiles
            .FirstOrDefaultAsync(x => x.IssueKey == issueId);

        if (row is null)
        {
            row = await MigrateLegacyProfileAsync(db, issueId, legacyIssueKey);
        }

        if (row is null)
        {
            row = new IssueWorkflowProfile
            {
                IssueKey = issueId,
                Variables = bundle.ToJson(),
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.IssueWorkflowProfiles.Add(row);
        }
        else
        {
            row.Variables = bundle.ToJson();
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync();
        return bundle;
    }

    public async Task<VariableBundle> PatchVariablesAsync(string issueId, VariableBundle patch, string? legacyIssueKey = null)
    {
        var current = await GetVariablesAsync(issueId, legacyIssueKey);
        var merged = VariableBundle.Patch(current, patch);
        return await SetVariablesAsync(issueId, merged, legacyIssueKey);
    }

    // =======================================================================
    // Prompts
    // =======================================================================

    public async Task<Dictionary<string, string>> GetPromptsAsync(string issueId, string? legacyIssueKey = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var profile = await FindProfileAsync(db, issueId, legacyIssueKey);
        return DeserializePrompts(profile?.Prompts);
    }

    public async Task SetPromptAsync(string issueId, string key, string body, string? legacyIssueKey = null)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("key is required", nameof(key));

        await using var db = await _dbFactory.CreateDbContextAsync();
        var profile = await db.IssueWorkflowProfiles
            .FirstOrDefaultAsync(x => x.IssueKey == issueId);

        if (profile is null)
        {
            profile = await MigrateLegacyProfileAsync(db, issueId, legacyIssueKey);
        }

        if (profile is null)
        {
            profile = new IssueWorkflowProfile
            {
                IssueKey = issueId,
                Variables = VariableBundle.Empty.ToJson(),
                Prompts = SerializePrompt(key, body),
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.IssueWorkflowProfiles.Add(profile);
        }
        else
        {
            var prompts = DeserializePrompts(profile.Prompts);
            prompts[key] = body;
            profile.Prompts = SerializePrompts(prompts);
            profile.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync();
    }

    public async Task DeletePromptAsync(string issueId, string key, string? legacyIssueKey = null)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("key is required", nameof(key));

        await using var db = await _dbFactory.CreateDbContextAsync();
        var profile = await db.IssueWorkflowProfiles
            .FirstOrDefaultAsync(x => x.IssueKey == issueId);

        if (profile is null)
        {
            profile = await MigrateLegacyProfileAsync(db, issueId, legacyIssueKey);
        }

        if (profile is null) return;

        var prompts = DeserializePrompts(profile.Prompts);
        if (!prompts.Remove(key)) return;

        profile.Prompts = SerializePrompts(prompts);
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    // =======================================================================
    // Helpers
    // =======================================================================

    private static string SerializeDefinition(WorkflowDefinition def) =>
        JsonSerializer.Serialize(def, WorkflowYamlSerializer.JsonOptions);

    private static async Task<IssueWorkflowProfile?> FindProfileAsync(
        MohistDbContext db,
        string issueId,
        string? legacyIssueKey)
    {
        var row = await db.IssueWorkflowProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.IssueKey == issueId);
        if (row is not null || string.IsNullOrWhiteSpace(legacyIssueKey))
            return row;

        return await db.IssueWorkflowProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.IssueKey == legacyIssueKey);
    }

    private static async Task<IssueWorkflowProfile?> MigrateLegacyProfileAsync(
        MohistDbContext db,
        string issueId,
        string? legacyIssueKey)
    {
        if (string.IsNullOrWhiteSpace(legacyIssueKey)) return null;

        var legacy = await db.IssueWorkflowProfiles
            .FirstOrDefaultAsync(x => x.IssueKey == legacyIssueKey);
        if (legacy is null) return null;

        var migrated = new IssueWorkflowProfile
        {
            IssueKey = issueId,
            SourceTemplateId = legacy.SourceTemplateId,
            Template = legacy.Template,
            Variables = legacy.Variables,
            Prompts = legacy.Prompts,
            UpdatedAt = legacy.UpdatedAt,
        };
        db.IssueWorkflowProfiles.Remove(legacy);
        db.IssueWorkflowProfiles.Add(migrated);
        return migrated;
    }

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

    private static Dictionary<string, string> DeserializePrompts(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new(StringComparer.Ordinal);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                ?? new(StringComparer.Ordinal);
        }
        catch
        {
            return new(StringComparer.Ordinal);
        }
    }

    private static string SerializePrompts(Dictionary<string, string> prompts) =>
        JsonSerializer.Serialize(prompts);

    private static string SerializePrompt(string key, string body) =>
        JsonSerializer.Serialize(new Dictionary<string, string> { [key] = body });
}

/// <summary>
/// Request body for IssueWorkflowProfileManager.UpdateTemplateAsync.
/// Only one of ProjectTemplateId / Template may be set; both null clears issue-level override.
/// </summary>
public sealed record IssueTemplateUpdateRequest(
    string? ProjectTemplateId = null,
    string? Template = null);

public sealed record IssueWorkflowProfileState(
    string IssueKey,
    string? SourceTemplateId,
    bool HasCustomTemplate,
    WorkflowDefinition? Template,
    VariableBundle Variables,
    DateTimeOffset? UpdatedAt);
