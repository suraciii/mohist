using System.Text.Json;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Queries;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Issue.WorkflowProfiles;

public class MohistDefaultIssueWorkflowProfile : IIssueWorkflowProfile
{
    private readonly Workflow.Prompts.IPromptLoader _promptLoader;

    public MohistDefaultIssueWorkflowProfile(Workflow.Prompts.IPromptLoader promptLoader)
    {
        _promptLoader = promptLoader;
    }

    public string Id => IssueWorkflowProfiles.DefaultId;
    public string DisplayName => "Mohist Default";
    public string Description => "Plan, build, check, and integrate an issue using OpenSpec artifacts.";
    public bool IsDefault => true;
    public WorkflowDefinition Definition => MohistWorkflow.Definition;

    public Dictionary<string, string> LoadPrompts() => _promptLoader.LoadAll();

    public string BuildVariables(string workflowRunId, Domain.Issue issue, WorkflowProjectContext project, Dictionary<string, object?>? globalAgentConfig = null)
    {
        var agentConfig = BuildAgentConfig(globalAgentConfig);
        var prompts = _promptLoader.LoadAll();

        var variables = new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
        {
            ["mohist"] = JsonSerializer.SerializeToElement(new { system = "mohist", runId = workflowRunId }, WorkflowVariableJson.Options),
            ["issue"] = JsonSerializer.SerializeToElement(new { id = issue.Id, number = issue.Number, title = issue.Title, body = issue.Body ?? "" }, WorkflowVariableJson.Options),
            ["project"] = JsonSerializer.SerializeToElement(new { id = project.Id, name = project.Name, path = project.Path, baseBranch = project.BaseBranch, defaultBranch = project.BaseBranch }, WorkflowVariableJson.Options),
            ["openspecChangeName"] = JsonSerializer.SerializeToElement(MohistDefaultWorkflowProjection.ChangeName(issue.Number), WorkflowVariableJson.Options),
            ["openspecChangeDir"] = JsonSerializer.SerializeToElement(MohistDefaultWorkflowProjection.ChangeDir(issue.Number), WorkflowVariableJson.Options),
            ["vars"] = JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["agent"] = agentConfig }, WorkflowVariableJson.Options),
            ["prompts"] = JsonSerializer.SerializeToElement(prompts, WorkflowVariableJson.Options),
        };
        return JsonSerializer.Serialize(variables, WorkflowVariableJson.Options);
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
        var agentConfig = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "opencode",
        };
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

        sections["vars"] = sections.TryGetValue("vars", out var existingVars)
            ? MergeVarsJson(existingVars, agentConfig)
            : JsonSerializer.Serialize(new Dictionary<string, object?> { ["agent"] = agentConfig }, WorkflowVariableJson.Options);
    }

    private static string MergeVarsJson(string existingJson, Dictionary<string, object?> agentConfig)
    {
        try
        {
            var vars = JsonSerializer.Deserialize<Dictionary<string, object?>>(existingJson, WorkflowVariableJson.Options)
                ?? new Dictionary<string, object?>(StringComparer.Ordinal);
            var existingAgent = vars.TryGetValue("agent", out var value)
                ? NormalizeJsonValue(value) as Dictionary<string, object?>
                : null;
            existingAgent = existingAgent is null
                ? new Dictionary<string, object?>(StringComparer.Ordinal)
                : new Dictionary<string, object?>(existingAgent, StringComparer.Ordinal);
            MergeAgentConfig(existingAgent, agentConfig);
            vars["agent"] = existingAgent;
            return JsonSerializer.Serialize(vars, WorkflowVariableJson.Options);
        }
        catch
        {
            return JsonSerializer.Serialize(new Dictionary<string, object?> { ["agent"] = agentConfig }, WorkflowVariableJson.Options);
        }
    }

    private static void MergeAgentConfig(Dictionary<string, object?> target, Dictionary<string, object?>? source)
    {
        if (source is null) return;
        foreach (var (key, value) in source)
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
            JsonValueKind.Object => JsonSerializer.Deserialize<Dictionary<string, object?>>(element.GetRawText(), WorkflowVariableJson.Options),
            JsonValueKind.Array => JsonSerializer.Deserialize<object?[]>(element.GetRawText(), WorkflowVariableJson.Options),
            _ => null,
        },
        _ => value,
    };

    public MohistDefaultWorkflowState ProjectWorkflowState(Domain.Issue issue, WorkflowStatusSnapshot? workflow) =>
        MohistDefaultWorkflowProjection.ProjectWorkflowState(
            issue.Number,
            issue.Title,
            IssueDomainNames.Stage(issue.Stage),
            issue.Attention,
            issue.BlockedReason,
            workflow);

    public MohistDefaultWorkflowState ProjectWorkflowState(IssueReadModel issue, WorkflowStatusSnapshot? workflow) =>
        MohistDefaultWorkflowProjection.ProjectWorkflowState(
            issue.Number,
            issue.Title,
            issue.Stage,
            issue.Attention,
            issue.BlockedReason,
            workflow);
}
