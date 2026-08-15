using System.CommandLine;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal static partial class AgentCommands
{
    private static string? ValidateClearSetPair(string setFlag, bool setProvided, string clearFlag, bool clearProvided)
    {
        return setProvided && clearProvided
            ? $"{setFlag} cannot be used with {clearFlag}"
            : null;
    }

    private static (JsonNode? Config, string? Error) ResolveTypedAgentConfig(
        JsonNode? current,
        string? legacy,
        string? runtime,
        string? model,
        string? variant,
        bool clearRuntime,
        bool clearModel,
        bool clearVariant)
    {
        if (legacy is not null)
            return (null, "--agent-config is retired; use --runtime, --model, and --variant");

        if (runtime is not null && runtime is not ("opencode" or "pi"))
            return (null, $"--runtime '{runtime}' is invalid; use opencode or pi");
        if (model is not null && string.IsNullOrWhiteSpace(model))
            return (null, "--model must not be empty; use provider/model");
        if (variant is not null && string.IsNullOrWhiteSpace(variant))
            return (null, "--variant must not be empty; use the variant supported by the selected runtime");

        var supplied = runtime is not null || model is not null || variant is not null
            || clearRuntime || clearModel || clearVariant;
        if (!supplied)
            return (null, null);

        var config = new JsonObject();
        if (current is JsonObject existing)
        {
            foreach (var key in new[] { "runtime", "model", "variant" })
            {
                if (existing[key] is JsonNode value)
                    config[key] = value.DeepClone();
            }
        }

        if (clearRuntime) config.Remove("runtime");
        if (clearModel) config.Remove("model");
        if (clearVariant) config.Remove("variant");
        if (runtime is not null) config["runtime"] = runtime;
        if (model is not null) config["model"] = model;
        if (variant is not null) config["variant"] = variant;

        return (config.Count == 0 ? null : config, null);
    }

    private static string[]? ParseSkills(string? value) => string.IsNullOrWhiteSpace(value)
        ? null
        : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static string[]? ParsePermissions(string? value) => string.IsNullOrWhiteSpace(value)
        ? null
        : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static string? ValidateSkills(string? value) =>
        value is null || ParseSkills(value) is { Length: > 0 }
            ? null
            : "--skills must contain at least one non-empty skill name; omit it or use --clear-skills to clear existing skills";

    private static string? ValidatePermissions(string? value) =>
        value is null || ParsePermissions(value) is { Length: > 0 }
            ? null
            : "--permissions must contain at least one non-empty permission term";

    private static Option<string[]?> AllowedSubagentOption() => new("--allowed-subagent")
    {
        Description = "Allowed subagent stable agent id/ref. Repeat for multiple subagents.",
        AllowMultipleArgumentsPerToken = true,
    };
}
