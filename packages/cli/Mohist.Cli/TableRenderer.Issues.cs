using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal sealed partial class TableRenderer
{
    private void RenderIssueList(JsonNode? data)
    {
        var rows = AsArray(data);
        var headers = new[] { "number", "title", "stage", "status", "priority", "state", "labels" };
        var widths = new[] { 7, TitleSoftCap, 16, 12, 9, TitleSoftCap, TitleSoftCap };

        var cells = new List<string[]>();
        foreach (var row in rows)
        {
            var number = NumberOf(row, "number");
            var title = StringOf(row, "title");
            var stage = StringOf(row, "workflowStage");
            var status = StringOf(row, "status");
            var priority = StringOf(row, "priority");
            var state = FormatIssueState(row);
            var labels = FormatLabels(row?["labels"]);
            cells.Add(new[]
            {
                number,
                Truncate(title, TitleSoftCap),
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
        var project = StringOf(data, "projectName");
        if (string.IsNullOrEmpty(project))
            project = StringOf(data, "projectId");
        var updatedAt = StringOf(data, "updatedAt");
        var body = StringOf(data, "body");
        var labels = FormatLabels(data["labels"]);

        _out.WriteLine($"number:   {number}");
        _out.WriteLine($"title:    {Truncate(title, TitleSoftCap)}");
        _out.WriteLine($"stage:    {stage}");
        _out.WriteLine($"status:   {status}");
        _out.WriteLine($"priority: {priority}");
        _out.WriteLine($"project:  {project}");
        _out.WriteLine($"updated:  {Truncate(updatedAt, TitleSoftCap)}");
        _out.WriteLine($"labels:   {labels}");
        var workflowProfileId = StringOf(data, "workflowProfileId");
        if (!string.IsNullOrEmpty(workflowProfileId))
            _out.WriteLine($"profile:  {workflowProfileId}");
        if (!string.IsNullOrEmpty(body))
            _out.WriteLine($"body:     {Truncate(body, BodySoftCap)}");
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

    private static string FormatLabels(JsonNode? labels)
    {
        if (labels is not JsonObject obj || obj.Count == 0)
            return "";
        return string.Join(",", obj.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv =>
            {
                var value = kv.Value is null ? "" : (kv.Value.GetValue<string>() ?? "");
                return string.Concat(kv.Key, "=", value);
            }));
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
        var message = StringOf(failure, "message");
        if (string.IsNullOrEmpty(message)) return;
        var output = failure["output"] as JsonNode;

        var (kind, guidance, evidence) = DeliveryFailureGuidance.ResolveWithEvidence(message, output);
        if (kind is null || guidance is null) return;

        var workspaceEvidence = DeliveryFailureGuidance.ResolveWorkspaceEvidence(message, output);

        _out.WriteLine("");
        _out.WriteLine("delivery failure:");
        _out.WriteLine($"  kind:       {kind}");
        _out.WriteLine($"  label:      {guidance.Value.Label}");
        _out.WriteLine($"  next action: {guidance.Value.NextAction}");
        if (string.Equals(kind, DeliveryFailureGuidance.BranchInvariantViolation, StringComparison.OrdinalIgnoreCase) && evidence is not null)
        {
            _out.WriteLine($"  attribution: runner/action (not issue work)");
            if (!string.IsNullOrEmpty(evidence.Boundary))
            {
                _out.WriteLine($"  boundary:   {evidence.Boundary}");
            }
            if (!string.IsNullOrEmpty(evidence.ExpectedBranch))
            {
                _out.WriteLine($"  expected:   {evidence.ExpectedBranch}");
            }
            if (!string.IsNullOrEmpty(evidence.ObservedBranch))
            {
                _out.WriteLine($"  observed:   {evidence.ObservedBranch}");
            }
            else if (!string.IsNullOrEmpty(evidence.ObservedRef))
            {
                _out.WriteLine($"  observed:   (detached at {evidence.ObservedRef})");
            }
        }
        else if (DeliveryFailureGuidance.IsWorkspaceMaterializationKind(kind) && workspaceEvidence is not null)
        {
            _out.WriteLine($"  attribution: workflow infrastructure (not issue work)");
            if (!string.IsNullOrEmpty(workspaceEvidence.WorkspacePath))
            {
                _out.WriteLine($"  workspace:  {workspaceEvidence.WorkspacePath}");
            }
            if (!string.IsNullOrEmpty(workspaceEvidence.ExpectedRunId))
            {
                _out.WriteLine($"  expected:   {workspaceEvidence.ExpectedRunId}");
            }
            if (!string.IsNullOrEmpty(workspaceEvidence.ActualRunId))
            {
                _out.WriteLine($"  actual:     {workspaceEvidence.ActualRunId}");
            }
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

    private void WriteBody(string body)
    {
        foreach (var line in body.Split('\n'))
            _out.WriteLine($"  {line}");
    }

    private void RenderIssueTemplateList(JsonNode? data)
    {
        var rows = AsArray(data);
        if (rows.Count == 0)
        {
            _out.WriteLine("No issue templates");
            return;
        }

        var headers = new[] { "name", "about", "default", "source" };
        var widths = new[] { IdSoftCap, TitleSoftCap, 7, 12 };

        var cells = new List<string[]>();
        foreach (var row in rows)
        {
            var name = StringOf(row, "name");
            if (string.IsNullOrEmpty(name))
                name = StringOf(row, "id");
            var about = StringOf(row, "about");
            var isDefault = BoolOf(row, "isDefault") ? "yes" : "";
            var source = StringOf(row, "source");
            cells.Add(new[]
            {
                Truncate(name, IdSoftCap),
                Truncate(about, TitleSoftCap),
                Truncate(isDefault, 7),
                Truncate(source, 12),
            });
        }

        WriteTable(headers, widths, cells);
    }

    private void RenderIssueTemplateShow(JsonNode? data)
    {
        if (data is null)
        {
            _out.WriteLine("");
            return;
        }

        var id = StringOf(data, "id");
        var name = StringOf(data, "name");
        var about = StringOf(data, "about");
        var isDefault = BoolOf(data, "isDefault");
        var source = StringOf(data, "source");
        var suitableFor = data["suitableFor"] as JsonArray;
        var defaults = data["defaults"] as JsonObject;
        var sections = data["sections"] as JsonArray;

        _out.WriteLine($"id:          {id}");
        _out.WriteLine($"name:        {name}");
        _out.WriteLine($"about:       {Truncate(about, BodySoftCap)}");
        _out.WriteLine($"default:     {(isDefault ? "yes" : "no")}");
        _out.WriteLine($"source:      {source}");

        if (suitableFor is not null && suitableFor.Count > 0)
        {
            var items = suitableFor.Select(s => s?.GetValue<string>() ?? "").Where(s => !string.IsNullOrWhiteSpace(s));
            _out.WriteLine($"suitable for: {string.Join(", ", items)}");
        }

        if (defaults is not null && defaults.Count > 0)
        {
            var risk = StringOf(defaults, "risk");
            var workflow = StringOf(defaults, "workflow");
            var labels = defaults["labels"] as JsonObject;
            var labelText = labels is null || labels.Count == 0
                ? ""
                : string.Join(",", labels.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv =>
                    {
                        var value = kv.Value is null ? "" : (kv.Value.GetValue<string>() ?? "");
                        return string.Concat(kv.Key, "=", value);
                    }));
            if (!string.IsNullOrWhiteSpace(risk))
                _out.WriteLine($"defaults.risk:       {risk}");
            if (!string.IsNullOrWhiteSpace(workflow))
                _out.WriteLine($"defaults.workflow:   {workflow}");
            if (!string.IsNullOrEmpty(labelText))
                _out.WriteLine($"defaults.labels:     {labelText}");
        }

        if (sections is null || sections.Count == 0)
        {
            _out.WriteLine("");
            _out.WriteLine("sections: (none)");
            return;
        }

        _out.WriteLine("");
        _out.WriteLine($"sections: {sections.Count}");
        for (var i = 0; i < sections.Count; i++)
        {
            if (sections[i] is not JsonObject section) continue;
            var title = StringOf(section, "title");
            var guidance = StringOf(section, "guidance");
            var placeholder = StringOf(section, "placeholder");

            _out.WriteLine("");
            _out.WriteLine($"  [{i + 1}] {title}");
            if (!string.IsNullOrEmpty(guidance))
            {
                _out.WriteLine("      guidance:");
                foreach (var line in guidance.Split('\n'))
                    _out.WriteLine($"        {line.TrimEnd('\r')}");
            }
            if (!string.IsNullOrEmpty(placeholder))
            {
                _out.WriteLine("      placeholder:");
                foreach (var line in placeholder.Split('\n'))
                    _out.WriteLine($"        {line.TrimEnd('\r')}");
            }
        }
    }

    private void RenderLabelList(JsonNode? data)
    {
        var rows = AsArray(data);
        if (rows.Count == 0)
        {
            _out.WriteLine("No label definitions");
            return;
        }

        var headers = new[] { "key", "description", "origin" };
        var widths = new[] { 16, TitleSoftCap, 8 };

        var cells = new List<string[]>();
        foreach (var row in rows)
        {
            var key = StringOf(row, "key");
            var description = StringOf(row, "description");
            var origin = StringOf(row, "origin");
            var supportedValues = row?["supportedValues"] as JsonArray;
            if (supportedValues is not null && supportedValues.Count > 0)
            {
                var values = string.Join(",", supportedValues.Select(v => v?.GetValue<string>() ?? "").Where(s => !string.IsNullOrWhiteSpace(s)));
                if (!string.IsNullOrEmpty(values))
                    description = $"{description} [{values}]";
            }
            cells.Add(new[]
            {
                Truncate(key, 16),
                Truncate(description, TitleSoftCap),
                Truncate(origin, 8),
            });
        }

        WriteTable(headers, widths, cells);
    }
}
