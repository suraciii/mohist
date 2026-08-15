using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal static partial class AgentCommands
{
    private static (JsonNode? Config, string? Error) ResolveTypedAgentConfig(
        JsonNode? current,
        string? legacy,
        string? runtime,
        string? model,
        string? reasoningEffort,
        string? variant,
        bool clearRuntime,
        bool clearModel,
        bool clearReasoningEffort,
        bool clearVariant)
    {
        if (legacy is not null)
            return (null, "--agent-config is retired; use --runtime, --model, --reasoning-effort, and --variant");

        if (runtime is not null && runtime is not ("opencode" or "pi"))
            return (null, $"--runtime '{runtime}' is invalid; use opencode or pi");
        if (model is not null && string.IsNullOrWhiteSpace(model))
            return (null, "--model must not be empty; use provider/model");
        if (reasoningEffort is not null && string.IsNullOrWhiteSpace(reasoningEffort))
            return (null, "--reasoning-effort must not be empty; use a canonical reasoning effort");
        if (variant is not null && string.IsNullOrWhiteSpace(variant))
            return (null, "--variant must not be empty; use the variant supported by the selected runtime");

        var supplied = runtime is not null || model is not null || reasoningEffort is not null || variant is not null
            || clearRuntime || clearModel || clearReasoningEffort || clearVariant;
        if (!supplied)
            return (null, null);

        var config = new JsonObject();
        if (current is JsonObject existing)
        {
            foreach (var key in new[] { "runtime", "model", "reasoningEffort", "variant" })
            {
                if (existing[key] is JsonNode value)
                    config[key] = value.DeepClone();
            }
        }

        if (clearRuntime) config.Remove("runtime");
        if (clearModel) config.Remove("model");
        if (clearReasoningEffort) config.Remove("reasoningEffort");
        if (clearVariant) config.Remove("variant");
        if (runtime is not null) config["runtime"] = runtime;
        if (model is not null) config["model"] = model;
        if (reasoningEffort is not null) config["reasoningEffort"] = reasoningEffort;
        if (variant is not null) config["variant"] = variant;

        return (config.Count == 0 ? null : config, null);
    }
}
