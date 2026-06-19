using System.Text;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal sealed class TableRenderer
{
    private const int TitleSoftCap = 60;
    private const int IdSoftCap = 24;
    private const int BodySoftCap = 60;

    private readonly TextWriter _out;
    private readonly string? _activeProjectId;

    internal TableRenderer(TextWriter output, string? activeProjectId)
    {
        _out = output;
        _activeProjectId = activeProjectId;
    }

    public void Render(JsonNode? data, MohistCliApi.TableShape shape)
    {
        switch (shape)
        {
            case MohistCliApi.TableShape.ProjectList:
                RenderProjectList(data);
                break;
            case MohistCliApi.TableShape.ProjectShow:
                RenderProjectShow(data);
                break;
            case MohistCliApi.TableShape.IssueList:
                RenderIssueList(data);
                break;
            case MohistCliApi.TableShape.IssueShow:
                RenderIssueShow(data);
                break;
            case MohistCliApi.TableShape.WorkflowStatus:
                RenderWorkflowStatus(data);
                break;
            case MohistCliApi.TableShape.Sessions:
                RenderSessions(data);
                break;
            case MohistCliApi.TableShape.RepoList:
                RenderRepoList(data);
                break;
            case MohistCliApi.TableShape.FeedbackList:
                RenderFeedbackList(data);
                break;
            case MohistCliApi.TableShape.FeedbackShow:
                RenderFeedbackShow(data);
                break;
            case MohistCliApi.TableShape.AgentList:
                RenderAgentList(data);
                break;
            case MohistCliApi.TableShape.AgentShow:
                RenderAgentShow(data);
                break;
            case MohistCliApi.TableShape.EpicList:
                RenderEpicList(data);
                break;
            case MohistCliApi.TableShape.EpicShow:
                RenderEpicShow(data);
                break;
            case MohistCliApi.TableShape.EpicLink:
                RenderEpicMembership(data, "Linked");
                break;
            case MohistCliApi.TableShape.EpicUnlink:
                RenderEpicMembership(data, "Unlinked");
                break;
            default:
                _out.WriteLine(data?.ToJsonString() ?? "");
                break;
        }
    }

    private void RenderProjectList(JsonNode? data)
    {
        var rows = AsArray(data);
        if (rows.Count == 0)
        {
            _out.WriteLine("No projects");
            return;
        }

        var hasBaseBranch = rows.Any(r => !string.IsNullOrEmpty(StringOf(r, "baseBranch")));

        if (!hasBaseBranch)
        {
            foreach (var row in rows)
            {
                var id = StringOf(row, "id");
                var name = StringOf(row, "name");
                var marker = !string.IsNullOrEmpty(_activeProjectId) &&
                             string.Equals(id, _activeProjectId, StringComparison.Ordinal)
                    ? "* "
                    : "  ";
                _out.WriteLine($"{marker}{name}");
            }
            return;
        }

        var headers = new[] { "*", "id", "name", "base branch" };
        var widths = new[] { 1, IdSoftCap, TitleSoftCap, 16 };

        var cells = new List<string[]>();
        foreach (var row in rows)
        {
            var id = StringOf(row, "id");
            var name = StringOf(row, "name");
            var baseBranch = StringOf(row, "baseBranch");
            var marker = !string.IsNullOrEmpty(_activeProjectId) &&
                         string.Equals(id, _activeProjectId, StringComparison.Ordinal)
                ? "*"
                : "";
            cells.Add(new[] { marker, Truncate(id, IdSoftCap), Truncate(name, TitleSoftCap), Truncate(baseBranch, 16) });
        }

        WriteTable(headers, widths, cells);
    }

    private void RenderProjectShow(JsonNode? data)
    {
        if (data is null)
        {
            _out.WriteLine("");
            return;
        }

        var id = StringOf(data, "id");
        var name = StringOf(data, "name");
        var baseBranch = StringOf(data, "baseBranch");
        var createdAt = StringOf(data, "createdAt");
        var updatedAt = StringOf(data, "updatedAt");
        var repos = data["repositories"] as JsonArray;
        var repoCount = repos?.Count.ToString() ?? "0";

        _out.WriteLine($"id:          {id}");
        _out.WriteLine($"name:        {name}");
        _out.WriteLine($"base branch: {baseBranch}");
        _out.WriteLine($"repositories:{repoCount}");
        _out.WriteLine($"created:     {Truncate(createdAt, TitleSoftCap)}");
        _out.WriteLine($"updated:     {Truncate(updatedAt, TitleSoftCap)}");
    }

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

    private void RenderRepoList(JsonNode? data)
    {
        var rows = AsArray(data);
        var headers = new[] { "name", "path", "remote", "base branch", "default" };
        var widths = new[] { 16, TitleSoftCap, TitleSoftCap, 16, 7 };

        var cells = new List<string[]>();
        foreach (var row in rows)
        {
            var name = StringOf(row, "name");
            var path = StringOf(row, "path");
            var remote = StringOf(row, "remote");
            var baseBranch = StringOf(row, "baseBranch");
            var isDefault = BoolOf(row, "isDefault") ? "yes" : "";
            cells.Add(new[]
            {
                Truncate(name, 16),
                Truncate(path, TitleSoftCap),
                Truncate(remote, TitleSoftCap),
                Truncate(baseBranch, 16),
                Truncate(isDefault, 7),
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

    private void RenderAgentList(JsonNode? data)
    {
        var rows = AsArray(data);
        var headers = new[] { "id", "name", "status", "updatedAt" };
        var widths = new[] { IdSoftCap, 24, 12, 24 };

        var cells = new List<string[]>();
        foreach (var row in rows)
        {
            cells.Add(new[]
            {
                Truncate(StringOf(row, "id"), IdSoftCap),
                Truncate(StringOf(row, "name"), 24),
                Truncate(StringOf(row, "status"), 12),
                Truncate(StringOf(row, "updatedAt"), 24),
            });
        }

        WriteTable(headers, widths, cells);
    }

    private void RenderAgentShow(JsonNode? data)
    {
        if (data is null)
        {
            _out.WriteLine("");
            return;
        }

        var skills = data["skills"] as JsonArray;
        var skillText = skills is null ? "" : string.Join(",", skills.Select(s => s?.GetValue<string>() ?? "").Where(s => !string.IsNullOrWhiteSpace(s)));

        _out.WriteLine($"id:                  {StringOf(data, "id")}");
        _out.WriteLine($"name:                {StringOf(data, "name")}");
        _out.WriteLine($"status:              {StringOf(data, "status")}");
        _out.WriteLine($"description:         {Truncate(StringOf(data, "description"), TitleSoftCap)}");
        _out.WriteLine($"max concurrent runs: {NumberOf(data, "maxConcurrentRuns")}");
        _out.WriteLine($"skills:              {skillText}");
        _out.WriteLine($"createdAt:           {Truncate(StringOf(data, "createdAt"), TitleSoftCap)}");
        _out.WriteLine($"updatedAt:           {Truncate(StringOf(data, "updatedAt"), TitleSoftCap)}");
        var instructions = StringOf(data, "instructions");
        if (!string.IsNullOrEmpty(instructions))
            _out.WriteLine($"instructions:        {Truncate(instructions, BodySoftCap)}");
    }

    private void RenderEpicList(JsonNode? data)
    {
        var rows = AsArray(data);
        if (rows.Count == 0)
        {
            _out.WriteLine("No epics");
            return;
        }

        var headers = new[] { "number", "title", "status", "priority" };
        var widths = new[] { 7, TitleSoftCap, 12, 9 };

        var cells = new List<string[]>();
        foreach (var row in rows)
        {
            var number = NumberOf(row, "number");
            var title = StringOf(row, "title");
            var status = StringOf(row, "status");
            var priority = StringOf(row, "priority");
            cells.Add(new[]
            {
                number,
                Truncate(title, TitleSoftCap),
                Truncate(status, 12),
                Truncate(priority, 9),
            });
        }

        WriteTable(headers, widths, cells);
    }

    private void RenderEpicShow(JsonNode? data)
    {
        if (data is null)
        {
            _out.WriteLine("");
            return;
        }

        var number = NumberOf(data, "number");
        var title = StringOf(data, "title");
        var status = StringOf(data, "status");
        var priority = StringOf(data, "priority");
        var description = StringOf(data, "description");
        var progress = data["progress"] as JsonObject;
        var delivered = NumberOf(progress, "deliveredCount");
        var total = NumberOf(progress, "totalIssueCount");
        var nextIssue = progress?["nextIssue"] as JsonObject;
        var linked = data["linkedIssues"] as JsonArray;

        _out.WriteLine($"number:     {number}");
        _out.WriteLine($"title:      {Truncate(title, TitleSoftCap)}");
        _out.WriteLine($"status:     {status}");
        _out.WriteLine($"priority:   {priority}");
        if (!string.IsNullOrEmpty(description))
            _out.WriteLine($"description:{Truncate(description, BodySoftCap)}");
        _out.WriteLine($"progress:   {delivered}/{total} delivered");
        if (nextIssue is not null)
        {
            var nextNumber = NumberOf(nextIssue, "number");
            var nextTitle = StringOf(nextIssue, "title");
            _out.WriteLine($"next issue: #{nextNumber} {Truncate(nextTitle, TitleSoftCap)}");
        }

        if (linked is null || linked.Count == 0)
        {
            _out.WriteLine("linked issues: (none)");
            return;
        }

        _out.WriteLine("");
        var headers = new[] { "number", "title", "status", "priority" };
        var widths = new[] { 7, TitleSoftCap, 12, 9 };
        var cells = new List<string[]>();
        foreach (var row in linked)
        {
            var linkedNumber = NumberOf(row, "number");
            var linkedTitle = StringOf(row, "title");
            var linkedStatus = StringOf(row, "status");
            var linkedPriority = StringOf(row, "priority");
            cells.Add(new[]
            {
                linkedNumber,
                Truncate(linkedTitle, TitleSoftCap),
                Truncate(linkedStatus, 12),
                Truncate(linkedPriority, 9),
            });
        }
        WriteTable(headers, widths, cells);
    }

    private void RenderEpicMembership(JsonNode? data, string verb)
    {
        if (data is null)
        {
            _out.WriteLine("OK");
            return;
        }

        var epicId = StringOf(data, "epicId");
        var issueId = StringOf(data, "issueId");
        if (!string.IsNullOrEmpty(epicId) && !string.IsNullOrEmpty(issueId))
            _out.WriteLine($"{verb} issue {issueId} {(verb == "Linked" ? "to" : "from")} epic {epicId}");
        else
            _out.WriteLine("OK");
    }

    private void WriteBody(string body)
    {
        foreach (var line in body.Split('\n'))
            _out.WriteLine($"  {line}");
    }


    private void WriteTable(string[] headers, int[] widths, IReadOnlyList<string[]> cells)
    {
        _out.WriteLine(string.Join("  ", headers.Select((h, i) => h.PadRight(widths[i]))).TrimEnd());
        if (cells.Count == 0)
            return;
        foreach (var row in cells)
            _out.WriteLine(string.Join("  ", row.Select((c, i) => c.PadRight(widths[i]))).TrimEnd());
    }

    private static JsonArray AsArray(JsonNode? data)
    {
        return data as JsonArray ?? new JsonArray();
    }

    private static string StringOf(JsonNode? node, string key)
    {
        var value = node?[key];
        if (value is null || value is JsonObject || value is JsonArray)
            return "";
        var s = value.GetValue<string>();
        return s ?? "";
    }

    private static bool BoolOf(JsonNode? node, string key)
    {
        var value = node?[key];
        if (value is null) return false;
        if (value is JsonValue jv)
            return jv.TryGetValue<bool>(out var b) && b;
        return false;
    }

    private static string NumberOf(JsonNode? node, string key)
    {
        var value = node?[key];
        if (value is null) return "";
        if (value is JsonValue jv)
        {
            if (jv.TryGetValue<int>(out var i)) return i.ToString();
            if (jv.TryGetValue<long>(out var l)) return l.ToString();
        }
        return value.ToString();
    }

    private static string Truncate(string value, int softCap)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var firstLine = value.AsSpan();
        var nl = firstLine.IndexOf('\n');
        if (nl >= 0) firstLine = firstLine[..nl];
        if (firstLine.Length <= softCap) return firstLine.ToString();
        if (softCap <= 1) return firstLine[..softCap].ToString();
        return string.Concat(firstLine[..(softCap - 1)], "…");
    }
}
