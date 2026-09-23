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

    public AgentReadinessService(AgentJobQuerier jobs)
    {
        _jobs = jobs;
    }

    public async Task<AgentExecutabilityResult> GetAsync(string projectId, AgentInfo agent, CancellationToken ct = default)
    {
        if (IsBuiltInAgent(agent)) return Unknown();
        var history = await _jobs.GetLatestExecutionAsync(projectId, agent.Id, ct);
        return Evaluate(agent, history);
    }

    public async Task EnsureLaunchableAsync(string projectId, AgentInfo agent, CancellationToken ct = default)
    {
        var executability = await GetAsync(projectId, agent, ct);
        if (AgentExecutabilityStates.IsBlocked(executability.State))
            throw new AgentExecutabilityException(executability);
    }

    public static AgentExecutabilityResult Evaluate(
        AgentInfo agent,
        AgentExecutionHistory? history)
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

    /// <summary>
    /// Structural gaps are decision-free configuration facts about the Agent
    /// definition itself: missing Instructions, an explicitly malformed Model
    /// reference, or an explicitly unsupported Runtime. An unset Model is not
    /// a gap — the Runtime chooses the model at dispatch and the Job snapshot
    /// records that choice — and a Variant or Reasoning Effort without a Model
    /// is not a gap either, because both apply to the Runtime-chosen model.
    /// Malformed explicit values are never masked by any other source.
    /// </summary>
    internal static List<AgentExecutabilityGap> StructuralGaps(AgentInfo agent)
    {
        var gaps = new List<AgentExecutabilityGap>();
        if (string.IsNullOrWhiteSpace(agent.Instructions))
            gaps.Add(Gap(agent, "instructions-missing", "Instructions are missing.", "Add instructions in Agent settings."));

        var config = agent.AgentConfig is { ValueKind: JsonValueKind.Object } raw ? raw : (JsonElement?)null;
        var resolved = ExecutionConfigResolver.Resolve(
            callerHint: null,
            definition: ExecutionConfigResolver.FromAgentConfig(config));

        if (resolved.Model is not null
            && !string.Equals(resolved.Runtime, AgentConfigSchema.CodexRuntime, StringComparison.OrdinalIgnoreCase)
            && !resolved.Model.Contains('/', StringComparison.Ordinal))
            gaps.Add(Gap(agent, "model-reference-malformed", "The model reference must use provider/model format.", "Set a valid model in Agent settings."));
        if (config is not null && AgentConfigSchema.ValidateRuntime(config.Value) is not null)
            gaps.Add(Gap(agent, "runtime-invalid", "The configured runtime is not supported.", "Choose opencode, pi, or codex in Agent settings."));
        return gaps;
    }

    /// <summary>
    /// Compares the resolved execution tuple — caller hint or Agent
    /// definition — against the last execution's launch-time definition
    /// snapshot. A definition edit therefore flips a completed execution to
    /// Unknown; a completed execution whose definition is unchanged never
    /// flips.
    ///
    /// Older AgentJobInput records predate the AgentConfig snapshot and have
    /// only the already-resolved dispatch fields. For those records, compare
    /// fields the current definition explicitly supplies and leave
    /// runtime-defaulted fields free, preserving the pre-feature readiness
    /// result while retaining the full tuple comparison for new launches.
    /// </summary>
    private static bool MatchesCurrentDefinition(
        AgentInfo agent,
        AgentJobInput input)
    {
        var definition = ExecutionConfigResolver.FromAgentConfig(agent.AgentConfig);
        var current = ExecutionConfigResolver.Resolve(null, definition);
        var launchDefinition = ExecutionConfigResolver.FromAgentConfig(input.AgentConfig);
        var matchesExecution = launchDefinition is not null
            ? MatchesResolvedTuple(
                current,
                ExecutionConfigResolver.Resolve(null, launchDefinition))
            : MatchesLegacyDispatch(definition, current, input);

        return string.Equals(agent.Instructions, input.AgentInstructions ?? string.Empty, StringComparison.Ordinal)
            && matchesExecution
            && string.Equals(AgentLauncher.ResolveReasoningEffort(agent.AgentConfig), input.ReasoningEffort, StringComparison.Ordinal)
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
