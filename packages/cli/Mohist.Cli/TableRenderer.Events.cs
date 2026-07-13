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
                Truncate(SanitizeTerminalText(StringOf(row, "type")), 42),
                Truncate(SanitizeTerminalText(StringOf(row, "handler")), 44),
                Truncate(SanitizeTerminalText(StringOf(row, "status")), 12),
                NumberOf(row, "attempts"),
                Truncate(SanitizeTerminalText(StringOf(row, "deadLetteredAt")), 25),
                Truncate(SanitizeTerminalText(StringOf(row, "error")), 60),
            ]);
        }

        WriteTable(
            ["id", "type", "handler", "status", "attempts", "dead-lettered", "error"],
            [10, 42, 44, 12, 8, 25, 60],
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
            _out.WriteLine(SanitizeTerminalText(error));
    }

    private static string SanitizeTerminalText(string value)
    {
        var result = new System.Text.StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character is '\r' or '\n')
                break;
            if (character == '\u001b')
            {
                if (index + 1 < value.Length && value[index + 1] == '[')
                {
                    index += 2;
                    while (index < value.Length && value[index] is < '@' or > '~')
                        index++;
                }
                else if (index + 1 < value.Length)
                {
                    index++;
                }
                continue;
            }
            if (!char.IsControl(character))
                result.Append(character);
        }
        return result.ToString();
    }
}
