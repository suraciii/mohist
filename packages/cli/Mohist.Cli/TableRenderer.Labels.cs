using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal sealed partial class TableRenderer
{
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
}