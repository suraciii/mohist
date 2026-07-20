using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Orleans;

namespace Mohist.Server.Agent.Services;

/// <summary>
/// <see cref="IAgentLauncher"/> implementation that performs the canonical
/// mint-session → open-generic-session → build-AgentJobInput → submit-to-grain
/// pipeline. Extracted verbatim from the manual HTTP launch route
/// (<c>Api/AgentSessionLaunchRoutes.cs</c>, issue-129 T-003) so the manual
/// HTTP path and the subscription dispatch handler (issue-391 T-003) compose
/// sessions through a single, observable, testable entry point.
///
/// <para>
/// Lifetime is <see cref="IScopedService"/> because the launcher resolves
/// scoped collaborators (<see cref="AgentSessionResolver"/>) from its
/// captured scope; the dispatch handler resolves this service via
/// <c>IServiceScopeFactory</c> from its own single-threaded bus handler
/// scope (same pattern as <c>InboxProjectionHandler</c>).
/// </para>
///
/// <para>
/// <c>OpenAsync</c> and <c>SubmitAsync</c> are awaited sequentially because
/// both the manual launch path and the subscription path require the
/// session, including trigger correlation labels, to be durable before the
/// AgentJobGrain dispatches. The dispatch submission itself is
/// durable at the grain side — <see cref="IAgentJobGrain.SubmitAsync"/>
/// persists the job input before performing one dispatch attempt without
/// waiting for Agent execution — so a replay resumes the same job record.
/// </para>
/// </summary>
public sealed class AgentLauncher : IAgentLauncher, IScopedService
{
    private readonly AgentSessionResolver _sessions;
    private readonly IGrainFactory _grains;

    public AgentLauncher(
        AgentSessionResolver sessions,
        IGrainFactory grains)
    {
        _sessions = sessions;
        _grains = grains;
    }

    public async Task<AgentLaunchResult> LaunchAsync(
        AgentInfo agent,
        string prompt,
        AgentLaunchContext context,
        IReadOnlyDictionary<string, string>? triggerLabels = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(context);

        var trimmedPrompt = prompt.Trim();
        if (string.IsNullOrWhiteSpace(trimmedPrompt))
        {
            throw new ArgumentException(
                "Prompt must not be empty or whitespace.",
                nameof(prompt));
        }

        var triggerIdentity = BuildTriggerIdentity(context.ProjectId, triggerLabels);
        var sessionId = triggerIdentity is null
            ? _sessions.NewSessionId()
            : StableId("agent-session", triggerIdentity);
        var sessionContext = BuildContext(context, agent);
        var metadata = GenericAgentSessionMetadata.Metadata(sessionContext);
        var durableMetadata = triggerIdentity is null
            ? metadata
            : WithTriggerLabels(metadata, triggerLabels!);

        var sessionGrain = _sessions.GetGrain(sessionId);
        await sessionGrain.OpenAsync(
            new OpenAgentSessionCommand(
                RunnerId: string.Empty,
                AgentRuntime: "opencode",
                WorkDir: context.WorkspacePath,
                Metadata: durableMetadata));

        var jobKey = triggerIdentity is null
            ? $"agent-job-launch-{Guid.NewGuid():N}"
            : StableId("agent-job-trigger", triggerIdentity);
        var jobGrain = _grains.GetGrain<IAgentJobGrain>(jobKey);
        var (resolvedModel, resolvedVariant) = ResolveModelAndVariant(agent.AgentConfig);
        var jobInput = new AgentJobInput(
            Prompt: trimmedPrompt,
            Model: resolvedModel,
            WorkspacePath: context.WorkspacePath,
            ProjectId: context.ProjectId,
            AgentId: agent.Id,
            AgentInstructions: string.IsNullOrWhiteSpace(agent.Instructions) ? null : agent.Instructions,
            AgentConfig: agent.AgentConfig?.Clone(),
            AgentSessionId: sessionId,
            Variant: resolvedVariant);
        if (triggerIdentity is null)
            await jobGrain.SubmitAsync(jobInput);
        else
            await jobGrain.EnsureSubmittedAsync(jobInput);

        return new AgentLaunchResult(
            SessionId: sessionId,
            AgentId: agent.Id,
            AgentName: agent.Name);
    }

    private static GenericAgentSessionContext BuildContext(AgentLaunchContext context, AgentInfo agent) =>
        new(
            ProjectId: context.ProjectId,
            AgentId: agent.Id,
            AgentName: agent.Name,
            IssueNumber: context.IssueNumber,
            EpicNumber: context.EpicNumber,
            Repository: context.Repository,
            WorkspacePath: context.WorkspacePath,
            Title: context.Title);

    private static string? BuildTriggerIdentity(
        string projectId,
        IReadOnlyDictionary<string, string>? triggerLabels)
    {
        if (triggerLabels is null || triggerLabels.Count == 0)
            return null;

        if (!triggerLabels.TryGetValue(GenericAgentSessionMetadata.TriggerEventId, out var eventId)
            || string.IsNullOrWhiteSpace(eventId)
            || !triggerLabels.TryGetValue(GenericAgentSessionMetadata.TriggerRuleId, out var ruleId)
            || string.IsNullOrWhiteSpace(ruleId))
        {
            throw new ArgumentException(
                "Trigger labels must include non-empty event and rule ids.",
                nameof(triggerLabels));
        }

        return $"{projectId}\n{eventId}\n{ruleId}";
    }

    private static string StableId(string prefix, string identity)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return $"{prefix}-{Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant()}";
    }

    /// <summary>
    /// Returns a new <see cref="AgentSessionMetadata"/> carrying
    /// <paramref name="triggerLabels"/> merged into the existing label set
    /// via the record-level <see cref="AgentSessionMetadata.Merge"/> API.
    /// Manual launches pass <c>null</c> and skip this path entirely — no
    /// trigger labels are recorded.
    /// </summary>
    private static AgentSessionMetadata WithTriggerLabels(
        AgentSessionMetadata metadata,
        IReadOnlyDictionary<string, string> triggerLabels) =>
        metadata.Merge(new AgentSessionMetadata(
            Labels: triggerLabels,
            Annotations: null));

    /// <summary>
    /// Reads the <c>model</c> and <c>variant</c> fields out of the Agent
    /// <c>AgentConfig</c> JSON element so they can be captured into the
    /// launch-time snapshot on <see cref="AgentJobInput"/>. Editing the
    /// Agent definition while a job is in flight therefore cannot change
    /// the resolved model/variant — they were copied at launch time
    /// (design D2, #410 T-001 AC).
    /// </summary>
    internal static (string? Model, string? Variant) ResolveModelAndVariant(JsonElement? agentConfig)
    {
        if (agentConfig is not { ValueKind: JsonValueKind.Object } config)
            return (null, null);

        var model = TryReadString(config, "model");
        var variant = string.IsNullOrWhiteSpace(model) ? null : TryReadString(config, "variant");
        return (model, variant);
    }

    private static string? TryReadString(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var value))
            return null;
        if (value.ValueKind != JsonValueKind.String)
            return null;
        var raw = value.GetString();
        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }
}
