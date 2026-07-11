using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal sealed partial class TableRenderer
{
    private void RenderDeadLetterList(JsonNode? data)
    {
        var rows = AsArray(data);
        if (rows.Count == 0)
        {
            _out.WriteLine("No unresolved dead letters");
            return;
        }

        var cells = new List<string[]>();
        foreach (var row in rows.OfType<JsonObject>())
        {
            cells.Add([
                NumberOf(row, "id"),
                Truncate(StringOf(row, "type"), 42),
                Truncate(StringOf(row, "handler"), 44),
                NumberOf(row, "attempts"),
                Truncate(StringOf(row, "deadLetteredAt"), 25),
                Truncate(StringOf(row, "error"), 60),
            ]);
        }

        WriteTable(
            ["id", "type", "handler", "attempts", "dead-lettered", "error"],
            [10, 42, 44, 8, 25, 60],
            cells);
    }

    private void RenderDeadLetterRedelivery(JsonNode? data)
    {
        if (data is not JsonObject row)
        {
            _out.WriteLine("Dead-letter re-delivery returned no result");
            return;
        }

        var status = BoolOf(row, "delivered")
            ? "delivered"
            : "failed";
        _out.WriteLine($"Dead-letter {NumberOf(row, "id")}: {status} after {NumberOf(row, "attempts")} attempt(s)");
        var error = StringOf(row, "error");
        if (!string.IsNullOrWhiteSpace(error))
            _out.WriteLine(error);
    }
}
