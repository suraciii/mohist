using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal sealed partial class TableRenderer
{
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
}