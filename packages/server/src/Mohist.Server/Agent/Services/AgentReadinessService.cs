using System.Text.Json;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Agent.Services;

public static class AgentReadinessConclusions
{
    public const string Ready = "Ready";
    public const string NeedsSetup = "Needs setup";
    public const string Unknown = "Unknown";
}

[GenerateSerializer]
public sealed record AgentReadinessGap(
    [property: Id(0)] string Code,
    [property: Id(1)] string Message,
    [property: Id(2)] string Action);

[GenerateSerializer]
public sealed record AgentReadinessSetup(
    [property: Id(0)] string Label,
    [property: Id(1)] string Path);

[GenerateSerializer]
public sealed record AgentReadinessResult(
    [property: Id(0)] string Conclusion,
    [property: Id(1)] IReadOnlyList<AgentReadinessGap> Gaps,
    [property: Id(2)] AgentReadinessSetup? Setup);

public sealed class AgentReadinessException : Exception
{
    public AgentReadinessResult Result { get; }

    public AgentReadinessException(AgentReadinessResult result)
        : base("This Agent needs setup before it can accept new work.") => Result = result;
}

public sealed class AgentReadinessService : IScopedService
{
    private readonly AgentJobQuerier _jobs;

    public AgentReadinessService(AgentJobQuerier jobs) => _jobs = jobs;

    public async Task<AgentReadinessResult> GetAsync(string projectId, AgentInfo agent, CancellationToken ct = default)
    {
        var structuralGaps = StructuralGaps(agent);
        if (structuralGaps.Count > 0) return NeedsSetup(agent, structuralGaps);
        return Evaluate(agent, await _jobs.GetLatestExecutionAsync(projectId, agent.Id, ct));
    }

    public async Task EnsureLaunchableAsync(string projectId, AgentInfo agent, CancellationToken ct = default)
    {
        var readiness = await GetAsync(projectId, agent, ct);
        if (readiness.Conclusion == AgentReadinessConclusions.NeedsSetup)
            throw new AgentReadinessException(readiness);
    }

    public static AgentReadinessResult Evaluate(AgentInfo agent, AgentExecutionHistory? history)
    {
        var structuralGaps = StructuralGaps(agent);
        if (structuralGaps.Count > 0) return NeedsSetup(agent, structuralGaps);
        if (history is null || !MatchesCurrentDefinition(agent, history.Input)) return Unknown();
        if (history.Status == AgentJobStatus.Completed) return Ready();
        return history.Status == AgentJobStatus.Failed && IsConfigurationFailure(history.FailureCategory)
            ? NeedsSetup(agent, [new AgentReadinessGap("execution-config-failure", DescribeConfigurationFailure(history.FailureCategory), "Update the Agent settings and run it again.")])
            : Unknown();
    }

    private static List<AgentReadinessGap> StructuralGaps(AgentInfo agent)
    {
        var gaps = new List<AgentReadinessGap>();
        if (string.IsNullOrWhiteSpace(agent.Instructions))
            gaps.Add(new("instructions-missing", "Instructions are missing.", "Add instructions in Agent settings."));

        var (model, variant) = AgentLauncher.ResolveModelAndVariant(agent.AgentConfig);
        if (agent.AgentConfig is null
            || agent.AgentConfig.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            gaps.Add(new(
                "model-missing",
                "A model is not configured.",
                "Set a model in Agent settings."));
            return gaps;
        }
        if (agent.AgentConfig.Value.ValueKind != JsonValueKind.Object)
            return gaps;

        if (string.IsNullOrWhiteSpace(model))
            gaps.Add(new(
                "model-missing",
                "A model is not configured.",
                "Set a model in Agent settings."));
        if (agent.AgentConfig is { ValueKind: JsonValueKind.Object } rawConfig
            && rawConfig.TryGetProperty("variant", out var rawVariant)
            && rawVariant.ValueKind == JsonValueKind.String)
            variant = rawVariant.GetString();
        if (!string.IsNullOrWhiteSpace(model) && !model.Contains('/', StringComparison.Ordinal))
            gaps.Add(new("model-reference-malformed", "The model reference must use provider/model format.", "Set a valid model in Agent settings."));
        if (!string.IsNullOrWhiteSpace(variant) && string.IsNullOrWhiteSpace(model))
            gaps.Add(new("variant-without-model", "A variant is set without a model.", "Set a model or remove the variant in Agent settings."));
        if (agent.AgentConfig is { ValueKind: JsonValueKind.Object } config && AgentConfigSchema.ValidateRuntime(config) is not null)
            gaps.Add(new("runtime-invalid", "The configured runtime is not supported.", "Choose opencode or pi in Agent settings."));
        return gaps;
    }

    private static bool MatchesCurrentDefinition(AgentInfo agent, AgentJobInput input)
    {
        var current = AgentLauncher.ResolveModelAndVariant(agent.AgentConfig);
        return string.Equals(agent.Instructions, input.AgentInstructions ?? string.Empty, StringComparison.Ordinal)
            && string.Equals(AgentLauncher.ResolveRuntime(agent.AgentConfig), input.Runtime ?? AgentConfigSchema.OpenCodeRuntime, StringComparison.Ordinal)
            && string.Equals(current.Model, input.Model, StringComparison.Ordinal)
            && string.Equals(current.Variant, input.Variant, StringComparison.Ordinal)
            && agent.Skills.SequenceEqual(input.Skills ?? [], StringComparer.Ordinal);
    }

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
            || value.Contains("incompatible-runtime") || value.Contains("runtime-rejected");
    }

    private static string DescribeConfigurationFailure(string? category) =>
        category?.Contains("api_key", StringComparison.OrdinalIgnoreCase) == true
        || category?.Contains("credential", StringComparison.OrdinalIgnoreCase) == true
        || category?.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) == true
            ? "The runtime could not authenticate with the configured provider."
            : category?.Contains("model", StringComparison.OrdinalIgnoreCase) == true
                ? "The configured model could not be used by the runtime."
                : "The configured runtime rejected this Agent definition.";

    private static AgentReadinessResult Ready() => new(AgentReadinessConclusions.Ready, [], null);
    private static AgentReadinessResult Unknown() => new(AgentReadinessConclusions.Unknown, [], null);
    private static AgentReadinessResult NeedsSetup(AgentInfo agent, IReadOnlyList<AgentReadinessGap> gaps) => new(
        AgentReadinessConclusions.NeedsSetup,
        gaps,
        new AgentReadinessSetup("Agent settings", $"/agents/{agent.Id}"));
}
