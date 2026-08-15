using System.Text.Json;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Agent.Services;

public static class AgentExecutabilityStates
{
    public const string NotConfigured = "not-configured";
    public const string NotExecutable = "not-executable";
    public const string Unknown = "unknown";
    public const string Executable = "executable";

    public static bool IsBlocked(string state) => state is NotConfigured or NotExecutable;
}

[GenerateSerializer]
public sealed record AgentExecutabilityFixEntryPoint(
    [property: Id(0)] string Label,
    [property: Id(1)] string Path,
    [property: Id(2)] string Command);

[GenerateSerializer]
public sealed record AgentExecutabilityGap(
    [property: Id(0)] string Code,
    [property: Id(1)] string Message,
    [property: Id(2)] string NextAction,
    [property: Id(3)] AgentExecutabilityFixEntryPoint FixEntryPoint);

[GenerateSerializer]
public sealed record AgentExecutabilityResult(
    [property: Id(0)] string State,
    [property: Id(1)] IReadOnlyList<AgentExecutabilityGap> Gaps,
    [property: Id(2)] string? PendingLaunchNote);

public sealed class AgentExecutabilityException : Exception
{
    public AgentExecutabilityResult Result { get; }

    public AgentExecutabilityException(AgentExecutabilityResult result)
        : base($"This Agent is {result.State} and cannot accept new work.") => Result = result;

    public string ErrorCode => Result.State switch
    {
        AgentExecutabilityStates.NotConfigured => "agent_not_configured",
        AgentExecutabilityStates.NotExecutable => "agent_not_executable",
        _ => "agent_not_executable",
    };
}

public sealed class AgentReadinessService : IScopedService
{
    private readonly AgentJobQuerier _jobs;

    public AgentReadinessService(AgentJobQuerier jobs) => _jobs = jobs;

    public async Task<AgentExecutabilityResult> GetAsync(string projectId, AgentInfo agent, CancellationToken ct = default)
    {
        if (IsBuiltInAgent(agent)) return Unknown();
        var structuralGaps = StructuralGaps(agent);
        if (structuralGaps.Count > 0) return NotConfigured(structuralGaps);
        return Evaluate(agent, await _jobs.GetLatestExecutionAsync(projectId, agent.Id, ct));
    }

    public async Task EnsureLaunchableAsync(string projectId, AgentInfo agent, CancellationToken ct = default)
    {
        var executability = await GetAsync(projectId, agent, ct);
        if (AgentExecutabilityStates.IsBlocked(executability.State))
            throw new AgentExecutabilityException(executability);
    }

    public static AgentExecutabilityResult Evaluate(AgentInfo agent, AgentExecutionHistory? history)
    {
        if (IsBuiltInAgent(agent)) return Unknown();
        var structuralGaps = StructuralGaps(agent);
        if (structuralGaps.Count > 0) return NotConfigured(structuralGaps);
        if (history is null || !MatchesCurrentDefinition(agent, history.Input)) return Unknown();
        if (history.Status == AgentJobStatus.Completed) return Executable();
        return history.Status == AgentJobStatus.Failed && IsConfigurationFailure(history.FailureCategory)
            ? NotExecutable(agent, history.FailureCategory)
            : Unknown();
    }

    private static List<AgentExecutabilityGap> StructuralGaps(AgentInfo agent)
    {
        var gaps = new List<AgentExecutabilityGap>();
        if (string.IsNullOrWhiteSpace(agent.Instructions))
            gaps.Add(Gap(agent, "instructions-missing", "Instructions are missing.", "Add instructions in Agent settings."));

        var (model, variant) = AgentLauncher.ResolveModelAndVariant(agent.AgentConfig);
        var reasoningEffort = AgentLauncher.ResolveReasoningEffort(agent.AgentConfig);
        if (agent.AgentConfig is null
            || agent.AgentConfig.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            gaps.Add(Gap(
                agent,
                "model-missing",
                "A model is not configured.",
                "Set a model in Agent settings."));
            return gaps;
        }
        if (agent.AgentConfig.Value.ValueKind != JsonValueKind.Object)
            return gaps;

        if (string.IsNullOrWhiteSpace(model))
            gaps.Add(Gap(
                agent,
                "model-missing",
                "A model is not configured.",
                "Set a model in Agent settings."));
        if (agent.AgentConfig is { ValueKind: JsonValueKind.Object } rawConfig
            && rawConfig.TryGetProperty("variant", out var rawVariant)
            && rawVariant.ValueKind == JsonValueKind.String)
            variant = rawVariant.GetString();
        if (!string.IsNullOrWhiteSpace(model) && !model.Contains('/', StringComparison.Ordinal))
            gaps.Add(Gap(agent, "model-reference-malformed", "The model reference must use provider/model format.", "Set a valid model in Agent settings."));
        if (!string.IsNullOrWhiteSpace(variant) && string.IsNullOrWhiteSpace(model))
            gaps.Add(Gap(agent, "variant-without-model", "A variant is set without a model.", "Set a model or remove the variant in Agent settings."));
        if (!string.IsNullOrWhiteSpace(reasoningEffort) && string.IsNullOrWhiteSpace(model))
            gaps.Add(Gap(agent, "reasoning-effort-without-model", "A reasoning effort is set without a model.", "Set a model or remove the reasoning effort in Agent settings."));
        if (agent.AgentConfig is { ValueKind: JsonValueKind.Object } config && AgentConfigSchema.ValidateRuntime(config) is not null)
            gaps.Add(Gap(agent, "runtime-invalid", "The configured runtime is not supported.", "Choose opencode or pi in Agent settings."));
        return gaps;
    }

    private static bool MatchesCurrentDefinition(AgentInfo agent, AgentJobInput input)
    {
        var current = AgentLauncher.ResolveModelAndVariant(agent.AgentConfig);
        return string.Equals(agent.Instructions, input.AgentInstructions ?? string.Empty, StringComparison.Ordinal)
            && string.Equals(AgentLauncher.ResolveRuntime(agent.AgentConfig), input.Runtime ?? AgentConfigSchema.OpenCodeRuntime, StringComparison.Ordinal)
            && string.Equals(current.Model, input.Model, StringComparison.Ordinal)
            && string.Equals(current.Variant, input.Variant, StringComparison.Ordinal)
            && string.Equals(AgentLauncher.ResolveReasoningEffort(agent.AgentConfig), input.ReasoningEffort, StringComparison.Ordinal)
            && agent.Skills.SequenceEqual(input.Skills ?? [], StringComparer.Ordinal);
    }

    private static bool IsBuiltInAgent(AgentInfo agent) =>
        string.Equals(agent.Id, $"builtin:{BuiltInAgentCatalog.MohistSlackName}", StringComparison.Ordinal)
        && string.Equals(agent.ProjectId, BuiltInAgentCatalog.MohistSlackProjectId, StringComparison.Ordinal)
        && string.Equals(agent.Name, BuiltInAgentCatalog.MohistSlackName, StringComparison.Ordinal);

    private static bool IsConfigurationFailure(string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return false;
        var value = category.Trim().ToLowerInvariant().Replace('_', '-');
        if (value.Contains("runtime-unavailable")
            || value.Contains("unavailable-runtime")
            || value.Contains("runner-unavailable"))
            return false;

        return value.Contains("api-key") || value.Contains("credential") || value.Contains("unauthorized")
            || value.Contains("model not found") || value.Contains("model-not-found")
            || value.Contains("model-rejected") || value.Contains("preflight-rejected")
            || value.Contains("runtime-invalid") || value.Contains("invalid-runtime")
            || value.Contains("incompatible-runtime") || value.Contains("runtime-rejected")
            || value.Contains("unsupported-execution-configuration")
            || value.Contains("incompatible-execution-configuration");
    }

    private static string DescribeConfigurationFailure(string? category) =>
        category?.Contains("api_key", StringComparison.OrdinalIgnoreCase) == true
        || category?.Contains("credential", StringComparison.OrdinalIgnoreCase) == true
        || category?.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) == true
            ? "The runtime could not authenticate with the configured provider."
            : category?.Contains("model", StringComparison.OrdinalIgnoreCase) == true
                ? "The configured model could not be used by the runtime."
                : "The configured runtime rejected this Agent definition.";

    private static AgentExecutabilityGap Gap(AgentInfo agent, string code, string message, string nextAction) => new(
        code,
        message,
        nextAction,
        new AgentExecutabilityFixEntryPoint("Agent settings", $"/agents/{agent.Id}", $"mo agent edit {agent.Id}"));

    private static AgentExecutabilityResult NotConfigured(IReadOnlyList<AgentExecutabilityGap> gaps) => new(
        AgentExecutabilityStates.NotConfigured,
        gaps,
        null);

    private static AgentExecutabilityResult NotExecutable(AgentInfo agent, string? failureCategory) => new(
        AgentExecutabilityStates.NotExecutable,
        [Gap(
            agent,
            "execution-config-failure",
            DescribeConfigurationFailure(failureCategory),
            "Update the Agent execution settings and run it again.")],
        null);

    private static AgentExecutabilityResult Executable() => new(
        AgentExecutabilityStates.Executable,
        [],
        null);

    private static AgentExecutabilityResult Unknown() => new(
        AgentExecutabilityStates.Unknown,
        [],
        "No matching execution evidence exists. This launch is accepted and awaits Runner verification.");
}
