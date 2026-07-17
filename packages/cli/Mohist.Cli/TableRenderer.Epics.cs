using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal sealed partial class TableRenderer
{
    private void RenderEpicList(JsonNode? data)
    {
        var rows = AsArray(data);
        if (rows.Count == 0)
        {
            _out.WriteLine("No epics");
            return;
        }

        var headers = new[] { "number", "title", "status", "priority" };
        var widths = new[] { 7, TitleSoftCap, 12, 9 };

        var cells = new List<string[]>();
        foreach (var row in rows)
        {
            var number = NumberOf(row, "number");
            var title = StringOf(row, "title");
            var status = StringOf(row, "status");
            var priority = StringOf(row, "priority");
            cells.Add(new[]
            {
                number,
                Truncate(title, TitleSoftCap),
                Truncate(status, 12),
                Truncate(priority, 9),
            });
        }

        WriteTable(headers, widths, cells);
    }

    private void RenderEpicShow(JsonNode? data)
    {
        if (data is null)
        {
            _out.WriteLine("");
            return;
        }

        var number = NumberOf(data, "number");
        var title = StringOf(data, "title");
        var status = StringOf(data, "status");
        var priority = StringOf(data, "priority");
        var description = StringOf(data, "description");
        var progress = data["progress"] as JsonObject;
        var delivered = NumberOf(progress, "deliveredCount");
        var total = NumberOf(progress, "totalIssueCount");
        var nextIssue = progress?["nextIssue"] as JsonObject;
        var linked = data["linkedIssues"] as JsonArray;

        _out.WriteLine($"number:     {number}");
        _out.WriteLine($"title:      {Truncate(title, TitleSoftCap)}");
        _out.WriteLine($"status:     {status}");
        _out.WriteLine($"priority:   {priority}");
        if (!string.IsNullOrEmpty(description))
            _out.WriteLine($"description:{Truncate(description, BodySoftCap)}");
        _out.WriteLine($"progress:   {delivered}/{total} delivered");
        if (nextIssue is not null)
        {
            var nextNumber = NumberOf(nextIssue, "number");
            var nextTitle = StringOf(nextIssue, "title");
            _out.WriteLine($"next issue: #{nextNumber} {Truncate(nextTitle, TitleSoftCap)}");
        }

        if (linked is null || linked.Count == 0)
        {
            _out.WriteLine("linked issues: (none)");
            return;
        }

        _out.WriteLine("");
        var headers = new[] { "number", "title", "status", "priority" };
        var widths = new[] { 7, TitleSoftCap, 12, 9 };
        var cells = new List<string[]>();
        foreach (var row in linked)
        {
            var linkedNumber = NumberOf(row, "number");
            var linkedTitle = StringOf(row, "title");
            var linkedStatus = StringOf(row, "status");
            var linkedPriority = StringOf(row, "priority");
            cells.Add(new[]
            {
                linkedNumber,
                Truncate(linkedTitle, TitleSoftCap),
                Truncate(linkedStatus, 12),
                Truncate(linkedPriority, 9),
            });
        }
        WriteTable(headers, widths, cells);
    }

    private void RenderEpicMembership(JsonNode? data, string verb)
    {
        if (data is null)
        {
            _out.WriteLine("OK");
            return;
        }

        var epicNumber = NumberOf(data, "epicNumber");
        var issueNumber = NumberOf(data, "issueNumber");
        if (!string.IsNullOrEmpty(epicNumber) && !string.IsNullOrEmpty(issueNumber))
            _out.WriteLine($"{verb} issue #{issueNumber} {(verb == "Linked" ? "to" : "from")} epic #{epicNumber}");
        else
            _out.WriteLine("OK");
    }
}
