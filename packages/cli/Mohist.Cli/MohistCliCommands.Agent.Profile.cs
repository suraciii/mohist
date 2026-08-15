using System.CommandLine;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal static partial class AgentCommands
{
    private static string? ValidateClearSetPair(string setFlag, bool setProvided, string clearFlag, bool clearProvided) =>
        setProvided && clearProvided ? $"{setFlag} cannot be used with {clearFlag}" : null;

    private static void AddIfProvided(JsonObject body, string property, string? value, bool provided = true)
    {
        if (provided) body[property] = value;
    }

    private static void AddIfProvided(JsonObject body, string property, int? value, bool provided)
    {
        if (provided) body[property] = value;
    }

    private static void AddIfProvided(JsonObject body, string property, JsonNode? value, bool provided)
    {
        if (provided) body[property] = value;
    }

    private static string[]? ParseSkills(string? value) => ParseCommaSeparated(value);

    private static string[]? ParsePermissions(string? value) => ParseCommaSeparated(value);

    private static string[]? ParseCommaSeparated(string? value) => string.IsNullOrWhiteSpace(value)
        ? null
        : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static string? ValidateSkills(string? value) =>
        value is null || ParseSkills(value) is { Length: > 0 }
            ? null
            : "--skills must contain at least one non-empty skill name; omit it or use --clear-skills to clear existing skills";

    private static Option<string[]?> AllowedSubagentOption() => new("--allowed-subagent")
    {
        Description = "Allowed subagent stable agent id/ref. Repeat for multiple subagents.",
        AllowMultipleArgumentsPerToken = true,
    };
}
