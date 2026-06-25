using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal sealed partial class TableRenderer
{
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
}
