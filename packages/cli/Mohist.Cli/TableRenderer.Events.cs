using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal sealed partial class TableRenderer
{
    private void RenderDeadLetterList(JsonNode? data)
    {
        var rows = AsArray(data);
        if (rows.Count == 0)
        {
            _out.WriteLine("No unresolved dead letters");
            return;
        }

        var cells = new List<string[]>();
        foreach (var row in rows.OfType<JsonObject>())
        {
            cells.Add([
                NumberOf(row, "id"),
                Truncate(SanitizeTerminalText(StringOf(row, "type")), 42),
                Truncate(SanitizeTerminalText(StringOf(row, "handler")), 44),
                Truncate(SanitizeTerminalText(StringOf(row, "status")), 12),
                NumberOf(row, "attempts"),
                Truncate(SanitizeTerminalText(StringOf(row, "deadLetteredAt")), 25),
                Truncate(SanitizeTerminalText(StringOf(row, "error")), 60),
            ]);
        }

        WriteTable(
            ["id", "type", "handler", "status", "attempts", "dead-lettered", "error"],
            [10, 42, 44, 12, 8, 25, 60],
            cells);
    }

    private void RenderDeadLetterRedelivery(JsonNode? data)
    {
        if (data is not JsonObject row)
        {
            _out.WriteLine("Dead-letter re-delivery returned no result");
            return;
        }

        var status = BoolOf(row, "delivered")
            ? "delivered"
            : "failed";
        _out.WriteLine($"Dead-letter {NumberOf(row, "id")}: {status} after {NumberOf(row, "attempts")} attempt(s)");
        var error = StringOf(row, "error");
        if (!string.IsNullOrWhiteSpace(error))
            _out.WriteLine(SanitizeTerminalText(error));
    }

    private static string SanitizeTerminalText(string value)
    {
        var result = new System.Text.StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character is '\r' or '\n')
                break;
            if (character == '\u001b')
            {
                if (index + 1 < value.Length && value[index + 1] == '[')
                {
                    index += 2;
                    while (index < value.Length && value[index] is < '@' or > '~')
                        index++;
                }
                else if (index + 1 < value.Length)
                {
                    index++;
                }
                continue;
            }
            if (!char.IsControl(character))
                result.Append(character);
        }
        return result.ToString();
    }

    internal void RenderActivityList(JsonNode? data)
    {
        var rows = AsArray(data);
        if (rows.Count == 0)
        {
            _out.WriteLine("No activity recorded");
            return;
        }

        var headers = new[] { "provenance", "scope", "kind", "time", "title", "source" };
        var widths = new[] { 12, 10, 14, 22, 36, 36 };

        var cells = new List<string[]>();
        foreach (var row in rows.OfType<JsonObject>())
        {
            cells.Add([
                Truncate(StringOf(row, "provenance"), 12),
                Truncate(StringOf(row, "scope"), 10),
                Truncate(StringOf(row, "kind"), 14),
                Truncate(FormatTime(StringOf(row, "time")), 22),
                Truncate(StringOf(row, "title"), 36),
                Truncate(ActivitySourceIdentity(row), 36),
            ]);
        }

        WriteTable(headers, widths, cells);
    }

    private static string FormatTime(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";
        if (!DateTimeOffset.TryParse(raw, out var parsed))
            return raw;
        return parsed.ToString("yyyy-MM-dd HH:mm:ss zzz");
    }

    private static string ActivitySourceIdentity(JsonObject row)
    {
        var kind = StringOf(row, "kind");
        switch (kind)
        {
            case "issue":
                var issueId = NumberOf(row, "issueNumber");
                var eventType = StringOf(row, "eventType");
                var status = StringOf(row, "status");
                var issueRef = !string.IsNullOrEmpty(issueId) ? $"#{issueId}" : "";
                if (string.IsNullOrEmpty(issueRef))
                    return eventType;
                var detail = !string.IsNullOrEmpty(eventType) ? eventType : status;
                return string.IsNullOrEmpty(detail) ? issueRef : $"{issueRef} {detail}";
            case "workflow-run":
                var runId = StringOf(row, "workflowRunId");
                var runEvent = StringOf(row, "eventType");
                return string.IsNullOrEmpty(runEvent) ? runId : $"{runId} {runEvent}";
            case "agent-session":
                var sessionId = StringOf(row, "sessionId");
                var sessionStatus = StringOf(row, "status");
                return string.IsNullOrEmpty(sessionStatus) ? sessionId : $"{sessionId} {sessionStatus}";
            case "waiting":
                var waitingIssue = NumberOf(row, "issueNumber");
                return !string.IsNullOrEmpty(waitingIssue) ? $"#{waitingIssue}" : "waiting";
            case "runner":
                return StringOf(row, "runnerId");
            default:
                return StringOf(row, "id");
        }
    }
}
