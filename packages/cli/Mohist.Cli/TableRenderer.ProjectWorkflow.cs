using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal sealed partial class TableRenderer
{
    private void RenderWorkflowProfileList(JsonNode? data)
    {
        var rows = AsArray(data);
        if (rows.Count == 0)
        {
            _out.WriteLine("No workflow profiles");
            return;
        }

        var headers = new[] { "profile", "name", "runtime", "agent action", "source" };
        var widths = new[] { IdSoftCap, TitleSoftCap, 12, IdSoftCap, 12 };
        var cells = rows.Select(row =>
        {
            var agentAction = StringOf(row, "agentAction");
            var agentRuntime = StringOf(row, "agentRuntime");
            return new[]
            {
                Truncate(StringOf(row, "profileId"), IdSoftCap),
                Truncate(StringOf(row, "name"), TitleSoftCap),
                Truncate(string.IsNullOrEmpty(agentRuntime) ? "(none)" : agentRuntime, 12),
                Truncate(string.IsNullOrEmpty(agentAction) ? "(none)" : agentAction, IdSoftCap),
                Truncate(StringOf(row, "sourceProvenance"), 12),
            };
        }).ToList();

        WriteTable(headers, widths, cells);
    }

    private void RenderWorkflowProfileDetail(JsonNode? data)
    {
        if (data is null)
        {
            _out.WriteLine("");
            return;
        }

        var agentAction = StringOf(data, "agentAction");
        var agentRuntime = StringOf(data, "agentRuntime");
        _out.WriteLine($"profile:       {StringOf(data, "profileId")}");
        _out.WriteLine($"name:          {StringOf(data, "name")}");
        _out.WriteLine($"description:   {Truncate(StringOf(data, "description"), BodySoftCap)}");
        _out.WriteLine($"source:        {StringOf(data, "sourceProvenance")}");
        _out.WriteLine($"built in:      {(BoolOf(data, "isBuiltIn") ? "yes" : "no")}");
        _out.WriteLine($"agent action:  {(string.IsNullOrEmpty(agentAction) ? "(none)" : agentAction)}");
        _out.WriteLine($"agent runtime: {(string.IsNullOrEmpty(agentRuntime) ? "(none)" : agentRuntime)}");
    }

    private void RenderProjectTemplateList(JsonNode? data)
    {
        var rows = AsArray(data);
        if (rows.Count == 0)
        {
            _out.WriteLine("No workflow templates");
            return;
        }

        var headers = new[] { "template", "project", "created", "updated" };
        var widths = new[] { IdSoftCap, IdSoftCap, 24, 24 };

        var cells = new List<string[]>();
        foreach (var row in rows)
        {
            var templateId = StringOf(row, "templateId");
            var projectId = StringOf(row, "projectId");
            var createdAt = StringOf(row, "createdAt");
            var updatedAt = StringOf(row, "updatedAt");
            cells.Add(new[]
            {
                Truncate(templateId, IdSoftCap),
                Truncate(projectId, IdSoftCap),
                Truncate(createdAt, 24),
                Truncate(updatedAt, 24),
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

        var templateId = StringOf(data, "templateId");
        var projectId = StringOf(data, "projectId");
        var createdAt = StringOf(data, "createdAt");
        var updatedAt = StringOf(data, "updatedAt");
        var definition = DefinitionTextOf(data["definition"]);
        var deleted = BoolOf(data, "deleted");

        if (!string.IsNullOrEmpty(templateId))
            _out.WriteLine($"template: {templateId}");
        if (!string.IsNullOrEmpty(projectId))
            _out.WriteLine($"project:  {projectId}");
        if (!string.IsNullOrEmpty(createdAt))
            _out.WriteLine($"created:  {createdAt}");
        if (!string.IsNullOrEmpty(updatedAt))
            _out.WriteLine($"updated:  {updatedAt}");
        if (deleted)
        {
            _out.WriteLine("deleted:  yes");
            return;
        }

        if (!string.IsNullOrEmpty(definition))
        {
            _out.WriteLine("");
            _out.WriteLine("definition:");
            foreach (var line in definition.Split('\n'))
                _out.WriteLine($"  | {line.TrimEnd('\r')}");
        }
    }

    private static string DefinitionTextOf(JsonNode? definition)
    {
        if (definition is null)
            return "";
        if (definition is JsonValue value && value.TryGetValue<string>(out var text))
            return text;
        return definition.ToJsonString(MohistCliApi.JsonOutputOptions);
    }

    private void RenderProjectWorkflowProfile(JsonNode? data)
    {
        if (data is null)
        {
            _out.WriteLine("");
            return;
        }

        var defaultTemplateId = StringOf(data, "defaultTemplateId");
        var profileId = StringOf(data, "profileId");

        _out.WriteLine("workflow profile:");
        if (!string.IsNullOrEmpty(profileId))
            _out.WriteLine($"  profile:      {profileId}");
        _out.WriteLine($"  default template: {(string.IsNullOrEmpty(defaultTemplateId) ? "(none)" : defaultTemplateId)}");

        RenderWorkflowProfileVariables(data["variables"]);
        RenderProjectWorkflowProfilePrompts(data["prompts"]);
    }

    private void RenderProjectWorkflowProfilePrompts(JsonNode? prompts)
    {
        _out.WriteLine("");
        _out.WriteLine("prompts:");

        if (prompts is JsonObject obj)
        {
            foreach (var kvp in obj.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var raw = kvp.Value is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : null;
                if (raw is null)
                {
                    _out.WriteLine($"  {kvp.Key}: <invalid>");
                    continue;
                }

                var preview = Truncate(raw.Replace("\r\n", "\n"), 200);
                _out.WriteLine($"  {kvp.Key}:");
                foreach (var line in preview.Split('\n'))
                    _out.WriteLine($"    | {line.TrimEnd('\r')}");
            }
            return;
        }

        if (prompts is not JsonArray arr || arr.Count == 0)
        {
            _out.WriteLine("  (none)");
            return;
        }

        foreach (var prompt in arr.OfType<JsonObject>()
                     .OrderBy(p => StringOf(p, "key"), StringComparer.Ordinal))
        {
            var key = StringOf(prompt, "key");
            var source = StringOf(prompt, "source");
            var stage = StringOf(prompt, "stage");
            var body = StringOf(prompt, "body");
            var header = string.IsNullOrEmpty(key) ? "<unknown>" : key;
            var details = new List<string>();
            if (!string.IsNullOrEmpty(source))
                details.Add($"source: {source}");
            if (!string.IsNullOrEmpty(stage))
                details.Add($"stage: {stage}");

            _out.WriteLine(details.Count == 0
                ? $"  {header}:"
                : $"  {header} ({string.Join(", ", details)}):");

            if (string.IsNullOrEmpty(body))
            {
                _out.WriteLine("    body: (empty)");
                continue;
            }

            var preview = Truncate(body.Replace("\r\n", "\n"), 200);
            foreach (var line in preview.Split('\n'))
                _out.WriteLine($"    | {line.TrimEnd('\r')}");
        }
    }
}
