using System.Text.Json;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Project;
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
    private readonly ProjectDefaultExecutionConfigReader _defaults;

    public AgentReadinessService(AgentJobQuerier jobs, ProjectDefaultExecutionConfigReader defaults)
    {
        _jobs = jobs;
        _defaults = defaults;
    }

    public async Task<AgentReadinessResult> GetAsync(string projectId, AgentInfo agent, CancellationToken ct = default)
    {
        // The Project default is read once per request scope (cached in the
        // reader), so hydrating Readiness for an N-agent list costs one read.
        var projectDefault = await _defaults.GetAsync(projectId, ct);
        var history = await _jobs.GetLatestExecutionAsync(projectId, agent.Id, ct);
        return Evaluate(agent, history, projectDefault);
    }

    public async Task EnsureLaunchableAsync(string projectId, AgentInfo agent, CancellationToken ct = default)
    {
        var readiness = await GetAsync(projectId, agent, ct);
        if (readiness.Conclusion == AgentReadinessConclusions.NeedsSetup)
            throw new AgentReadinessException(readiness);
    }

    public static AgentReadinessResult Evaluate(
        AgentInfo agent,
        AgentExecutionHistory? history,
        ExecutionConfigHint? projectDefault = null)
    {
        var structuralGaps = StructuralGaps(agent, projectDefault);
        if (structuralGaps.Count > 0) return NeedsSetup(agent, structuralGaps);
        if (history is null || !MatchesCurrentDefinition(agent, history.Input, projectDefault)) return Unknown();
        if (history.Status == AgentJobStatus.Completed) return Ready();
        return history.Status == AgentJobStatus.Failed && IsConfigurationFailure(history.FailureCategory)
            ? NeedsSetup(agent, [new AgentReadinessGap("execution-config-failure", DescribeConfigurationFailure(history.FailureCategory), "Update the Agent settings and run it again.")])
            : Unknown();
    }

    /// <summary>
    /// Structural gaps resolve Model and Variant by Agent definition, then
    /// Project default — the same precedence rule as launch. A configured
    /// default therefore resolves <c>model-missing</c> and
    /// <c>variant-without-model</c>; definition errors
    /// (<c>model-reference-malformed</c>, <c>runtime-invalid</c>) are
    /// malformed explicit values and are never masked by a default.
    /// </summary>
    internal static List<AgentReadinessGap> StructuralGaps(AgentInfo agent, ExecutionConfigHint? projectDefault)
    {
        var gaps = new List<AgentReadinessGap>();
        if (string.IsNullOrWhiteSpace(agent.Instructions))
            gaps.Add(new("instructions-missing", "Instructions are missing.", "Add instructions in Agent settings."));

        var config = agent.AgentConfig is { ValueKind: JsonValueKind.Object } raw ? raw : (JsonElement?)null;
        var resolved = ExecutionConfigResolver.Resolve(
            callerHint: null,
            definition: ExecutionConfigResolver.FromAgentConfig(config),
            projectDefault: projectDefault);

        if (resolved.Model is null)
            gaps.Add(new(
                "model-missing",
                "A model is not configured.",
                "Set a model in Agent settings."));
        else if (!resolved.Model.Contains('/', StringComparison.Ordinal))
            gaps.Add(new("model-reference-malformed", "The model reference must use provider/model format.", "Set a valid model in Agent settings."));
        if (resolved.Model is null && resolved.Variant is not null)
            gaps.Add(new("variant-without-model", "A variant is set without a model.", "Set a model or remove the variant in Agent settings."));
        if (config is not null && AgentConfigSchema.ValidateRuntime(config.Value) is not null)
            gaps.Add(new("runtime-invalid", "The configured runtime is not supported.", "Choose opencode or pi in Agent settings."));
        return gaps;
    }

    /// <summary>
    /// Compares the resolved execution tuple — definition, then Project
    /// default — against the last execution's launch-time definition
    /// snapshot, with both sides resolved under the same (current) default.
    /// A Project-default change therefore cannot flip a completed
    /// execution: an Agent whose definition is unchanged still matches.
    ///
    /// Older AgentJobInput records predate the AgentConfig snapshot and have
    /// only the already-resolved dispatch fields. For those records, compare
    /// fields the current definition explicitly supplies and leave
    /// default-resolved fields free, preserving the pre-feature readiness
    /// result while retaining the full tuple comparison for new launches.
    /// </summary>
    private static bool MatchesCurrentDefinition(
        AgentInfo agent,
        AgentJobInput input,
        ExecutionConfigHint? projectDefault)
    {
        var definition = ExecutionConfigResolver.FromAgentConfig(agent.AgentConfig);
        var current = ExecutionConfigResolver.Resolve(null, definition, projectDefault);
        var launchDefinition = ExecutionConfigResolver.FromAgentConfig(input.AgentConfig);
        var matchesExecution = launchDefinition is not null
            ? MatchesResolvedTuple(
                current,
                ExecutionConfigResolver.Resolve(null, launchDefinition, projectDefault))
            : MatchesLegacyDispatch(definition, current, input);

        return string.Equals(agent.Instructions, input.AgentInstructions ?? string.Empty, StringComparison.Ordinal)
            && matchesExecution
            && agent.Skills.SequenceEqual(input.Skills ?? [], StringComparer.Ordinal);
    }

    private static bool MatchesResolvedTuple(
        ResolvedExecutionConfig current,
        ResolvedExecutionConfig atLaunch) =>
        string.Equals(current.Runtime, atLaunch.Runtime, StringComparison.Ordinal)
        && string.Equals(current.Model, atLaunch.Model, StringComparison.Ordinal)
        && string.Equals(current.Variant, atLaunch.Variant, StringComparison.Ordinal);

    private static bool MatchesLegacyDispatch(
        ExecutionConfigHint? definition,
        ResolvedExecutionConfig current,
        AgentJobInput input) =>
        (definition?.Runtime is null
            || string.Equals(current.Runtime, input.Runtime ?? AgentConfigSchema.OpenCodeRuntime, StringComparison.Ordinal))
        && (definition?.Model is null
            || string.Equals(current.Model, input.Model, StringComparison.Ordinal))
        && (definition?.Variant is null
            || string.Equals(current.Variant, input.Variant, StringComparison.Ordinal));

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
