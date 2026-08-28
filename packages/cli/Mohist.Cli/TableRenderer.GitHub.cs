using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal sealed partial class TableRenderer
{
    private void RenderGitHubConnectionList(JsonNode? data)
    {
        var rows = AsArray(data);
        if (rows.Count == 0)
        {
            _out.WriteLine("No GitHub connections");
            return;
        }
        var headers = new[] { "repository", "github", "status", "attention", "reprojection" };
        var widths = new[] { 20, 30, 12, 10, 12 };
        var cells = rows.OfType<JsonObject>().Select(row => new[]
        {
            Truncate(StringOf(row, "repositoryName"), widths[0]),
            Truncate($"{StringOf(row, "owner")}/{StringOf(row, "repo")}", widths[1]),
            Truncate(StringOf(row, "status"), widths[2]),
            Truncate(Flag(row, "needsAttention"), widths[3]),
            Truncate(Flag(row, "needsReprojection"), widths[4]),
        }).ToList();
        WriteTable(headers, widths, cells);
    }

    private void RenderGitHubConnection(JsonNode? data)
    {
        if (data is not JsonObject row)
        {
            _out.WriteLine(data?.ToJsonString() ?? "");
            return;
        }
        _out.WriteLine($"GitHub       {StringOf(row, "owner")}/{StringOf(row, "repo")}");
        _out.WriteLine($"Repository   {StringOf(row, "repositoryName")}");
        _out.WriteLine($"Status       {StringOf(row, "status")}");
        _out.WriteLine($"Installation {StringOf(row, "installationId")}");
        _out.WriteLine($"Attention    {Flag(row, "needsAttention")}");
        _out.WriteLine($"Reprojection {Flag(row, "needsReprojection")}");
        if (row["lastError"] is JsonObject error)
            _out.WriteLine($"Error        {StringOf(error, "code")}: {StringOf(error, "detail")}");
    }

    private static string Flag(JsonObject row, string key) =>
        row[key]?.GetValue<bool>() == true ? "yes" : "no";
}
