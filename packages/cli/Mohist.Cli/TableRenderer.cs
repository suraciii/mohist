using System.Text.Json.Nodes;

namespace Mohist.Cli;

// Why partial: TableRenderer is a single-responsibility JSON→table renderer with 18
// peer-case entity branches. Partial is the right tool here because the class is
// single-purpose, every branch is a peer (no divergent dependencies), and they share
// the same infrastructure (TextWriter, _activeProjectId, column-width constants,
// WriteTable / AsArray / StringOf / BoolOf / NumberOf / Truncate). Splitting into
// collaborator classes would force infrastructure duplication or reverse-dependency
// on a "core" — worse than partial. This is the textbook partial use-case, not a
// god-class split. See design.md §"决策 2" and tasks.json#T-001.
//
// Cluster layout:
//   TableRenderer.cs         — dispatch + shared infrastructure
//   TableRenderer.Issues.cs  — Issue / template / workflow / delivery / feedback / sessions / labels
//   TableRenderer.Runners.cs — Runner / repository
//   TableRenderer.Epics.cs   — Epic list / show / membership
//   TableRenderer.Entities.cs — Project / Agent (thin peers)
internal sealed partial class TableRenderer
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

    public void Render(JsonNode? data, MohistCliApi.TableShape shape, bool colorEnabled = true)
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
            case MohistCliApi.TableShape.LabelList:
                RenderLabelList(data);
                break;
            case MohistCliApi.TableShape.IssueTemplateList:
                RenderIssueTemplateList(data);
                break;
            case MohistCliApi.TableShape.IssueTemplateShow:
                RenderIssueTemplateShow(data);
                break;
            case MohistCliApi.TableShape.RunnerList:
                RenderRunnerList(data, colorEnabled);
                break;
            case MohistCliApi.TableShape.WorkflowProfile:
                RenderWorkflowProfile(data);
                break;
            case MohistCliApi.TableShape.WorkflowVariables:
                RenderWorkflowProfileVariables(data);
                break;
            case MohistCliApi.TableShape.WorkflowProfilePrompt:
                RenderWorkflowProfilePrompt(data);
                break;
            case MohistCliApi.TableShape.WorkflowProfilePreview:
                RenderWorkflowProfilePreview(data);
                break;
            case MohistCliApi.TableShape.SessionMetadata:
                RenderSessionMetadata(data);
                break;
            case MohistCliApi.TableShape.SessionTranscriptSummary:
                RenderSessionTranscriptSummary(data);
                break;
            case MohistCliApi.TableShape.SessionRecovery:
                RenderSessionRecovery(data);
                break;
            case MohistCliApi.TableShape.AgentSessionLaunch:
                RenderAgentSessionLaunch(data);
                break;
            case MohistCliApi.TableShape.AgentSessionFollowup:
                RenderAgentSessionFollowup(data);
                break;
            case MohistCliApi.TableShape.ProjectTemplateList:
                RenderProjectTemplateList(data);
                break;
            case MohistCliApi.TableShape.ProjectTemplateShow:
                RenderProjectTemplateShow(data);
                break;
            case MohistCliApi.TableShape.ProjectWorkflowProfile:
                RenderProjectWorkflowProfile(data);
                break;
            case MohistCliApi.TableShape.AgentSessionCancel:
                RenderAgentSessionCancel(data);
                break;
            case MohistCliApi.TableShape.AgentSessionList:
                RenderAgentSessionList(data);
                break;
            case MohistCliApi.TableShape.AgentSessionShow:
                RenderAgentSessionShow(data);
                break;
            case MohistCliApi.TableShape.AgentSessionTranscript:
                RenderAgentSessionTranscript(data);
                break;
            case MohistCliApi.TableShape.AgentSubscriptionList:
                RenderAgentSubscriptionList(data);
                break;
            case MohistCliApi.TableShape.AgentSubscriptionShow:
                RenderAgentSubscriptionShow(data);
                break;
            case MohistCliApi.TableShape.RoutingRuleList:
                RenderRoutingRuleList(data);
                break;
            case MohistCliApi.TableShape.RoutingRule:
                RenderRoutingRule(data);
                break;
            case MohistCliApi.TableShape.IssueArchiveCompleted:
                RenderIssueArchiveCompleted(data);
                break;
            case MohistCliApi.TableShape.WorkflowRunDetail:
                RenderWorkflowRunDetail(data);
                break;
            case MohistCliApi.TableShape.WorkflowRunVariables:
                RenderWorkflowRunVariables(data);
                break;
            case MohistCliApi.TableShape.WorkflowRunEvents:
                RenderWorkflowRunEvents(data);
                break;
            case MohistCliApi.TableShape.DeadLetterList:
                RenderDeadLetterList(data);
                break;
            case MohistCliApi.TableShape.DeadLetterRedelivery:
                RenderDeadLetterRedelivery(data);
                break;
            default:
                _out.WriteLine(data?.ToJsonString() ?? "");
                break;
        }
    }

    private void WriteTable(string[] headers, int[] widths, IReadOnlyList<string[]> cells)
    {
        _out.WriteLine(string.Join("  ", headers.Select((h, i) => h.PadRight(widths[i]))).TrimEnd());
        if (cells.Count == 0)
            return;
        foreach (var row in cells)
            _out.WriteLine(string.Join("  ", row.Select((c, i) => c.PadRight(widths[i]))).TrimEnd());
    }

    private void RenderRunnerList(JsonNode? data, bool colorEnabled)
    {
        var rows = AsArray(data);
        if (rows.Count == 0)
        {
            _out.WriteLine("No runners connected");
            _out.WriteLine("Start a runner: npx mohist runner");
            return;
        }

        var headers = new[] { "id", "kind", "status", "scope", "capacity", "heartbeat", "hostname" };
        var widths = new[] { IdSoftCap, 12, 16, 16, 14, 18, TitleSoftCap };

        var cells = new List<string[]>();
        foreach (var row in rows)
        {
            if (row is not JsonObject obj) continue;
            var id = StringOf(obj, "id");
            var kind = StringOf(obj, "kind");
            var status = StringOf(obj, "status");
            var scope = FormatScope(obj["scope"]);
            var capacity = FormatCapacity(obj["capacity"]);
            var heartbeat = FormatHeartbeat(obj);
            var hostname = StringOf(obj, "hostname");
            cells.Add(new[]
            {
                Truncate(id, IdSoftCap),
                Truncate(kind, 12),
                Truncate(colorEnabled ? ColorizeStatus(status) : status, 16),
                Truncate(scope, 16),
                Truncate(capacity, 14),
                Truncate(heartbeat, 18),
                Truncate(hostname, TitleSoftCap),
            });
        }

        WriteTable(headers, widths, cells);
    }

    private static string FormatScope(JsonNode? scopeNode)
    {
        if (scopeNode is not JsonObject scope) return "";
        var type = StringOf(scope, "type");
        if (string.Equals(type, "global", StringComparison.OrdinalIgnoreCase))
            return "global";
        if (string.Equals(type, "project", StringComparison.OrdinalIgnoreCase))
        {
            var projectId = StringOf(scope, "projectId");
            var projectName = StringOf(scope, "projectName");
            if (!string.IsNullOrWhiteSpace(projectName)) return projectName;
            if (!string.IsNullOrWhiteSpace(projectId)) return projectId;
            return "project";
        }
        return type;
    }

    private static string FormatCapacity(JsonNode? capacityNode)
    {
        if (capacityNode is null) return "-";
        if (capacityNode is not JsonObject capacity) return "-";
        var used = NumberOf(capacity, "usedSlots");
        var total = NumberOf(capacity, "totalSlots");
        if (string.IsNullOrEmpty(used) && string.IsNullOrEmpty(total))
            return "-";
        if (string.IsNullOrEmpty(used)) used = "0";
        if (string.IsNullOrEmpty(total)) total = "0";
        return $"{used}/{total} slots";
    }

    private static string FormatHeartbeat(JsonNode? row)
    {
        if (row is not JsonObject obj) return "unknown";
        var lastHeartbeatAt = obj["lastHeartbeatAt"];
        if (lastHeartbeatAt is null) return "unknown";
        if (lastHeartbeatAt is JsonValue jv && jv.TryGetValue<string>(out var raw))
        {
            if (string.IsNullOrWhiteSpace(raw) || raw == "null") return "unknown";
        }
        var dateString = StringOf(obj, "lastHeartbeatAt");
        if (string.IsNullOrWhiteSpace(dateString)) return "unknown";
        if (!DateTimeOffset.TryParse(dateString, out var heartbeatAt)) return "unknown";
        var age = DateTimeOffset.UtcNow - heartbeatAt.ToUniversalTime();
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        if (age.TotalSeconds < 60) return $"{Math.Max(0, (int)age.TotalSeconds)}s ago";
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes}m ago";
        if (age.TotalHours < 24) return $"{(int)age.TotalHours}h ago";
        return $"{(int)age.TotalDays}d ago";
    }

    internal const string AnsiReset = "\u001b[0m";
    internal const string AnsiGreen = "\u001b[32m";
    internal const string AnsiBlue = "\u001b[34m";
    internal const string AnsiYellow = "\u001b[33m";
    internal const string AnsiDim = "\u001b[2m";

    internal static string ColorizeStatus(string status)
    {
        if (string.Equals(status, "idle", StringComparison.OrdinalIgnoreCase))
            return string.Concat(AnsiGreen, status, AnsiReset);
        if (string.Equals(status, "busy", StringComparison.OrdinalIgnoreCase))
            return string.Concat(AnsiBlue, status, AnsiReset);
        if (string.Equals(status, "stale", StringComparison.OrdinalIgnoreCase))
            return string.Concat(AnsiYellow, status, AnsiReset);
        if (string.Equals(status, "offline", StringComparison.OrdinalIgnoreCase))
            return string.Concat(AnsiDim, status, AnsiReset);
        return status;
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
