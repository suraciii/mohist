using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Infrastructure.Data.Workflow;

namespace Mohist.Server.Workflow.Services;

/// <summary>
/// Issue-scope template + variables write endpoint.
/// 
/// UpdateTemplateAsync 入参:
///   ProjectTemplateId: 指向项目模板 (引用, SourceTemplateId 设置, Template 清空)
///   Template:          自定义 YAML 字符串 (Template 设置, SourceTemplateId 清空)
///   两个都 null:       清空 issue 级模板 (继承项目默认)
/// </summary>
public class IssueWorkflowProfileManager : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public IssueWorkflowProfileManager(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    // =======================================================================
    // Template
    // =======================================================================

    public async Task<WorkflowDefinition?> GetTemplateAsync(string projectId, int issueNumber)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await FindProfileAsync(db, projectId, issueNumber);
        if (row is null) return null;
        if (!string.IsNullOrWhiteSpace(row.Template))
            return DeserializeDefinition(row.Template);
        // SourceTemplateId case - caller should resolve via ProjectWorkflowProfileManager.GetTemplateAsync
        return null;
    }

    public async Task<IssueWorkflowProfileState> GetStateAsync(string projectId, int issueNumber)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await FindProfileAsync(db, projectId, issueNumber);

        return row is null
            ? new IssueWorkflowProfileState(projectId, issueNumber, null, false, null, VariableBundle.Empty, null)
            : new IssueWorkflowProfileState(
                row.ProjectId,
                row.IssueNumber,
                row.SourceTemplateId,
                !string.IsNullOrWhiteSpace(row.Template),
                string.IsNullOrWhiteSpace(row.Template) ? null : DeserializeDefinition(row.Template),
                VariableBundle.FromJson(row.Variables),
                row.UpdatedAt);
    }

    internal async Task<IssueWorkflowProfile?> GetProfileAsync(string projectId, int issueNumber)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await FindProfileAsync(db, projectId, issueNumber);
    }

    /// <summary>
    /// Update issue template choice.
    /// - request.ProjectTemplateId set:  reference a project template, clear custom
    /// - request.Template set:           upload custom YAML, clear reference
    /// - both null:                      clear issue-level template (inherit project default)
    /// - both set:                       invalid
    /// </summary>
    public async Task<IssueWorkflowProfileState> UpdateTemplateAsync(
        string projectId,
        int issueNumber,
        IssueTemplateUpdateRequest request)
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
            .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.IssueNumber == issueNumber);

        if (row is null)
        {
            row = CreateProfile(projectId, issueNumber);
            row.SourceTemplateId = request.ProjectTemplateId;
            row.Template = parsed is null ? null : SerializeDefinition(parsed);
            row.Variables = VariableBundle.Empty.ToJson();
            row.UpdatedAt = DateTimeOffset.UtcNow;
            db.IssueWorkflowProfiles.Add(row);
        }
        else
        {
            row.SourceTemplateId = request.ProjectTemplateId;
            row.Template = parsed is null ? null : SerializeDefinition(parsed);
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync();
        return ToState(row);
    }

    // =======================================================================
    // Variables (Set + Patch)
    // =======================================================================

    public async Task<VariableBundle> GetVariablesAsync(string projectId, int issueNumber)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await FindProfileAsync(db, projectId, issueNumber);
        return row is null ? VariableBundle.Empty : VariableBundle.FromJson(row.Variables);
    }

    public async Task<VariableBundle> SetVariablesAsync(string projectId, int issueNumber, VariableBundle bundle)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.IssueWorkflowProfiles
            .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.IssueNumber == issueNumber);

        if (row is null)
        {
            row = CreateProfile(projectId, issueNumber);
            row.Variables = bundle.ToJson();
            row.UpdatedAt = DateTimeOffset.UtcNow;
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

    public async Task<VariableBundle> PatchVariablesAsync(string projectId, int issueNumber, VariableBundle patch)
    {
        var current = await GetVariablesAsync(projectId, issueNumber);
        var merged = VariableBundle.Patch(current, patch);
        return await SetVariablesAsync(projectId, issueNumber, merged);
    }

    // =======================================================================
    // Prompts
    // =======================================================================

    public async Task<Dictionary<string, string>> GetPromptsAsync(string projectId, int issueNumber)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var profile = await FindProfileAsync(db, projectId, issueNumber);
        return profile?.Prompts ?? new(StringComparer.Ordinal);
    }

    public async Task SetPromptAsync(string projectId, int issueNumber, string key, string body)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("key is required", nameof(key));

        await using var db = await _dbFactory.CreateDbContextAsync();
        var profile = await db.IssueWorkflowProfiles
            .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.IssueNumber == issueNumber);

        if (profile is null)
        {
            profile = CreateProfile(projectId, issueNumber);
            profile.Variables = VariableBundle.Empty.ToJson();
            profile.Prompts = new Dictionary<string, string>(StringComparer.Ordinal) { [key] = body };
            profile.UpdatedAt = DateTimeOffset.UtcNow;
            db.IssueWorkflowProfiles.Add(profile);
        }
        else
        {
            profile.Prompts[key] = body;
            profile.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync();
    }

    public async Task DeletePromptAsync(string projectId, int issueNumber, string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("key is required", nameof(key));

        await using var db = await _dbFactory.CreateDbContextAsync();
        var profile = await db.IssueWorkflowProfiles
            .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.IssueNumber == issueNumber);

        if (profile is null) return;

        if (!profile.Prompts.Remove(key)) return;
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
        string projectId,
        int issueNumber) =>
        await db.IssueWorkflowProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.IssueNumber == issueNumber);

    private static IssueWorkflowProfile CreateProfile(string projectId, int issueNumber)
    {
        return new IssueWorkflowProfile
        {
            ProjectId = projectId,
            IssueNumber = issueNumber,
        };
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

    private static IssueWorkflowProfileState ToState(IssueWorkflowProfile row) =>
        new(
            row.ProjectId,
            row.IssueNumber,
            row.SourceTemplateId,
            !string.IsNullOrWhiteSpace(row.Template),
            string.IsNullOrWhiteSpace(row.Template) ? null : DeserializeDefinition(row.Template),
            VariableBundle.FromJson(row.Variables),
            row.UpdatedAt);
}

/// <summary>
/// Request body for IssueWorkflowProfileManager.UpdateTemplateAsync.
/// Only one of ProjectTemplateId / Template may be set; both null clears issue-level override.
/// </summary>
public sealed record IssueTemplateUpdateRequest(
    string? ProjectTemplateId = null,
    string? Template = null);

public sealed record IssueWorkflowProfileState(
    string ProjectId,
    int IssueNumber,
    string? SourceTemplateId,
    bool HasCustomTemplate,
    WorkflowDefinition? Template,
    VariableBundle Variables,
    DateTimeOffset? UpdatedAt);
