using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal sealed partial class TableRenderer
{
    private void RenderIssueList(JsonNode? data)
    {
        var rows = AsArray(data);
        var headers = new[] { "number", "title", "repository", "stage", "status", "priority", "state", "labels" };
        var widths = new[] { 7, TitleSoftCap, 20, 16, 12, 9, TitleSoftCap, TitleSoftCap };

        var cells = new List<string[]>();
        foreach (var row in rows)
        {
            var number = NumberOf(row, "number");
            var title = StringOf(row, "title");
            var repository = StringOf(row, "repositoryName");
            var stage = StringOf(row, "workflowStage");
            var status = StringOf(row, "status");
            var priority = StringOf(row, "priority");
            var state = FormatIssueState(row);
            var labels = FormatLabels(row?["labels"]);
            cells.Add(new[]
            {
                number,
                Truncate(title, TitleSoftCap),
                Truncate(repository, 20),
                Truncate(stage, 16),
                Truncate(status, 12),
                Truncate(priority, 9),
                Truncate(state, TitleSoftCap),
                Truncate(labels, TitleSoftCap),
            });
        }

        WriteTable(headers, widths, cells);
    }

    private void RenderIssueShow(JsonNode? data)
    {
        if (data is null)
        {
            _out.WriteLine("");
            return;
        }

        var number = NumberOf(data, "number");
        var title = StringOf(data, "title");
        var stage = StringOf(data, "workflowStage");
        var status = StringOf(data, "status");
        var priority = StringOf(data, "priority");
        var repository = StringOf(data, "repositoryName");
        var project = StringOf(data, "projectName");
        if (string.IsNullOrEmpty(project))
            project = StringOf(data, "projectId");
        var updatedAt = StringOf(data, "updatedAt");
        var body = StringOf(data, "body");
        var workflowRunId = StringOf(data, "workflowRunId");
        var labels = FormatLabels(data["labels"]);

        _out.WriteLine($"number:   {number}");
        _out.WriteLine($"title:    {Truncate(title, TitleSoftCap)}");
        _out.WriteLine($"stage:    {stage}");
        _out.WriteLine($"status:   {status}");
        _out.WriteLine($"priority: {priority}");
        _out.WriteLine($"repository: {repository}");
        _out.WriteLine($"project:  {project}");
        _out.WriteLine($"updated:  {Truncate(updatedAt, TitleSoftCap)}");
        if (!string.IsNullOrEmpty(workflowRunId))
            _out.WriteLine($"workflowRunId: {workflowRunId}");
        _out.WriteLine($"labels:   {labels}");
        var workflowProfileId = StringOf(data, "workflowProfileId");
        if (!string.IsNullOrEmpty(workflowProfileId))
            _out.WriteLine($"profile:  {workflowProfileId}");
        if (!string.IsNullOrEmpty(body))
            _out.WriteLine($"body:     {Truncate(body, BodySoftCap)}");
        var parentRef = data["parentIssueRef"] as JsonObject;
        if (parentRef is not null)
        {
            var parentNumber = NumberOf(parentRef, "number");
            var parentTitle = StringOf(parentRef, "title");
            _out.WriteLine($"parent:   #{parentNumber} {parentTitle}");
        }
        var childSummary = data["childIssuesSummary"] as JsonObject;
        if (childSummary is not null)
        {
            var hasChildren = BoolOf(childSummary, "hasChildren");
            if (hasChildren)
            {
                var countText = NumberOf(childSummary, "count");
                var count = int.TryParse(countText, out var parsedCount) ? parsedCount : 0;
                _out.WriteLine($"parent:   is a parent ({count} child issue{(count == 1 ? "" : "s")})");
                var backlog = NumberOf(childSummary, "backlogCount");
                var inProgress = NumberOf(childSummary, "inProgressCount");
                var done = NumberOf(childSummary, "doneCount");
                var cancelled = NumberOf(childSummary, "cancelledCount");
                _out.WriteLine($"children: {done} done / {inProgress} in-progress / {cancelled} cancelled / {backlog} backlog / {count} total");
            }
        }
        _out.WriteLine($"state:    {FormatIssueState(data)}");
    }

    internal static string FormatIssueState(JsonNode? data)
    {
        if (data is null) return "";
        var isDraft = BoolOf(data, "isDraft");
        if (isDraft) return "draft";
        var blocker = data["blocker"];
        if (blocker is JsonObject blockerObj)
        {
            var kind = blockerObj["kind"]?.GetValue<string>();
            if (kind == "draft") return "draft";
            if (kind == "waiting-for")
            {
                var issue = blockerObj["issue"] as JsonObject;
                var blockedNumber = issue?["number"]?.GetValue<int?>();
                if (blockedNumber is int n) return $"Waiting for #{n}";
                return "Waiting for prerequisite";
            }
        }
        var canStart = BoolOf(data, "canStart");
        if (canStart) return "ready";
        return "";
    }

    private void RenderWorkflowStatus(JsonNode? data)
    {
        if (data is null)
        {
            _out.WriteLine("");
            return;
        }

        var workflow = data["workflow"] as JsonObject;
        var currentStage = StringOf(workflow, "currentStage");
        var status = StringOf(workflow, "status");
        var stages = workflow?["stages"] as JsonArray;
        var failure = workflow?["failure"] as JsonObject;

        _out.WriteLine($"current stage: {currentStage}");
        _out.WriteLine($"status:        {status}");

        RenderDeliveryFailure(failure);

        if (stages is null)
            return;

        _out.WriteLine("");
        var headers = new[] { "stage", "status", "tasks", "waiting" };
        var widths = new[] { 16, 12, 6, TitleSoftCap };
        var cells = new List<string[]>();
        foreach (var stageNode in stages)
        {
            if (stageNode is not JsonObject stageObj) continue;
            var stageName = StringOf(stageObj, "stage");
            var stageStatus = StringOf(stageObj, "status");
            var tasks = stageObj["tasks"] as JsonArray;
            var taskStates = tasks is null
                ? ""
                : string.Join(",", tasks.Select(t => t is JsonObject to ? StringOf(to, "status") : ""));
            var approval = stageObj["approvalStatus"] as JsonObject;
            var waiting = approval is null ? "" : StringOf(approval, "result");
            cells.Add(new[]
            {
                Truncate(stageName, 16),
                Truncate(stageStatus, 12),
                Truncate(taskStates, 6),
                Truncate(waiting, TitleSoftCap),
            });
        }
        WriteTable(headers, widths, cells);
    }

    private void RenderDeliveryFailure(JsonNode? failureNode)
    {
        if (failureNode is not JsonObject failure) return;
        var error = failure["error"] as JsonObject;
        var message = StringOf(error, "message");
        var output = failure["output"] as JsonNode;
        var kind = StringOf(error, "code");
        var guidance = DeliveryFailureGuidance.ResolveGuidance(kind);
        if (string.IsNullOrEmpty(kind) || guidance is null) return;
        var evidence = DeliveryFailureGuidance.ResolveBranchEvidence(message, output);
        var workspaceEvidence = DeliveryFailureGuidance.ResolveWorkspaceEvidence(message, output);

        _out.WriteLine("");
        _out.WriteLine("delivery failure:");
        _out.WriteLine($"  kind:       {kind}");
        _out.WriteLine($"  label:      {guidance.Value.Label}");
        _out.WriteLine($"  next action: {guidance.Value.NextAction}");
        if (string.Equals(kind, DeliveryFailureGuidance.BranchInvariantViolation, StringComparison.OrdinalIgnoreCase) && evidence is not null)
        {
            _out.WriteLine("  attribution: runner/action (not issue work)");
            if (!string.IsNullOrEmpty(evidence.Boundary)) _out.WriteLine($"  boundary:   {evidence.Boundary}");
            if (!string.IsNullOrEmpty(evidence.ExpectedBranch)) _out.WriteLine($"  expected:   {evidence.ExpectedBranch}");
            if (!string.IsNullOrEmpty(evidence.ObservedBranch)) _out.WriteLine($"  observed:   {evidence.ObservedBranch}");
            else if (!string.IsNullOrEmpty(evidence.ObservedRef)) _out.WriteLine($"  observed:   (detached at {evidence.ObservedRef})");
        }
        else if (DeliveryFailureGuidance.IsWorkspaceSetupKind(kind) && workspaceEvidence is not null)
        {
            _out.WriteLine("  attribution: workflow infrastructure (not issue work)");
            if (!string.IsNullOrEmpty(workspaceEvidence.WorkspacePath)) _out.WriteLine($"  workspace:  {workspaceEvidence.WorkspacePath}");
        }
    }

    private void RenderSessions(JsonNode? data)
    {
        var rows = AsArray(data);
        var headers = new[] { "id", "state", "started", "model" };
        var widths = new[] { IdSoftCap, 14, 24, TitleSoftCap };

        var cells = new List<string[]>();
        foreach (var row in rows)
        {
            var id = StringOf(row, "sessionName");
            if (string.IsNullOrEmpty(id))
                id = StringOf(row, "id");
            var state = StringOf(row, "status");
            var started = StringOf(row, "createdAt");
            var model = StringOf(row, "model");
            cells.Add(new[]
            {
                Truncate(id, IdSoftCap),
                Truncate(state, 14),
                Truncate(started, 24),
                Truncate(model, TitleSoftCap),
            });
        }

        WriteTable(headers, widths, cells);
    }

    private void RenderFeedbackList(JsonNode? data)
    {
        var rows = AsArray(data);
        var headers = new[] { "id", "stage", "status", "createdAt", "body" };
        var widths = new[] { IdSoftCap, 12, 12, 24, TitleSoftCap };

        var cells = new List<string[]>();
        foreach (var row in rows)
        {
            var id = StringOf(row, "id");
            var stage = StringOf(row, "stage");
            var status = StringOf(row, "status");
            var createdAt = StringOf(row, "createdAt");
            var body = StringOf(row, "body");
            cells.Add(new[]
            {
                Truncate(id, IdSoftCap),
                Truncate(stage, 12),
                Truncate(status, 12),
                Truncate(createdAt, 24),
                Truncate(body, TitleSoftCap),
            });
        }

        WriteTable(headers, widths, cells);
    }

    private void RenderFeedbackShow(JsonNode? data)
    {
        if (data is null)
        {
            _out.WriteLine("");
            return;
        }

        var id = StringOf(data, "id");
        var issueNumber = NumberOf(data, "issueNumber");
        var workflowRunId = StringOf(data, "workflowRunId");
        var stage = StringOf(data, "stage");
        var status = StringOf(data, "status");
        var body = StringOf(data, "body");
        var createdAt = StringOf(data, "createdAt");
        var resolutionTaskId = StringOf(data, "resolutionTaskId");
        var resolvedAt = StringOf(data, "resolvedAt");
        var resolutionSummary = StringOf(data, "resolutionSummary");
        var resolution = data["resolution"] as JsonObject;

        _out.WriteLine($"id:               {id}");
        _out.WriteLine($"issue:            {issueNumber}");
        _out.WriteLine($"workflow run:     {Truncate(workflowRunId, TitleSoftCap)}");
        _out.WriteLine($"stage:            {stage}");
        _out.WriteLine($"status:           {status}");
        _out.WriteLine($"created:          {Truncate(createdAt, TitleSoftCap)}");
        if (!string.IsNullOrEmpty(resolutionTaskId))
            _out.WriteLine($"resolution task:  {Truncate(resolutionTaskId, TitleSoftCap)}");
        if (!string.IsNullOrEmpty(resolvedAt))
            _out.WriteLine($"resolved at:      {Truncate(resolvedAt, TitleSoftCap)}");
        if (!string.IsNullOrEmpty(resolutionSummary))
            _out.WriteLine($"resolution:       {Truncate(resolutionSummary, TitleSoftCap)}");
        if (resolution is not null)
        {
            var rSummary = StringOf(resolution, "resolutionSummary");
            var rResolvedAt = StringOf(resolution, "resolvedAt");
            var rTaskId = StringOf(resolution, "resolutionTaskId");
            if (!string.IsNullOrEmpty(rSummary))
                _out.WriteLine($"resolution:       {Truncate(rSummary, TitleSoftCap)}");
            if (!string.IsNullOrEmpty(rResolvedAt))
                _out.WriteLine($"resolved at:      {Truncate(rResolvedAt, TitleSoftCap)}");
            if (!string.IsNullOrEmpty(rTaskId))
                _out.WriteLine($"resolution task:  {Truncate(rTaskId, TitleSoftCap)}");
        }
        if (!string.IsNullOrEmpty(body))
            _out.WriteLine($"body:");
        if (!string.IsNullOrEmpty(body))
            WriteBody(body);
    }

    private void RenderCommentShow(JsonNode? data)
    {
        if (data is null)
        {
            _out.WriteLine("");
            return;
        }

        _out.WriteLine($"id:      {StringOf(data, "id")}");
        _out.WriteLine($"author:  {Truncate(StringOf(data, "author"), 100)}");
        _out.WriteLine($"body:    {Truncate(StringOf(data, "body"), BodySoftCap)}");
    }

    private void WriteBody(string body)
    {
        foreach (var line in body.Split('\n'))
            _out.WriteLine($"  {line}");
    }

    private void RenderSessionMetadata(JsonNode? data)
    {
        if (data is null)
        {
            _out.WriteLine("");
            return;
        }

        var name = StringOf(data, "sessionName");
        var status = StringOf(data, "status");
        var model = StringOf(data, "model");
        var stage = StringOf(data, "stage");
        var createdAt = StringOf(data, "createdAt");
        var metadata = data["metadata"] as JsonObject;
        var partCount = NumberOf(metadata, "partCount");
        var toolCount = NumberOf(metadata, "toolCount");
        var usage = data["usage"] as JsonObject;
        var inputTokens = NumberOf(usage, "inputTokens");
        var outputTokens = NumberOf(usage, "outputTokens");
        var totalTokens = NumberOf(usage, "totalTokens");
        var contextWindowUsed = NumberOf(usage, "contextWindowUsed");
        var contextWindowSize = NumberOf(usage, "contextWindowSize");
        var contextUsagePercent = NumberOf(usage, "contextUsagePercent");
        var healthStatus = StringOf(usage, "healthStatus");
        var tokenUsage = FormatTokenUsage(inputTokens, outputTokens, totalTokens);

        _out.WriteLine($"name:      {name}");
        _out.WriteLine($"status:    {status}");
        _out.WriteLine($"model:     {model}");
        _out.WriteLine($"stage:     {stage}");
        _out.WriteLine($"created:   {createdAt}");
        _out.WriteLine($"parts:     {partCount}");
        _out.WriteLine($"tools:     {toolCount}");
        _out.WriteLine($"tokens:    {tokenUsage}");
        _out.WriteLine($"context:   {contextWindowUsed}/{contextWindowSize} ({contextUsagePercent})");
        _out.WriteLine($"health:    {healthStatus}");
    }

    private static string FormatTokenUsage(string inputTokens, string outputTokens, string totalTokens)
    {
        if (!string.IsNullOrEmpty(totalTokens))
        {
            if (!string.IsNullOrEmpty(inputTokens) || !string.IsNullOrEmpty(outputTokens))
                return $"{totalTokens} (input {inputTokens}, output {outputTokens})";
            return totalTokens;
        }

        if (!string.IsNullOrEmpty(inputTokens) || !string.IsNullOrEmpty(outputTokens))
            return $"input {inputTokens}, output {outputTokens}";

        return "";
    }

    private void RenderSessionTranscriptSummary(JsonNode? data)
    {
        if (data is null)
        {
            _out.WriteLine("");
            return;
        }

        var turns = data["turns"] as JsonArray ?? new JsonArray();
        var turnCount = turns.Count.ToString();
        var partCount = NumberOf(data, "partCount");
        var firstActivity = turns.Count > 0 ? StringOf(turns[0], "startedAt") : "";
        var lastActivity = StringOf(data, "lastActivityAt");

        _out.WriteLine($"turns:          {turnCount}");
        _out.WriteLine($"parts:          {partCount}");
        _out.WriteLine($"first activity: {firstActivity}");
        _out.WriteLine($"last activity:  {lastActivity}");
    }

    private void RenderSessionRecovery(JsonNode? data)
    {
        if (data is null)
        {
            _out.WriteLine("");
            return;
        }

        var sessionId = StringOf(data, "id");
        var operation = StringOf(data, "operation");
        var wasCompacted = BoolOf(data, "wasCompacted") ? "true" : "false";
        var contextWindowUsedBefore = NumberOf(data, "contextWindowUsedBefore");
        var contextWindowUsed = NumberOf(data, "contextWindowUsed");
        var contextUsagePercent = NumberOf(data, "contextUsagePercent");
        var status = StringOf(data, "status");

        _out.WriteLine($"session id: {sessionId}");
        _out.WriteLine($"operation:   {operation}");
        _out.WriteLine($"compacted:   {wasCompacted}");
        _out.WriteLine($"context:     {contextWindowUsedBefore} → {contextWindowUsed} ({contextUsagePercent})");
        _out.WriteLine($"status:      {status}");
    }

    private void RenderIssueArchiveCompleted(JsonNode? data)
    {
        if (data is null)
        {
            _out.WriteLine("");
            return;
        }

        var message = StringOf(data, "message");
        var archived = NumberOf(data, "archived");
        var skipped = NumberOf(data, "skipped");

        if (!string.IsNullOrEmpty(message))
        {
            _out.WriteLine(message);
        }
        else
        {
            _out.WriteLine($"archived: {archived}");
            _out.WriteLine($"skipped:  {skipped}");
        }
    }

    // Renders the full WorkflowRunDetailDto payload from
    // GET /api/workflow-runs/{workflowRunId}: composes the WorkflowStatusView
    // (under `status`) with the associated-issue reference (under `issueRef`).
    // This is the table shape for `mo workflow get <runId> -o table` (and its
    // transitional `show` alias). The default table output is itself the summary
    // view (status, stage progress, approval state, associated issue), so a
    // separate compact subset for an old `status` command is no longer needed
    // — see workflow-run-reads/spec.md#the-redundant-status-command-is-removed.
    private void RenderWorkflowRunDetail(JsonNode? data)
    {
        if (data is null)
        {
            _out.WriteLine("");
            return;
        }

        var status = data["status"] as JsonObject;
        var issueRef = data["issueRef"] as JsonObject;

        var runId = StringOf(status, "workflowRunId");
        var runStatus = StringOf(status, "status");
        var currentStage = StringOf(status, "currentStage");
        var assignedTo = StringOf(status, "assignedTo");

        _out.WriteLine($"run id:        {runId}");
        _out.WriteLine($"status:        {runStatus}");
        _out.WriteLine($"current stage: {currentStage}");
        if (!string.IsNullOrEmpty(assignedTo))
            _out.WriteLine($"assigned to:   {assignedTo}");

        if (issueRef is not null)
        {
            var issueNumber = NumberOf(issueRef, "number");
            var issueTitle = StringOf(issueRef, "title");
            _out.WriteLine($"issue:         #{issueNumber} {issueTitle}");
        }
        else
        {
            _out.WriteLine("issue:         (none)");
        }

        RenderWorkflowRunMetadata(status);
        RenderWorkflowRunFailure(status?["failure"]);

        var stages = status?["stages"] as JsonArray;
        if (stages is not null)
        {
            RenderWorkflowRunStages(stages);
        }
    }

    private void RenderWorkflowRunMetadata(JsonNode? status)
    {
        if (status is null) return;
        var metadata = status["metadata"] as JsonObject;
        if (metadata is null) return;

        var name = StringOf(metadata, "name");
        var createdAt = StringOf(metadata, "createdAt");
        if (!string.IsNullOrEmpty(name))
            _out.WriteLine($"name:          {name}");
        if (!string.IsNullOrEmpty(createdAt))
            _out.WriteLine($"created:       {createdAt}");

        var labels = metadata["labels"] as JsonObject;
        if (labels is not null && labels.Count > 0)
        {
            var joined = string.Join(", ", labels.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key}={kv.Value}"));
            _out.WriteLine($"labels:        {joined}");
        }
    }

    // Surface a run-level failure (reason + message + failing stage/task/check)
    // when present. Stage-level failures are surfaced through the stage table's
    // individual rows instead of being printed here — keeping this helper
    // scoped to the run-level failure field of WorkflowStatusView.
    private void RenderWorkflowRunFailure(JsonNode? failureNode)
    {
        if (failureNode is not JsonObject failure) return;
        var reason = StringOf(failure, "reason");
        var message = StringOf(failure, "message");
        var stage = StringOf(failure, "stage");
        var taskId = StringOf(failure, "taskId");
        var checkName = StringOf(failure, "checkName");

        if (string.IsNullOrEmpty(reason)
            && string.IsNullOrEmpty(message)
            && string.IsNullOrEmpty(stage)
            && string.IsNullOrEmpty(taskId)
            && string.IsNullOrEmpty(checkName))
            return;

        _out.WriteLine("");
        _out.WriteLine("failure:");
        if (!string.IsNullOrEmpty(reason))
            _out.WriteLine($"  reason:    {reason}");
        if (!string.IsNullOrEmpty(message))
            _out.WriteLine($"  message:   {message}");
        if (!string.IsNullOrEmpty(stage))
            _out.WriteLine($"  stage:     {stage}");
        if (!string.IsNullOrEmpty(taskId))
            _out.WriteLine($"  task id:   {taskId}");
        if (!string.IsNullOrEmpty(checkName))
            _out.WriteLine($"  check:     {checkName}");
    }

    private void RenderWorkflowRunStages(JsonArray stages)
    {
        if (stages.Count == 0) return;

        _out.WriteLine("");
        var headers = new[] { "stage", "status", "tasks", "approval" };
        var widths = new[] { 16, 12, 8, TitleSoftCap };
        var cells = new List<string[]>();
        foreach (var stageNode in stages)
        {
            if (stageNode is not JsonObject stageObj) continue;
            var stageName = StringOf(stageObj, "stage");
            var stageStatus = StringOf(stageObj, "status");
            var tasks = stageObj["tasks"] as JsonArray;
            var taskStates = tasks is null
                ? ""
                : string.Join(",", tasks.Select(t => t is JsonObject to ? StringOf(to, "status") : ""));
            var approval = stageObj["approvalStatus"] as JsonObject;
            var waiting = approval is null ? "" : StringOf(approval, "result");
            cells.Add(new[]
            {
                Truncate(stageName, 16),
                Truncate(stageStatus, 12),
                Truncate(taskStates, 8),
                Truncate(waiting, TitleSoftCap),
            });
        }
        WriteTable(headers, widths, cells);
    }

    // Renders the effective-variables payload from
    // GET /api/workflow-runs/{id}/variables/effective[/{keyPath}].
    // The response is a flat object of merged variable values (top-level +
    // stage overrides resolved by the server). For `mo workflow variables
    // --key <path>`, the response is the single value at that path — printed
    // as one line. For the unscoped / --stage-scoped calls, the response is
    // an object we render as a key → value list (nested objects shown as a
    // single JSON line per top-level key). The endpoint is the run-scoped
    // effective-variables subresource (own resource path), distinct from the
    // WorkflowVariables shape used by the issue-scoped config bundle.
    private void RenderWorkflowRunVariables(JsonNode? data)
    {
        if (data is null)
        {
            _out.WriteLine("");
            return;
        }

        if (data is JsonValue value)
        {
            if (value.TryGetValue<string>(out var s))
                _out.WriteLine(s);
            else
                _out.WriteLine(data.ToJsonString(MohistCliApi.JsonOutputOptions));
            return;
        }

        if (data is JsonArray array)
        {
            _out.WriteLine(data.ToJsonString(MohistCliApi.JsonOutputOptions));
            return;
        }

        if (data is not JsonObject obj || obj.Count == 0)
        {
            _out.WriteLine("(no variables)");
            return;
        }

        var headers = new[] { "key", "value" };
        var widths = new[] { 24, TitleSoftCap };
        var cells = new List<string[]>();
        foreach (var kvp in obj.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var display = kvp.Value is JsonValue jv
                ? (jv.TryGetValue<string>(out var s) ? s : jv.ToJsonString())
                : kvp.Value?.ToJsonString(MohistCliApi.JsonOutputOptions) ?? "<null>";
            cells.Add(new[]
            {
                Truncate(kvp.Key, widths[0]),
                Truncate(display, widths[1]),
            });
        }
        WriteTable(headers, widths, cells);
    }

    // Renders the CloudEvent stream associated with the run (a list of
    // CloudEvent envelopes). For each event, we surface type, source, time,
    // and subject — enough to scan the stream without dumping the full data
    // payload. The endpoint is the run-scoped associated-resource equivalent
    // of `mo issue events`; both are read against the same event store.
    private void RenderWorkflowRunEvents(JsonNode? data)
    {
        var rows = AsArray(data);
        if (rows.Count == 0)
        {
            _out.WriteLine("No events");
            return;
        }

        var headers = new[] { "type", "source", "time", "subject" };
        var widths = new[] { IdSoftCap, IdSoftCap, 28, TitleSoftCap };
        var cells = new List<string[]>();
        foreach (var row in rows)
        {
            if (row is not JsonObject obj) continue;
            var type = StringOf(obj, "type");
            var source = StringOf(obj, "source");
            var time = StringOf(obj, "time");
            var subject = StringOf(obj, "subject");
            cells.Add(new[]
            {
                Truncate(type, IdSoftCap),
                Truncate(source, IdSoftCap),
                Truncate(time, 28),
                Truncate(subject, TitleSoftCap),
            });
        }
        WriteTable(headers, widths, cells);
    }
}
