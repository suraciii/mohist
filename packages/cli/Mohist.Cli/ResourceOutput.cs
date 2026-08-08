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
    private static readonly IReadOnlyList<string> AgentFields =
        ["id", "projectId", "name", "avatar", "description", "instructions", "agentConfig", "skills", "allowedSubagentAgentIds", "maxConcurrentRuns", "status", "createdAt", "updatedAt", "readiness"];

    public static ResourceDescriptor For(string? tableShape)
    {
        if (!Enum.TryParse<MohistCliApi.TableShape>(tableShape, ignoreCase: false, out var shape))
            throw new ArgumentException($"Unknown resource output shape '{tableShape}'.", nameof(tableShape));

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
            MohistCliApi.TableShape.Models or
            MohistCliApi.TableShape.RunList or
            MohistCliApi.TableShape.ActivityList or
            MohistCliApi.TableShape.AgentJobList or
            MohistCliApi.TableShape.SessionList or
            MohistCliApi.TableShape.SessionScheduleList or
            MohistCliApi.TableShape.OtelTracesList or
            MohistCliApi.TableShape.WebhookSubscriptionList or
            MohistCliApi.TableShape.WebhookDeliveryFailureList or
            MohistCliApi.TableShape.WorkspaceList => ResourceCardinality.Collection,
            _ => ResourceCardinality.Single,
        };

        IReadOnlyList<string> fields = shape switch
        {
            MohistCliApi.TableShape.ProjectList or MohistCliApi.TableShape.Project => ["id", "name", "createdAt", "updatedAt", "repositories", "variables", "defaultRepository"],
            MohistCliApi.TableShape.WorkflowStatus => ["issueNumber", "title", "stage", "runtimeStatus", "workflowRunId", "changeDir", "workspacePath", "workflow"],
            MohistCliApi.TableShape.EpicList => ["projectId", "number", "title", "description", "priority", "status", "createdAt", "updatedAt", "progress", "pauseReason"],
            MohistCliApi.TableShape.EpicShow => ["projectId", "number", "title", "description", "priority", "status", "createdAt", "updatedAt", "linkedIssues", "progress", "nextIssueNumber", "nextIssueReason", "pauseReason"],
            MohistCliApi.TableShape.EpicLink or MohistCliApi.TableShape.EpicUnlink => ["identifier", "status", "issueNumber", "owningEpicNumber", "owningEpicTitle"],
            MohistCliApi.TableShape.IssueList => ["number", "title", "status", "health", "projectId", "projectName", "labels", "priority", "risk", "createdAt", "updatedAt", "archivedAt", "completedAt", "approvalState", "blockedReason", "workflowRunId", "workflowStage", "workflowStatus", "workflowStageProgress", "workflowProfileId", "prerequisiteNumbers", "prereq", "isDraft", "canStart", "canBeParent", "blocker", "repositoryName", "repository", "repositoryProblem", "epic", "parentIssueRef", "childIssuesSummary", "children", "watching", "muted"],
            MohistCliApi.TableShape.Issue => ["number", "title", "body", "status", "health", "projectId", "projectName", "labels", "priority", "risk", "model", "modelVariant", "agentConfig", "stageModels", "stageModelVariants", "createdAt", "updatedAt", "archivedAt", "completedAt", "approvalState", "blockedReason", "attention", "workflowRunId", "workflowStage", "workflowStatus", "workflowStageProgress", "workflowProfileId", "workflowProfileMode", "prerequisiteNumbers", "comments", "attachments", "prereq", "isDraft", "canStart", "canBeParent", "blocker", "repositoryName", "repository", "repositoryProblem", "epic", "parentIssueRef", "childIssuesSummary", "children", "feedback", "watching", "muted"],
            MohistCliApi.TableShape.RepoList => ["name", "gitUrl", "baseBranch", "isDefault", "resolvedBaseBranch"],
            MohistCliApi.TableShape.FeedbackList or MohistCliApi.TableShape.FeedbackShow =>
                ["id", "workflowRunId", "stage", "body", "status", "createdAt", "resolution", "issueNumber"],
            MohistCliApi.TableShape.CommentShow => ["id", "projectId", "issueNumber", "body", "createdAt", "attachments", "author", "displayName"],
            MohistCliApi.TableShape.LabelList => ["key", "description", "supportedValues"],
            MohistCliApi.TableShape.AgentList or
            MohistCliApi.TableShape.AgentShow => AgentFields,
            MohistCliApi.TableShape.Sessions => ["id", "workflowRunId", "sessionName", "runtimeSessionId", "runtime", "projectId", "issueNumber", "runnerId", "activity", "stage", "model", "workDir", "processPid", "createdAt", "startedAt", "lastDataAt", "completedAt", "failureReason", "exitCode", "eventSummary", "usage"],
            MohistCliApi.TableShape.SessionMetadata => ["id", "sessionName", "runtimeSessionId", "runtime", "activity", "model", "stage", "title", "createdAt", "completedAt", "eventSummary", "usage", "metadata", "currentTurnId", "inputs", "turns"],
            MohistCliApi.TableShape.SessionTranscriptSummary => ["turns", "partCount", "lastActivityAt"],
            MohistCliApi.TableShape.AgentSessionList =>
                ["sessionId", "agentId", "agentName", "activity", "createdAt", "lastActivityAt", "resolvedModel", "origin", "targetId", "contextRefs"],
            MohistCliApi.TableShape.AgentSessionShow =>
                ["sessionId", "agentId", "agentName", "runtimeSessionId", "runtime", "activity", "createdAt", "lastActivityAt", "resolvedModel", "failureCategory", "failureReason", "toolCallCount", "toolErrorCount", "origin", "targetId", "contextRefs", "usage", "recoveryAvailable", "currentTurnId", "inputs", "turns"],
            MohistCliApi.TableShape.AgentSessionTranscript => ["turns", "partCount", "lastActivityAt"],
            MohistCliApi.TableShape.AgentSessionLaunch => ["jobId", "sessionId", "inputId", "turnId", "agentId", "agentName", "workspaceId", "targetId", "origin", "status", "attachments", "rejectedAttachments", "sessionUrl", "transcriptUrl", "jobUrl", "observationUrl"],
            MohistCliApi.TableShape.AgentSessionSpawn => ["jobId", "sessionId", "turnId", "parentSessionId", "edgeId"],
            MohistCliApi.TableShape.AgentJobList => ["jobId", "agentId", "agentName", "status", "submittedAt", "terminalAt"],
            MohistCliApi.TableShape.AgentJobView => ["jobId", "status", "message", "output", "artifactUploadIds", "failureReason", "exitCode", "executionDefinition"],
            MohistCliApi.TableShape.AgentSessionFollowup => ["sessionId", "status", "inputId", "turnId", "inputAcceptance", "turnStatus", "error", "code", "attachments", "rejectedAttachments"],
            MohistCliApi.TableShape.AgentSessionCancel => ["state", "interruptUnconfirmed"],
            MohistCliApi.TableShape.SessionList =>
                ["id", "source", "runtimeSessionId", "runtime", "activity", "createdAt", "lastActivityAt", "model", "agentId", "agentName", "workflowRunId", "sessionName", "origin", "targetId", "contextRefs"],
            MohistCliApi.TableShape.SessionShow =>
                ["id", "source", "runtimeSessionId", "runtime", "activity", "createdAt", "lastActivityAt", "model", "resolvedModel", "failureCategory", "failureReason", "toolCallCount", "toolErrorCount", "agentId", "agentName", "workflowRunId", "sessionName", "origin", "targetId", "contextRefs", "usage", "recoveryAvailable", "currentTurnId", "inputs", "turns", "recoveryHistory"],
            MohistCliApi.TableShape.SessionTree => ["root", "revision", "nodes", "edges", "continuation"],
            MohistCliApi.TableShape.SessionTranscript => ["turns", "partCount", "lastActivityAt"],
            MohistCliApi.TableShape.SessionFollowup => ["sessionId", "status", "inputId", "turnId", "inputAcceptance", "turnStatus", "error", "code", "attachments", "rejectedAttachments"],
            MohistCliApi.TableShape.SessionCancel => ["state", "interruptUnconfirmed"],
            MohistCliApi.TableShape.SessionStop => ["operationId", "rootSessionId", "status", "admissionFenceActive", "graphRevision", "membership", "targets"],
            MohistCliApi.TableShape.SessionDetach => ["state", "childSessionId", "parentSessionId", "edgeId", "childLaunchJobId", "attachedRevision", "detachedRevision", "historic", "reason"],
            MohistCliApi.TableShape.SessionScheduleCreate or
            MohistCliApi.TableShape.SessionScheduleList or
            MohistCliApi.TableShape.SessionScheduleCancel =>
                ["scheduleId", "status", "dueAt", "text", "inputId", "createdAt", "idempotencyKey", "cancelledAt"],
            MohistCliApi.TableShape.SessionRecovery =>
                ["id", "status", "contextWindowSize", "contextWindowUsed", "contextUsagePercent", "contextWindowUsedBefore", "operation", "wasCompacted"],
            MohistCliApi.TableShape.IssueTemplateList => ["id", "name", "description", "source"],
            MohistCliApi.TableShape.IssueTemplateShow => ["id", "name", "description", "body", "source"],
            MohistCliApi.TableShape.ProjectTemplateList => ["projectId", "templateId", "createdAt", "updatedAt", "name", "description"],
            MohistCliApi.TableShape.ProjectTemplateShow => ["projectId", "profileId", "name", "description", "sourceProvenance", "isBuiltIn", "definitionSource"],
            MohistCliApi.TableShape.ProjectWorkflowProfile => ["projectId", "profileId"],
            MohistCliApi.TableShape.WorkflowProfilePrompt => ["key", "displayName", "description", "tags", "stage", "body", "source"],
            MohistCliApi.TableShape.WorkflowProfilePreview => ["rendered", "missingVariables", "depth", "errors"],
            MohistCliApi.TableShape.WorkflowRunDetail => ["status", "issueRef"],
            MohistCliApi.TableShape.WorkflowApproval => ["workflowRunId", "approved"],
            MohistCliApi.TableShape.RunList => ["id", "status", "stage", "currentStage", "issueNumber"],
            MohistCliApi.TableShape.WorkflowRunEvents => ["id", "eventId", "source", "type", "specVersion", "subject", "time", "dataContentType", "data", "extensions"],
            MohistCliApi.TableShape.WorkflowRunVariables => ["key", "value"],
            MohistCliApi.TableShape.WorkflowVariables => ["vars", "stages"],
            MohistCliApi.TableShape.WorkflowProfile => ["issueNumber", "projectId", "sourceTemplateId", "hasCustomTemplate", "yaml", "workflowRunId", "profileId", "updateMode", "variables", "updatedAt", "templateSource"],
            MohistCliApi.TableShape.WorkflowProfileList => ["projectId", "profileId", "name", "description", "sourceProvenance", "isBuiltIn", "definitionSource"],
            MohistCliApi.TableShape.RoutingRule or MohistCliApi.TableShape.RoutingRuleList => ["id", "projectId", "name", "position", "match", "agentId", "responsePrompt", "continue", "status", "createdAt", "updatedAt"],
            MohistCliApi.TableShape.WebhookSubscription or MohistCliApi.TableShape.WebhookSubscriptionList =>
                ["id", "projectId", "name", "match", "targetUrl", "status", "eventSelectionMode", "eventTypes", "authType", "hasSecret", "createdAt", "updatedAt"],
            MohistCliApi.TableShape.WebhookDeliveryFailureList =>
                ["id", "projectId", "subscriptionId", "eventId", "eventType", "targetUrl", "responseStatus", "durationMs", "errorSummary", "occurredAt"],
            MohistCliApi.TableShape.WorkspaceList or
            MohistCliApi.TableShape.WorkspaceShow =>
                ["projectId", "name", "origin", "repositories", "status", "home", "createdAt", "archivedAt", "boundSessionCount", "sessions"],
            MohistCliApi.TableShape.DeadLetterList => ["id", "origin", "sourceId", "source", "eventId", "type", "time", "subject", "dataContentType", "data", "extensions", "handler", "error", "attempts", "deadLetteredAt", "status", "redeliveryAttemptedAt"],
            MohistCliApi.TableShape.DeadLetterRedelivery => ["id", "delivered", "attempts", "error"],
            MohistCliApi.TableShape.ActivityList => ["id", "provenance", "scope", "kind", "time", "title", "description", "eventType", "issueNumber", "workflowRunId", "sessionId", "runnerId", "status"],
            MohistCliApi.TableShape.IssueWatchList => ["number", "watching", "muted"],
            MohistCliApi.TableShape.IssueArchiveCompleted => ["archived", "skipped", "skippedNumbers", "message"],
            MohistCliApi.TableShape.Models => ["id"],
            MohistCliApi.TableShape.RunnerList or MohistCliApi.TableShape.RunnerShow =>
                ["id", "kind", "hostname", "scope", "status", "registeredAt", "lastHeartbeatAt", "connectionState", "capabilities", "coderModels", "coderModelCount", "capacity", "activeWorks", "buildGitHash"],
            MohistCliApi.TableShape.SystemInfo => ["running", "source", "install", "update", "services", "paths", "degraded", "cliVersion"],
            MohistCliApi.TableShape.OtelTracesList => ["trace_id", "service_name", "start_time", "end_time", "span_count"],
            _ => throw new ArgumentOutOfRangeException(nameof(tableShape), shape, "Resource output shape has no field catalog."),
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
