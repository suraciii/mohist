using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal sealed partial class TableRenderer
{
    internal void RenderRunnerStatus(JsonNode? data)
    {
        var rows = AsArray(data);
        if (rows.Count == 0)
        {
            _out.WriteLine("No runners connected");
            return;
        }

        var headers = new[] { "id", "heartbeat", "state" };
        var widths = new[] { IdSoftCap, 18, 8 };

        var cells = new List<string[]>();
        foreach (var row in rows)
        {
            if (row is not JsonObject obj) continue;
            var id = StringOf(obj, "id");
            var heartbeat = FormatHeartbeat(obj);
            var state = DeriveRunnerState(obj);
            cells.Add(new[]
            {
                Truncate(id, IdSoftCap),
                Truncate(heartbeat, 18),
                Truncate(state, 8),
            });
        }

        WriteTable(headers, widths, cells);
    }

    private static string DeriveRunnerState(JsonNode? row)
    {
        if (row is not JsonObject obj) return "unknown";
        if (obj["capacity"] is not JsonObject capacity) return "unknown";
        var usedSlots = capacity["usedSlots"];
        if (usedSlots is not JsonValue value) return "unknown";
        if (value.TryGetValue<int>(out var i)) return i == 0 ? "idle" : "busy";
        if (value.TryGetValue<long>(out var l)) return l == 0 ? "idle" : "busy";
        if (value.TryGetValue<double>(out var d) && !double.IsNaN(d)) return d == 0d ? "idle" : "busy";
        return "unknown";
    }

    private void RenderRepoList(JsonNode? data)
    {
        var rows = data is JsonObject project && project["repositories"] is JsonArray repositories
            ? repositories
            : AsArray(data);
        if (rows.Count == 0)
        {
            _out.WriteLine("No repositories found");
            return;
        }

        var headers = new[] { "name", "git URL", "base branch", "default" };
        var widths = new[] { 16, TitleSoftCap, 16, 7 };

        var cells = new List<string[]>();
        foreach (var row in rows)
        {
            var name = StringOf(row, "name");
            var gitUrl = StringOf(row, "gitUrl");
            var baseBranch = StringOf(row, "baseBranch");
            var isDefault = BoolOf(row, "isDefault") ? "yes" : "no";
            cells.Add(new[]
            {
                Truncate(name, 16),
                Truncate(gitUrl, TitleSoftCap),
                Truncate(baseBranch, 16),
                Truncate(isDefault, 7),
            });
        }

        WriteTable(headers, widths, cells);
    }
}
