using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal sealed partial class TableRenderer
{
    private void RenderProjectTemplateList(JsonNode? data)
    {
        var rows = AsArray(data);
        if (rows.Count == 0)
        {
            _out.WriteLine("No workflow templates");
            return;
        }

        var headers = new[] { "id", "name", "about", "default" };
        var widths = new[] { IdSoftCap, TitleSoftCap, BodySoftCap, 7 };

        var cells = new List<string[]>();
        foreach (var row in rows)
        {
            var id = StringOf(row, "id");
            var name = StringOf(row, "name");
            var about = StringOf(row, "description");
            if (string.IsNullOrEmpty(about))
                about = StringOf(row, "about");
            var isDefault = BoolOf(row, "isDefault") ? "yes" : "";
            cells.Add(new[]
            {
                Truncate(id, IdSoftCap),
                Truncate(name, TitleSoftCap),
                Truncate(about, BodySoftCap),
                Truncate(isDefault, 7),
            });
        }

        WriteTable(headers, widths, cells);
    }

    private void RenderProjectTemplateShow(JsonNode? data)
    {
        if (data is null)
        {
            _out.WriteLine("");
            return;
        }

        var id = StringOf(data, "id");
        var name = StringOf(data, "name");
        var description = StringOf(data, "description");
        if (string.IsNullOrEmpty(description))
            description = StringOf(data, "about");
        var isDefault = BoolOf(data, "isDefault");
        var yaml = StringOf(data, "yaml");

        _out.WriteLine($"id:          {id}");
        _out.WriteLine($"name:        {name}");
        _out.WriteLine($"description: {Truncate(description, BodySoftCap)}");
        _out.WriteLine($"default:     {(isDefault ? "yes" : "no")}");

        if (!string.IsNullOrEmpty(yaml))
        {
            _out.WriteLine("");
            _out.WriteLine("yaml:");
            foreach (var line in yaml.Split('\n'))
                _out.WriteLine($"  | {line.TrimEnd('\r')}");
        }
    }
}
