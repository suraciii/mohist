using System.Text.Json;
using System.Text.Json.Serialization;
using Mohist.Server.Issue.Grains;

namespace Mohist.Server.Workflow.Grains;

[GenerateSerializer]
public sealed record WorkflowExecutionContext(
    [property: Id(0)] MohistContext Mohist,
    [property: Id(1)] WorkflowIssueVariables Issue,
    [property: Id(2)] WorkflowProjectVariables Project,
    [property: Id(3)] WorkflowArtifactVariables Artifacts,
    [property: Id(4)] WorkflowModelVariables Model,
    [property: Id(5)] WorkflowUserVariables Vars)
{
    public static WorkflowExecutionContext FromIssue(string workflowRunId, WorkflowIssueContext context, WorkflowIssueSeed issue) => new(
        new MohistContext("mohist", workflowRunId),
        new WorkflowIssueVariables(context.IssueId, context.IssueNumber, issue.Title, issue.Body),
        new WorkflowProjectVariables(context.ProjectId, context.ProjectName, context.ProjectPath, context.BaseBranch, context.BaseBranch),
        new WorkflowArtifactVariables($"openspec/changes/{context.IssueNumber}-{Slug(issue.Title)}"),
        new WorkflowModelVariables(issue.Model ?? "", issue.StageModels ?? []),
        WorkflowUserVariables.Empty());

    public string ToDispatchJson(WorkflowDispatchContext dispatch)
    {
        var payload = new
        {
            mohist = Mohist,
            issue = Issue,
            project = Project,
            artifacts = Artifacts,
            model = Model,
            vars = Vars.Values,
            workflow = new { runId = dispatch.WorkflowRunId },
            stage = new { name = dispatch.Stage },
            work = new { id = dispatch.WorkId, type = dispatch.WorkType, title = dispatch.Title, attempt = dispatch.Attempt }
        };

        return JsonSerializer.Serialize(payload, WorkflowVariableJson.Options);
    }

    private static string Slug(string value)
    {
        var chars = value.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(slug) ? "issue" : slug;
    }
}

[GenerateSerializer]
public sealed record MohistContext(
    [property: Id(0)] string System,
    [property: Id(1)] string RunId);

[GenerateSerializer]
public sealed record WorkflowIssueVariables(
    [property: Id(0)] string Id,
    [property: Id(1)] int Number,
    [property: Id(2)] string Title,
    [property: Id(3)] string Body);

[GenerateSerializer]
public sealed record WorkflowProjectVariables(
    [property: Id(0)] string Id,
    [property: Id(1)] string Name,
    [property: Id(2)] string Path,
    [property: Id(3)] string BaseBranch,
    [property: Id(4)] string DefaultBranch);

[GenerateSerializer]
public sealed record WorkflowArtifactVariables(
    [property: Id(0)] string ChangeDir);

[GenerateSerializer]
public sealed record WorkflowModelVariables(
    [property: Id(0)] string Default,
    [property: Id(1)] Dictionary<string, string> Stage);

[GenerateSerializer]
public sealed record WorkflowUserVariables(
    [property: Id(0)] Dictionary<string, string> Values)
{
    public static WorkflowUserVariables Empty() => new([]);
}

[GenerateSerializer]
public sealed record WorkflowDispatchContext(
    [property: Id(0)] string WorkflowRunId,
    [property: Id(1)] string WorkId,
    [property: Id(2)] string WorkType,
    [property: Id(3)] string? Stage,
    [property: Id(4)] string? Title,
    [property: Id(5)] int Attempt);

internal static class WorkflowVariableJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
