using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal static partial class AgentCommands
{
    /// <summary>
    /// Validates typed agent config flags locally (before resolving the
    /// project or agent). Mirrors the server's canonical key set; the CLI
    /// cannot reference the Server assembly, so the accepted values live
    /// here. Returns the usage error, or <c>null</c> when the input is valid.
    /// </summary>
    private static string? ValidateAgentConfigInput(
        string? legacy,
        string? runtime,
        string? model,
        string? variant,
        string? reasoningEffort,
        bool clearRuntime,
        bool clearModel,
        bool clearVariant,
        bool clearReasoningEffort)
    {
        if (legacy is not null)
            return "--agent-config is retired; use --runtime, --model, --reasoning-effort, and --variant";

        var localConfig = ResolveTypedAgentConfig(
            current: null,
            legacy,
            runtime,
            model,
            reasoningEffort,
            variant,
            clearRuntime,
            clearModel,
            clearReasoningEffort,
            clearVariant);
        return localConfig.Error;
    }

    /// <summary>
    /// Resolves the typed agent config for a new agent (no existing config to
    /// merge and no clear flags on create). Mirrors
    /// <see cref="ResolveTypedAgentConfig"/> with the create-specific defaults.
    /// </summary>
    private static (JsonNode? Config, string? Error) ResolveNewAgentConfig(
        string? legacy,
        string? runtime,
        string? model,
        string? reasoningEffort,
        string? variant) =>
        ResolveTypedAgentConfig(
            current: null,
            legacy,
            runtime,
            model,
            reasoningEffort,
            variant,
            clearRuntime: false,
            clearModel: false,
            clearReasoningEffort: false,
            clearVariant: false);
}
