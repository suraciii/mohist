using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Workspace;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Issue.Services.WorkflowProfiles;

public abstract class MohistIssueWorkflowProfileBase : IIssueWorkflowProfile
{
    private readonly ProjectPromptStore _promptStore;

    protected MohistIssueWorkflowProfileBase(
        ProjectPromptStore promptStore)
    {
        _promptStore = promptStore;
    }

    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract string Description { get; }
    public abstract bool IsDefault { get; }
    public virtual WorkflowDefinition Definition => WorkflowProfileCatalog.Definition;

    public async Task<Dictionary<string, string>> GetMergedPromptsAsync(string projectId)
        => await _promptStore.GetMergedPromptBodiesAsync(projectId);

    public string BuildVariables(string workflowRunId, Domain.Issue issue, WorkflowProjectContext project, Dictionary<string, object?>? globalAgentConfig = null)
    {
        var agentConfig = BuildAgentConfig(globalAgentConfig);
        var prompts = GetMergedPromptsAsync(issue.ProjectId).GetAwaiter().GetResult();

        var workspace = IssueVariableBuilder.BuildWorkspaceIdentity(
            workflowRunId,
            issue,
            project,
            MohistWorkspaceLayout.DefaultRunnerRoot());
        var variables = IssueVariableBuilder.BuildRootVariables(
            workflowRunId,
            issue,
            project,
            workspace,
            agentConfig,
            prompts);
        return JSON.Serialize(variables);
    }

    public Dictionary<string, Dictionary<string, string>>? BuildStageVariables(Domain.Issue issue, Dictionary<string, Dictionary<string, object?>>? globalStageAgentConfigs = null)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        if (globalStageAgentConfigs is not null)
        {
            foreach (var (stage, config) in globalStageAgentConfigs)
                MergeStageAgent(result, stage, config);
        }

        return result.Count == 0 ? null : result;
    }

    private static Dictionary<string, object?> BuildAgentConfig(Dictionary<string, object?>? globalAgentConfig)
    {
        var agentConfig = new Dictionary<string, object?>(StringComparer.Ordinal);
        MergeAgentConfig(agentConfig, globalAgentConfig);
        return agentConfig;
    }

    private static void MergeStageAgent(Dictionary<string, Dictionary<string, string>> stageVariables, string stage, Dictionary<string, object?> agentConfig)
    {
        if (!stageVariables.TryGetValue(stage, out var sections))
        {
            sections = new Dictionary<string, string>(StringComparer.Ordinal);
            stageVariables[stage] = sections;
        }

        // project the incoming per-stage agent
        // config down to the converged {model, variant} whitelist before
        // writing into the stage vars. Legacy runtime/liveness keys supplied
        // by callers MUST NOT enter stages.<stage>.vars.agent.
        var filteredAgent = AgentConfigSchema.Filter(agentConfig);
        if (filteredAgent is null)
            return;

        sections["vars"] = sections.TryGetValue("vars", out var existingVars)
            ? MergeVarsJson(existingVars, filteredAgent)
            : JSON.Serialize(new Dictionary<string, object?> { ["agent"] = filteredAgent });
    }

    private static string MergeVarsJson(string existingJson, Dictionary<string, object?> agentConfig)
    {
        try
        {
            var vars = JsonSerializer.Deserialize<Dictionary<string, object?>>(existingJson, JSON.Options)
                ?? new Dictionary<string, object?>(StringComparer.Ordinal);
            var existingAgent = vars.TryGetValue("agent", out var value)
                ? NormalizeJsonValue(value) as Dictionary<string, object?>
                : null;
            existingAgent = existingAgent is null
                ? new Dictionary<string, object?>(StringComparer.Ordinal)
                : new Dictionary<string, object?>(existingAgent, StringComparer.Ordinal);
            MergeAgentConfig(existingAgent, agentConfig);
            vars["agent"] = existingAgent;
            return JSON.Serialize(vars);
        }
        catch
        {
            return JSON.Serialize(new Dictionary<string, object?> { ["agent"] = agentConfig });
        }
    }

    private static void MergeAgentConfig(Dictionary<string, object?> target, Dictionary<string, object?>? source)
    {
        if (source is null) return;
        var filtered = AgentConfigSchema.Filter(source);
        if (filtered is null) return;
        foreach (var (key, value) in filtered)
        {
            if (value is null) continue;
            target[key] = NormalizeJsonValue(value);
        }
    }

    private static object? NormalizeJsonValue(object? value) => value switch
    {
        JsonElement element => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt32(out var n) => n,
            JsonValueKind.Number when element.TryGetDouble(out var d) => d,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Object => JsonSerializer.Deserialize<Dictionary<string, object?>>(element.GetRawText(), JSON.Options),
            JsonValueKind.Array => JsonSerializer.Deserialize<object?[]>(element.GetRawText(), JSON.Options),
            _ => null,
        },
        _ => value,
    };

    public MohistDefaultWorkflowState ProjectWorkflowState(Domain.Issue issue, WorkflowStatusView? workflow) =>
        MohistDefaultWorkflowProjection.ProjectWorkflowState(
            issue.Number,
            issue.Title,
            issue.Status,
            workflow);

    public MohistDefaultWorkflowState ProjectWorkflowState(IssueReadModel issue, WorkflowStatusView? workflow) =>
        MohistDefaultWorkflowProjection.ProjectWorkflowState(
            issue.Number,
            issue.Title,
            issue.Status,
            workflow);
}
