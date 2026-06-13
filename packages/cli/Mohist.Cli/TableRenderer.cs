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
        var headers = new[] { "number", "title", "stage", "status", "priority" };
        var widths = new[] { 7, TitleSoftCap, 16, 12, 9 };

        var cells = new List<string[]>();
        foreach (var row in rows)
        {
            var number = NumberOf(row, "number");
            var title = StringOf(row, "title");
            var stage = StringOf(row, "workflowStage");
            var status = StringOf(row, "status");
            var priority = StringOf(row, "priority");
            cells.Add(new[]
            {
                number,
                Truncate(title, TitleSoftCap),
                Truncate(stage, 16),
                Truncate(status, 12),
                Truncate(priority, 9),
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

        _out.WriteLine($"number:   {number}");
        _out.WriteLine($"title:    {Truncate(title, TitleSoftCap)}");
        _out.WriteLine($"stage:    {stage}");
        _out.WriteLine($"status:   {status}");
        _out.WriteLine($"priority: {priority}");
        _out.WriteLine($"project:  {project}");
        _out.WriteLine($"updated:  {Truncate(updatedAt, TitleSoftCap)}");
        if (!string.IsNullOrEmpty(body))
            _out.WriteLine($"body:     {Truncate(body, BodySoftCap)}");
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

        _out.WriteLine($"current stage: {currentStage}");
        _out.WriteLine($"status:        {status}");

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
