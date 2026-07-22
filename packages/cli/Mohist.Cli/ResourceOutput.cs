using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal enum ResourceCardinality
{
    Single,
    Collection,
    Stream,
}

internal sealed record ResourceDescriptor(
    ResourceCardinality Cardinality,
    IReadOnlyList<string> Fields);

internal static class ResourceOutputCatalog
{
    public static ResourceDescriptor For(string? tableShape)
    {
        if (!Enum.TryParse<MohistCliApi.TableShape>(tableShape, ignoreCase: false, out var shape))
            return new(ResourceCardinality.Single, ["id", "name", "description", "status", "createdAt", "updatedAt"]);

        var cardinality = shape switch
        {
            MohistCliApi.TableShape.ProjectList or
            MohistCliApi.TableShape.IssueList or
            MohistCliApi.TableShape.RepoList or
            MohistCliApi.TableShape.FeedbackList or
            MohistCliApi.TableShape.LabelList or
            MohistCliApi.TableShape.AgentList or
            MohistCliApi.TableShape.EpicList or
            MohistCliApi.TableShape.Sessions or
            MohistCliApi.TableShape.AgentSessionList or
            MohistCliApi.TableShape.WorkflowProfileList or
            MohistCliApi.TableShape.RunnerList or
            MohistCliApi.TableShape.WorkflowRunEvents or
            MohistCliApi.TableShape.WorkflowRunVariables or
            MohistCliApi.TableShape.WorkflowVariables or
            MohistCliApi.TableShape.ProjectTemplateList or
            MohistCliApi.TableShape.IssueTemplateList or
            MohistCliApi.TableShape.RoutingRuleList or
            MohistCliApi.TableShape.DeadLetterList or
            MohistCliApi.TableShape.OpencodeModels or
            MohistCliApi.TableShape.RunList => ResourceCardinality.Collection,
            _ => ResourceCardinality.Single,
        };

        IReadOnlyList<string> fields = shape switch
        {
            MohistCliApi.TableShape.ProjectList => ["id", "name", "repository", "createdAt", "updatedAt"],
            MohistCliApi.TableShape.ProjectShow => ["id", "name", "repository", "workflowProfile", "createdAt", "updatedAt"],
            MohistCliApi.TableShape.RepoList => ["name", "gitUrl", "baseBranch", "isDefault", "createdAt", "updatedAt"],
            MohistCliApi.TableShape.FeedbackList or MohistCliApi.TableShape.FeedbackShow =>
                ["id", "issueNumber", "workflowRunId", "stage", "status", "body", "createdAt", "resolution", "updatedAt"],
            MohistCliApi.TableShape.CommentShow => ["id", "issueNumber", "author", "body", "createdAt", "updatedAt"],
            MohistCliApi.TableShape.LabelList => ["key", "description", "supportedValues"],
            MohistCliApi.TableShape.AgentList => ["id", "name", "description", "status", "archived", "createdAt", "updatedAt"],
            MohistCliApi.TableShape.AgentShow => ["id", "name", "description", "instructions", "agentConfig", "skills", "maxConcurrentRuns", "createdAt", "updatedAt"],
            MohistCliApi.TableShape.Sessions => ["id", "sessionName", "status", "createdAt", "model"],
            MohistCliApi.TableShape.SessionMetadata => ["id", "sessionName", "status", "model", "stage", "createdAt", "usage", "metadata"],
            MohistCliApi.TableShape.SessionTranscriptSummary => ["turns", "partCount", "firstActivityAt", "lastActivityAt"],
            MohistCliApi.TableShape.AgentSessionList =>
                ["sessionId", "agentId", "agentName", "status", "createdAt", "lastActivityAt", "resolvedModel", "failureReason", "failureCategory"],
            MohistCliApi.TableShape.AgentSessionShow =>
                ["sessionId", "agentId", "agentName", "status", "createdAt", "lastActivityAt", "resolvedModel", "failureReason", "failureCategory", "toolCallCount", "toolErrorCount", "contextRefs", "usage"],
            MohistCliApi.TableShape.AgentSessionTranscript => ["turns", "partCount", "firstActivityAt", "lastActivityAt"],
            MohistCliApi.TableShape.AgentSessionLaunch => ["sessionId", "agentId", "agentName", "status"],
            MohistCliApi.TableShape.AgentSessionFollowup => ["status"],
            MohistCliApi.TableShape.AgentSessionCancel => ["state", "status", "reason"],
            MohistCliApi.TableShape.SessionRecovery =>
                ["id", "status", "contextWindowSize", "contextWindowUsed", "contextUsagePercent", "contextWindowUsedBefore", "operation", "wasCompacted"],
            MohistCliApi.TableShape.IssueTemplateList => ["id", "name", "description", "source"],
            MohistCliApi.TableShape.IssueTemplateShow => ["id", "name", "description", "body", "source"],
            MohistCliApi.TableShape.ProjectTemplateList => ["projectId", "templateId", "createdAt", "updatedAt"],
            MohistCliApi.TableShape.ProjectTemplateShow => ["projectId", "templateId", "definition", "createdAt", "updatedAt"],
            MohistCliApi.TableShape.ProjectWorkflowProfile => ["defaultTemplateId", "variables", "prompts"],
            MohistCliApi.TableShape.WorkflowRunDetail => ["status", "issueRef"],
            MohistCliApi.TableShape.WorkflowApproval => ["workflowRunId", "approved"],
            MohistCliApi.TableShape.RunList => ["id", "status", "stage", "currentStage", "issueNumber"],
            MohistCliApi.TableShape.WorkflowRunEvents => ["id", "type", "source", "subject", "time", "data"],
            MohistCliApi.TableShape.WorkflowVariables => ["vars", "stages"],
            MohistCliApi.TableShape.WorkflowProfile =>
                ["issueNumber", "projectId", "sourceTemplateId", "hasCustomTemplate", "yaml", "workflowRunId", "profileId", "updateMode", "templateSource", "variables", "updatedAt"],
            MohistCliApi.TableShape.WorkflowProfileList => ["id", "name", "displayName", "description", "isDefault"],
            MohistCliApi.TableShape.RoutingRule or MohistCliApi.TableShape.RoutingRuleList => ["id", "name", "target", "priority", "enabled", "createdAt", "updatedAt"],
            MohistCliApi.TableShape.DeadLetterList or MohistCliApi.TableShape.DeadLetterRedelivery => ["id", "eventId", "handler", "attempts", "status", "createdAt", "updatedAt"],
            MohistCliApi.TableShape.OpencodeModels => ["id"],
            MohistCliApi.TableShape.RunnerList =>
                ["id", "kind", "hostname", "scope", "status", "registeredAt", "lastHeartbeatAt", "connectionState", "capabilities", "coderModels", "coderModelCount", "capacity", "activeWork", "activeWorks"],
            MohistCliApi.TableShape.RunnerShow =>
                ["id", "kind", "hostname", "scope", "status", "registeredAt", "lastHeartbeatAt", "connectionState", "capabilities", "coderModels", "coderModelCount", "capacity", "buildGitHash", "activeWorks"],
            MohistCliApi.TableShape.SystemInfo => ["running", "source", "install", "update", "services", "paths", "degraded", "cliVersion"],
            _ => ["id", "number", "name", "title", "description", "status", "state", "stage", "priority", "labels", "createdAt", "updatedAt"],
        };

        return new(cardinality, fields);
    }
}

internal enum JsonSelectionKind
{
    None,
    Discovery,
    Selected,
    Invalid,
}

internal sealed record JsonSelection(JsonSelectionKind Kind, IReadOnlyList<string> Fields, string? InvalidField)
{
    public static JsonSelection Parse(ResourceDescriptor descriptor, bool provided, string? value)
    {
        if (!provided)
            return new(JsonSelectionKind.None, [], null);
        if (value is null)
            return new(JsonSelectionKind.Discovery, descriptor.Fields, null);

        var fields = value.Split(',', StringSplitOptions.None);
        var selected = new List<string>(fields.Length);
        foreach (var raw in fields)
        {
            var field = raw.Trim();
            if (field.Length == 0 || !descriptor.Fields.Contains(field, StringComparer.Ordinal))
                return new(JsonSelectionKind.Invalid, [], field.Length == 0 ? raw : field);
            if (selected.Contains(field, StringComparer.Ordinal))
                return new(JsonSelectionKind.Invalid, [], field);
            selected.Add(field);
        }

        return new(JsonSelectionKind.Selected, selected, null);
    }

    public JsonNode Project(JsonNode? data, ResourceCardinality cardinality)
    {
        return cardinality switch
        {
            ResourceCardinality.Single => ProjectObject(data as JsonObject),
            ResourceCardinality.Collection => ProjectCollection(data as JsonArray),
            ResourceCardinality.Stream => ProjectObject(data as JsonObject),
            _ => throw new InvalidOperationException("Unknown resource cardinality"),
        };
    }

    private JsonObject ProjectObject(JsonObject? source)
    {
        if (source is null)
            throw new InvalidOperationException("The server returned a non-object resource");
        var result = new JsonObject();
        foreach (var field in Fields)
            result[field] = source[field]?.DeepClone();
        return result;
    }

    private JsonArray ProjectCollection(JsonArray? source)
    {
        if (source is null)
            throw new InvalidOperationException("The server returned a non-array collection");
        var result = new JsonArray();
        foreach (var item in source)
            result.Add(ProjectObject(item as JsonObject));
        return result;
    }
}
