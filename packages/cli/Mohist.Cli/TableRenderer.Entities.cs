using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal sealed partial class TableRenderer
{
    private void RenderProjectList(JsonNode? data)
    {
        var rows = AsArray(data);
        if (rows.Count == 0)
        {
            _out.WriteLine("No projects");
            return;
        }

        var hasBaseBranch = rows.Any(r => !string.IsNullOrEmpty(StringOf(r, "baseBranch")));

        if (!hasBaseBranch)
        {
            foreach (var row in rows)
            {
                var id = StringOf(row, "id");
                var name = StringOf(row, "name");
                var marker = !string.IsNullOrEmpty(_activeProjectId) &&
                             string.Equals(id, _activeProjectId, StringComparison.Ordinal)
                    ? "* "
                    : "  ";
                _out.WriteLine($"{marker}{name}");
            }
            return;
        }

        var headers = new[] { "*", "id", "name", "base branch" };
        var widths = new[] { 1, IdSoftCap, TitleSoftCap, 16 };

        var cells = new List<string[]>();
        foreach (var row in rows)
        {
            var id = StringOf(row, "id");
            var name = StringOf(row, "name");
            var baseBranch = StringOf(row, "baseBranch");
            var marker = !string.IsNullOrEmpty(_activeProjectId) &&
                         string.Equals(id, _activeProjectId, StringComparison.Ordinal)
                ? "*"
                : "";
            cells.Add(new[] { marker, Truncate(id, IdSoftCap), Truncate(name, TitleSoftCap), Truncate(baseBranch, 16) });
        }

        WriteTable(headers, widths, cells);
    }

    private void RenderProjectShow(JsonNode? data)
    {
        if (data is null)
        {
            _out.WriteLine("");
            return;
        }

        var id = StringOf(data, "id");
        var name = StringOf(data, "name");
        var baseBranch = StringOf(data, "baseBranch");
        var createdAt = StringOf(data, "createdAt");
        var updatedAt = StringOf(data, "updatedAt");
        var repos = data["repositories"] as JsonArray;
        var repoCount = repos?.Count.ToString() ?? "0";

        _out.WriteLine($"id:          {id}");
        _out.WriteLine($"name:        {name}");
        _out.WriteLine($"base branch: {baseBranch}");
        _out.WriteLine($"repositories:{repoCount}");
        _out.WriteLine($"created:     {Truncate(createdAt, TitleSoftCap)}");
        _out.WriteLine($"updated:     {Truncate(updatedAt, TitleSoftCap)}");
    }

    private void RenderAgentList(JsonNode? data)
    {
        var rows = AsArray(data);
        var headers = new[] { "id", "name", "status", "updatedAt" };
        var widths = new[] { IdSoftCap, 24, 12, 24 };

        var cells = new List<string[]>();
        foreach (var row in rows)
        {
            cells.Add(new[]
            {
                Truncate(StringOf(row, "id"), IdSoftCap),
                Truncate(StringOf(row, "name"), 24),
                Truncate(StringOf(row, "status"), 12),
                Truncate(StringOf(row, "updatedAt"), 24),
            });
        }

        WriteTable(headers, widths, cells);
    }

    private void RenderAgentShow(JsonNode? data)
    {
        if (data is null)
        {
            _out.WriteLine("");
            return;
        }

        var skills = data["skills"] as JsonArray;
        var skillText = skills is null ? "" : string.Join(",", skills.Select(s => s?.GetValue<string>() ?? "").Where(s => !string.IsNullOrWhiteSpace(s)));

        _out.WriteLine($"id:                  {StringOf(data, "id")}");
        _out.WriteLine($"name:                {StringOf(data, "name")}");
        _out.WriteLine($"status:              {StringOf(data, "status")}");
        _out.WriteLine($"description:         {Truncate(StringOf(data, "description"), TitleSoftCap)}");
        _out.WriteLine($"max concurrent runs: {NumberOf(data, "maxConcurrentRuns")}");
        _out.WriteLine($"skills:              {skillText}");
        _out.WriteLine($"createdAt:           {Truncate(StringOf(data, "createdAt"), TitleSoftCap)}");
        _out.WriteLine($"updatedAt:           {Truncate(StringOf(data, "updatedAt"), TitleSoftCap)}");
        var instructions = StringOf(data, "instructions");
        if (!string.IsNullOrEmpty(instructions))
            _out.WriteLine($"instructions:        {Truncate(instructions, BodySoftCap)}");
    }
}
