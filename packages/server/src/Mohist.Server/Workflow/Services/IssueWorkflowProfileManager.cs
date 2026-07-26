using System.Text.Json;
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
/// Issue-scope template + variables write endpoint.
/// 
/// UpdateTemplateAsync 入参:
///   ProjectTemplateId: 指向项目模板 (引用, SourceTemplateId 设置, Template 清空)
///   Template:          自定义 YAML 字符串 (Template 设置, SourceTemplateId 清空)
///   两个都 null:       清空 issue 级模板 (继承项目默认)
/// </summary>
public class IssueWorkflowProfileManager : IScopedService, IAgentRuntimeOverrideResolver
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IActionCatalogSource _catalogSource;

    public IssueWorkflowProfileManager(
        IDbContextFactory<MohistDbContext> dbFactory,
        IActionCatalogSource catalogSource)
    {
        _dbFactory = dbFactory;
        _catalogSource = catalogSource;
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
            return WorkflowProfilePersistence.Deserialize(row.Template).Definition;
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
                  string.IsNullOrWhiteSpace(row.Template) ? null : WorkflowProfilePersistence.Deserialize(row.Template),
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
    public async Task<IssueTemplateUpdateResult> UpdateTemplateAsync(
        string projectId,
        int issueNumber,
        IssueTemplateUpdateRequest request)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));

        if (!string.IsNullOrWhiteSpace(request.ProjectTemplateId) && !string.IsNullOrWhiteSpace(request.Template))
            throw new InvalidOperationException("Cannot set both ProjectTemplateId and custom Template at the same time");

        WorkflowProfile? parsedProfile = null;
        ActionValidationStatus actionValidation = ActionValidationStatus.Skipped;
        if (!string.IsNullOrWhiteSpace(request.Template))
        {
            var catalog = await _catalogSource.GetCatalogAsync();
            actionValidation = catalog is null
                ? ActionValidationStatus.Skipped
                : ActionValidationStatus.Performed;
            parsedProfile = WorkflowProfileYamlParser.Parse(request.Template, CustomProfileId(projectId, issueNumber), catalog);
        }

        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.IssueWorkflowProfiles
            .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.IssueNumber == issueNumber);

        if (row is null)
        {
            row = CreateProfile(projectId, issueNumber);
            row.SourceTemplateId = request.ProjectTemplateId;
            row.Template = parsedProfile is null ? null : WorkflowProfilePersistence.Serialize(parsedProfile);
            row.Variables = VariableBundle.Empty.ToJson();
            row.UpdatedAt = DateTimeOffset.UtcNow;
            db.IssueWorkflowProfiles.Add(row);
        }
        else
        {
            row.SourceTemplateId = request.ProjectTemplateId;
            row.Template = parsedProfile is null ? null : WorkflowProfilePersistence.Serialize(parsedProfile);
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync();
        return new IssueTemplateUpdateResult(ToState(row), actionValidation);
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

    public async Task<string?> GetAgentRuntimeOverrideAsync(string projectId, int issueNumber)
    {
        var variables = await GetVariablesAsync(projectId, issueNumber);
        if (variables.Vars is not { ValueKind: JsonValueKind.Object } vars
            || !vars.TryGetProperty("agent", out var agent)
            || agent.ValueKind != JsonValueKind.Object
            || !agent.TryGetProperty("runtime", out var runtime)
            || runtime.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = runtime.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public async Task<VariableBundle> SetVariablesAsync(string projectId, int issueNumber, VariableBundle bundle)
    {
        VariableBundleShapeValidator.Validate(bundle);
        ValidateAgentRuntimes(bundle);

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
        VariableBundleShapeValidator.Validate(patch);
        var current = await GetVariablesAsync(projectId, issueNumber);
        var merged = VariableBundle.Patch(current, patch);
        return await SetVariablesAsync(projectId, issueNumber, merged);
    }

    // =======================================================================
    // Helpers
    // =======================================================================

    private static string CustomProfileId(string projectId, int issueNumber) =>
        $"issue-custom:{projectId}#{issueNumber}";

    private static void ValidateAgentRuntimes(VariableBundle bundle)
    {
        ValidateAgentRuntime(bundle.Vars, "vars.agent.runtime");
        if (bundle.Stages is null) return;

        foreach (var (stage, stageVariables) in bundle.Stages)
            ValidateAgentRuntime(stageVariables.Vars, $"stages.{stage}.vars.agent.runtime");
    }

    private static void ValidateAgentRuntime(JsonElement? vars, string path)
    {
        if (vars is not { ValueKind: JsonValueKind.Object }
            || !vars.Value.TryGetProperty("agent", out var agent)
            || agent.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var error = AgentConfigSchema.ValidateRuntime(agent);
        if (error is not null)
            throw new ArgumentException(error.Replace("agentConfig.runtime", path, StringComparison.Ordinal));
    }

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

    private static IssueWorkflowProfileState ToState(IssueWorkflowProfile row) =>
        new(
            row.ProjectId,
            row.IssueNumber,
            row.SourceTemplateId,
            !string.IsNullOrWhiteSpace(row.Template),
            string.IsNullOrWhiteSpace(row.Template) ? null : WorkflowProfilePersistence.Deserialize(row.Template),
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
    WorkflowProfile? Template,
    VariableBundle Variables,
    DateTimeOffset? UpdatedAt);

public sealed record IssueTemplateUpdateResult(
    IssueWorkflowProfileState State,
    ActionValidationStatus ActionValidation);
