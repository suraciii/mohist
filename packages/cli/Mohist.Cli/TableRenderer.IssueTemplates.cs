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

        var headers = new[] { "name", "description", "source" };
        var widths = new[] { IdSoftCap, TitleSoftCap, 12 };

        var cells = new List<string[]>();
        foreach (var row in rows)
        {
            var name = StringOf(row, "name");
            if (string.IsNullOrEmpty(name))
                name = StringOf(row, "id");
            var description = StringOf(row, "description");
            var source = StringOf(row, "source");
            cells.Add(new[]
            {
                Truncate(name, IdSoftCap),
                Truncate(description, TitleSoftCap),
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
        var description = StringOf(data, "description");
        var source = StringOf(data, "source");
        var body = StringOf(data, "body");

        _out.WriteLine($"id:          {id}");
        _out.WriteLine($"name:        {name}");
        _out.WriteLine($"description: {Truncate(description, BodySoftCap)}");
        _out.WriteLine($"source:      {source}");

        _out.WriteLine("");
        _out.WriteLine("body:");
        if (string.IsNullOrEmpty(body))
        {
            _out.WriteLine("  (empty)");
            return;
        }
        foreach (var line in body.Replace("\r\n", "\n").Split('\n'))
            _out.WriteLine($"  | {line.TrimEnd('\r')}");
    }

    private void RenderWorkflowProfile(JsonNode? data)
    {
        if (data is null)
        {
            _out.WriteLine("");
            return;
        }

        var issueNumber = NumberOf(data, "issueNumber");
        var profileId = StringOf(data, "profileId");
        var updateMode = StringOf(data, "updateMode");
        var templateSource = StringOf(data, "templateSource");
        var sourceTemplateId = StringOf(data, "sourceTemplateId");
        var hasCustomTemplate = BoolOf(data, "hasCustomTemplate");
        var yamlNode = data["yaml"];
        var yaml = yamlNode is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : null;

        _out.WriteLine($"issue:        #{issueNumber}");
        if (!string.IsNullOrEmpty(profileId))
            _out.WriteLine($"profile:      {profileId}");
        _out.WriteLine($"update mode:  {Truncate(updateMode, 16)}");
        _out.WriteLine($"template src: {Truncate(templateSource, 16)}");
        if (!string.IsNullOrEmpty(sourceTemplateId))
            _out.WriteLine($"source tpl:   {Truncate(sourceTemplateId, TitleSoftCap)}");

        RenderWorkflowProfileTemplate(hasCustomTemplate, yaml);
        RenderWorkflowProfileVariables(data["variables"]);
    }

    private void RenderWorkflowProfileTemplate(bool hasCustomTemplate, string? yaml)
    {
        _out.WriteLine("");
        _out.WriteLine("template:");
        if (yaml is not null)
        {
            _out.WriteLine("  source: custom");
            foreach (var line in yaml.Split('\n'))
                _out.WriteLine($"  | {line.TrimEnd('\r')}");
        }
        else if (hasCustomTemplate)
        {
            _out.WriteLine("  source: custom (empty body)");
        }
        else
        {
            _out.WriteLine("  source: inherited (project/system default)");
        }
    }

    private void RenderWorkflowProfileVariables(JsonNode? variables)
    {
        _out.WriteLine("");
        _out.WriteLine("variables:");
        var bundle = variables as JsonObject;
        if (bundle is null)
        {
            _out.WriteLine("  (none)");
            return;
        }

        var vars = bundle["vars"] as JsonObject;
        var stages = bundle["stages"] as JsonObject;

        if (vars is null || vars.Count == 0)
        {
            _out.WriteLine("  vars: (none)");
        }
        else
        {
            _out.WriteLine($"  vars ({vars.Count}):");
            foreach (var kvp in vars.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                _out.WriteLine($"    {kvp.Key}: {FormatVariableValue(kvp.Value)}");
            }
        }

        if (stages is null || stages.Count == 0)
        {
            _out.WriteLine("  stages: (none)");
            return;
        }

        _out.WriteLine($"  stages ({stages.Count}):");
        foreach (var stageKvp in stages.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (stageKvp.Value is not JsonObject stageObj)
            {
                _out.WriteLine($"    {stageKvp.Key}: <invalid>");
                continue;
            }
            var stageVars = stageObj["vars"] as JsonObject;
            if (stageVars is null || stageVars.Count == 0)
            {
                _out.WriteLine($"    {stageKvp.Key}: (none)");
                continue;
            }
            _out.WriteLine($"    {stageKvp.Key} ({stageVars.Count}):");
            foreach (var kv in stageVars.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                _out.WriteLine($"      {kv.Key}: {FormatVariableValue(kv.Value)}");
            }
        }
    }

    private static string FormatVariableValue(JsonNode? value)
    {
        if (value is null) return "<null>";
        if (value is JsonValue jv)
        {
            if (jv.TryGetValue<string>(out var s)) return s ?? "";
            return jv.ToJsonString();
        }
        return value.ToJsonString();
    }

    private void RenderWorkflowProfilePrompt(JsonNode? data)
    {
        if (data is null)
        {
            _out.WriteLine("prompt: (none)");
            return;
        }

        var key = StringOf(data, "key");
        var body = StringOf(data, "body");
        var deleted = BoolOf(data, "deleted");
        _out.WriteLine(string.IsNullOrEmpty(key) ? "prompt:" : $"prompt: {key}");
        if (deleted)
        {
            _out.WriteLine("  deleted: yes");
            return;
        }
        if (string.IsNullOrEmpty(body))
        {
            _out.WriteLine("  body: (empty)");
            return;
        }
        _out.WriteLine("  body:");
        foreach (var line in body.Replace("\r\n", "\n").Split('\n'))
            _out.WriteLine($"    | {line.TrimEnd('\r')}");
    }

    private void RenderWorkflowProfilePreview(JsonNode? data)
    {
        if (data is null)
        {
            _out.WriteLine("");
            return;
        }

        var renderedNode = data["rendered"];
        string? rendered = null;
        if (renderedNode is JsonValue jv && jv.TryGetValue<string>(out var s))
            rendered = s;

        if (rendered is null)
        {
            _out.WriteLine("");
            return;
        }

        foreach (var line in rendered.Replace("\r\n", "\n").Split('\n'))
            _out.WriteLine(line.TrimEnd('\r'));
    }
}
