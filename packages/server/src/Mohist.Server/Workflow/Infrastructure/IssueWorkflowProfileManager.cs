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
///   ProjectTemplateId: 指向项目模板 (引用, SourceTemplateId 设置, TemplateJson 清空)
///   Template:          自定义 YAML 字符串 (TemplateJson 设置, SourceTemplateId 清空)
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
        if (!string.IsNullOrWhiteSpace(row.TemplateJson))
            return DeserializeDefinition(row.TemplateJson);
        // SourceTemplateId case - caller should resolve via ProjectWorkflowProfileManager.GetTemplateAsync
        return null;
    }

    public async Task<IssueWorkflowProfileRow?> GetProfileAsync(string issueId, string? legacyIssueKey = null)
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
                !string.IsNullOrWhiteSpace(row.TemplateJson),
                string.IsNullOrWhiteSpace(row.TemplateJson) ? null : DeserializeDefinition(row.TemplateJson),
                VariableBundle.FromJson(row.VariablesJson),
                row.UpdatedAt);
    }

    /// <summary>
    /// Update issue template choice.
    /// - request.ProjectTemplateId set:  reference a project template, clear custom
    /// - request.Template set:           upload custom YAML, clear reference
    /// - both null:                      clear issue-level template (inherit project default)
    /// - both set:                       invalid
    /// </summary>
    public async Task<IssueWorkflowProfileRow> UpdateTemplateAsync(
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
            row = await MigrateLegacyRowAsync(db, issueId, legacyIssueKey);
        }

        if (row is null)
        {
            row = new IssueWorkflowProfileRow
            {
                IssueKey = issueId,
                SourceTemplateId = request.ProjectTemplateId,
                TemplateJson = parsed is null ? null : SerializeDefinition(parsed),
                VariablesJson = VariableBundle.Empty.ToJson(),
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.IssueWorkflowProfiles.Add(row);
        }
        else
        {
            row.SourceTemplateId = request.ProjectTemplateId;
            row.TemplateJson = parsed is null ? null : SerializeDefinition(parsed);
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
        return row is null ? VariableBundle.Empty : VariableBundle.FromJson(row.VariablesJson);
    }

    public async Task<VariableBundle> SetVariablesAsync(string issueId, VariableBundle bundle, string? legacyIssueKey = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.IssueWorkflowProfiles
            .FirstOrDefaultAsync(x => x.IssueKey == issueId);

        if (row is null)
        {
            row = await MigrateLegacyRowAsync(db, issueId, legacyIssueKey);
        }

        if (row is null)
        {
            row = new IssueWorkflowProfileRow
            {
                IssueKey = issueId,
                VariablesJson = bundle.ToJson(),
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.IssueWorkflowProfiles.Add(row);
        }
        else
        {
            row.VariablesJson = bundle.ToJson();
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
    // Helpers
    // =======================================================================

    private static string SerializeDefinition(WorkflowDefinition def) =>
        JsonSerializer.Serialize(def, WorkflowYamlSerializer.JsonOptions);

    private static async Task<IssueWorkflowProfileRow?> FindProfileAsync(
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

    private static async Task<IssueWorkflowProfileRow?> MigrateLegacyRowAsync(
        MohistDbContext db,
        string issueId,
        string? legacyIssueKey)
    {
        if (string.IsNullOrWhiteSpace(legacyIssueKey)) return null;

        var legacy = await db.IssueWorkflowProfiles
            .FirstOrDefaultAsync(x => x.IssueKey == legacyIssueKey);
        if (legacy is null) return null;

        var migrated = new IssueWorkflowProfileRow
        {
            IssueKey = issueId,
            SourceTemplateId = legacy.SourceTemplateId,
            TemplateJson = legacy.TemplateJson,
            VariablesJson = legacy.VariablesJson,
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
