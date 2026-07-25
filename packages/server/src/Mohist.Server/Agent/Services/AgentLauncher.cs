using System.Text.Json;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Events.Subscriptions;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Events;
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
    private readonly TimeProvider _timeProvider;

    public AgentLauncher(
        AgentSessionResolver sessions,
        IGrainFactory grains,
        TimeProvider timeProvider)
    {
        _sessions = sessions;
        _grains = grains;
        _timeProvider = timeProvider;
    }

    public async Task<AgentLaunchResult> LaunchAsync(
        AgentInfo agent,
        string prompt,
        AgentLaunchContext context,
        IReadOnlyDictionary<string, string>? triggerLabels = null,
        string? runtimeOverride = null,
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

        var (sessionId, jobKey) = ResolveSessionAndJobKeys(context.ProjectId, triggerLabels);
        var sessionContext = BuildContext(context, agent);
        var metadata = GenericAgentSessionMetadata.Metadata(sessionContext);
        var durableMetadata = triggerLabels is null
            ? metadata
            : WithTriggerLabels(metadata, triggerLabels);

        var resolvedRuntime = ResolveRuntime(agent.AgentConfig, runtimeOverride);

        var sessionGrain = _sessions.GetGrain(sessionId);
        await sessionGrain.OpenAsync(
            new OpenAgentSessionCommand(
                RunnerId: string.Empty,
                AgentRuntime: resolvedRuntime,
                WorkDir: context.WorkspacePath,
                Metadata: durableMetadata));

        var jobGrain = _grains.GetGrain<IAgentJobGrain>(jobKey);
        var (resolvedModel, resolvedVariant) = ResolveModelAndVariant(agent.AgentConfig);
        var jobInput = new AgentJobInput(
            Prompt: trimmedPrompt,
            Model: resolvedModel,
            WorkspacePath: context.WorkspacePath,
            ProjectId: context.ProjectId,
            Runtime: resolvedRuntime,
            AgentId: agent.Id,
            AgentInstructions: string.IsNullOrWhiteSpace(agent.Instructions) ? null : agent.Instructions,
            AgentConfig: agent.AgentConfig?.Clone(),
            AgentSessionId: sessionId,
            Variant: resolvedVariant);
        if (triggerLabels is null)
            await jobGrain.SubmitAsync(jobInput);
        else
            await jobGrain.EnsureSubmittedAsync(jobInput);

        return new AgentLaunchResult(
            SessionId: sessionId,
            AgentId: agent.Id,
            AgentName: agent.Name);
    }

    private (string SessionId, string JobKey) ResolveSessionAndJobKeys(
        string projectId,
        IReadOnlyDictionary<string, string>? triggerLabels)
    {
        if (triggerLabels is null || triggerLabels.Count == 0)
        {
            var sessionId = _sessions.NewSessionId();
            return (sessionId, $"agent-job-launch-{Guid.NewGuid():N}");
        }

        if (!triggerLabels.TryGetValue(GenericAgentSessionMetadata.TriggerEventId, out var eventId)
            || string.IsNullOrWhiteSpace(eventId)
            || !triggerLabels.TryGetValue(GenericAgentSessionMetadata.TriggerRuleId, out var ruleId)
            || string.IsNullOrWhiteSpace(ruleId))
        {
            throw new ArgumentException(
                "Trigger labels must include non-empty event and rule ids.",
                nameof(triggerLabels));
        }

        return (_sessions.StableSessionId(projectId, eventId, ruleId),
            _sessions.StableJobKey(projectId, eventId, ruleId));
    }

    /// <summary>
    /// Routed-launch path (issue-449 design decisions 1-3). The
    /// resolver's <see cref="RoutedExecutionContext"/> is the
    /// ownership-validated workspace + lineage for this event/rule hit;
    /// the launcher mints the stable session id + job key from the
    /// trigger identity and asks the grain to claim the canonical plan
    /// via <c>EnsurePreparedAsync</c>. The grain returns the canonical
    /// plan — never the caller's newly resolved values on replay —
    /// which the launcher advances through Session open, launch-ready,
    /// and dispatch in a single chain.
    /// </summary>
    public async Task<RoutedAgentLaunchOutcome> LaunchRoutedAsync(
        AgentInfo agent,
        string prompt,
        RoutedExecutionContext executionContext,
        CloudEvent triggeringEvent,
        string ruleId,
        string? runtimeOverride = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(triggeringEvent);
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleId);

        var trimmedPrompt = prompt.Trim();
        if (string.IsNullOrWhiteSpace(trimmedPrompt))
        {
            throw new ArgumentException(
                "Prompt must not be empty or whitespace.",
                nameof(prompt));
        }

        var triggerIdentity = $"{executionContext.ProjectId}\n{triggeringEvent.Id}\n{ruleId}";
        var sessionId = _sessions.StableSessionId(executionContext.ProjectId, triggeringEvent.Id, ruleId);
        var jobKey = _sessions.StableJobKey(executionContext.ProjectId, triggeringEvent.Id, ruleId);

        var (resolvedModel, resolvedVariant) = ResolveModelAndVariant(agent.AgentConfig);
        var resolvedRuntime = ResolveRuntime(agent.AgentConfig, runtimeOverride);
        var agentConfigJson = agent.AgentConfig is { ValueKind: not JsonValueKind.Undefined }
            ? agent.AgentConfig.Value.GetRawText()
            : null;

        var resolvedPlan = new RoutedAgentLaunchPlan(
            ProjectId: executionContext.ProjectId,
            EventId: triggeringEvent.Id,
            RuleId: ruleId,
            SessionId: sessionId,
            JobKey: jobKey,
            IssueNumber: executionContext.IssueNumber,
            EpicNumber: executionContext.EpicNumber,
            WorkspacePath: executionContext.WorkspacePath,
            Disposition: RoutedLaunchDisposition.Executable,
            PreflightReason: null,
            PreflightCategory: null,
            PreparedAt: _timeProvider.GetUtcNow(),
            AgentId: agent.Id,
            AgentName: agent.Name,
            AgentInstructions: string.IsNullOrWhiteSpace(agent.Instructions) ? null : agent.Instructions,
            AgentConfigJson: agentConfigJson,
            Model: resolvedModel,
            Variant: resolvedVariant,
            Prompt: trimmedPrompt,
            Runtime: resolvedRuntime);

        var jobGrain = _grains.GetGrain<IAgentJobGrain>(jobKey);
        var canonical = await jobGrain.EnsurePreparedAsync(resolvedPlan);
        await jobGrain.AdvancePreparedLaunchAsync();

        return new RoutedAgentLaunchOutcome(
            SessionId: canonical.SessionId,
            JobKey: canonical.JobKey,
            AgentId: canonical.AgentId ?? agent.Id,
            AgentName: canonical.AgentName ?? agent.Name,
            Disposition: canonical.Disposition,
            PreflightReason: canonical.PreflightReason,
            PreflightCategory: canonical.PreflightCategory);
    }

    /// <summary>
    /// Mention-launch path (issue-490 T-002, design D1/D3/D6). Reuses the
    /// manual-style <see cref="LaunchAsync"/> pipeline (workspace-optional,
    /// no preflight gate) but derives the session id + AgentJob key from
    /// the comment identity via
    /// <see cref="AgentSessionResolver.CommentSessionId"/> /
    /// <see cref="AgentSessionResolver.CommentJobKey"/>, so redelivery of
    /// the same <c>comment-added</c> event reuses one session grain and
    /// one AgentJob — distinct from routing/watch launches (which key on
    /// <c>(projectId, eventId, ruleId)</c>). The trigger labels include
    /// both the event id and the new <c>mohist.io/trigger/comment-id</c>
    /// constant so the launch is findable from the comment side and
    /// distinguishable as a mention launch.
    /// </summary>
    public async Task<AgentLaunchResult> LaunchMentionAsync(
        AgentInfo agent,
        string prompt,
        AgentLaunchContext context,
        string commentId,
        string triggeringEventId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(commentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(triggeringEventId);

        var trimmedPrompt = prompt?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedPrompt))
        {
            throw new ArgumentException(
                "Prompt must not be empty or whitespace.",
                nameof(prompt));
        }

        var sessionId = _sessions.CommentSessionId(context.ProjectId, commentId, agent.Id);
        var jobKey = _sessions.CommentJobKey(context.ProjectId, commentId, agent.Id);

        var sessionContext = BuildContext(context, agent);
        var metadata = GenericAgentSessionMetadata.Metadata(sessionContext);
        var triggerLabels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GenericAgentSessionMetadata.TriggerEventId] = triggeringEventId,
            [GenericAgentSessionMetadata.TriggerCommentId] = commentId,
        };
        var durableMetadata = WithTriggerLabels(metadata, triggerLabels);

        var resolvedRuntime = ResolveRuntime(agent.AgentConfig, launchOverride: null);

        var sessionGrain = _sessions.GetGrain(sessionId);
        await sessionGrain.OpenAsync(
            new OpenAgentSessionCommand(
                RunnerId: string.Empty,
                AgentRuntime: resolvedRuntime,
                WorkDir: context.WorkspacePath,
                Metadata: durableMetadata));

        var jobGrain = _grains.GetGrain<IAgentJobGrain>(jobKey);
        var (resolvedModel, resolvedVariant) = ResolveModelAndVariant(agent.AgentConfig);
        var jobInput = new AgentJobInput(
            Prompt: trimmedPrompt,
            Model: resolvedModel,
            WorkspacePath: context.WorkspacePath,
            ProjectId: context.ProjectId,
            Runtime: resolvedRuntime,
            AgentId: agent.Id,
            AgentInstructions: string.IsNullOrWhiteSpace(agent.Instructions) ? null : agent.Instructions,
            AgentConfig: agent.AgentConfig?.Clone(),
            AgentSessionId: sessionId,
            Variant: resolvedVariant);
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

    /// <summary>
    /// Resolves the execution backend as
    /// <c>launchOverride ?? agentConfig.runtime ?? "opencode"</c>
    /// (issue-452 design D2). The launch-time override wins over the
    /// Agent's configured backend; absent an override, the Agent's
    /// configured backend is used; absent both, the backend resolves to
    /// <see cref="AgentConfigSchema.OpenCodeRuntime"/>. Manual HTTP
    /// launch passes the caller-supplied override from
    /// <c>AgentSessionLaunchRequest.Runtime</c>; the routed
    /// subscription launch passes no override so the Agent's configured
    /// backend applies.
    /// </summary>
    internal static string ResolveRuntime(JsonElement? agentConfig, string? launchOverride)
    {
        if (!string.IsNullOrWhiteSpace(launchOverride)
            && AgentConfigSchema.AllowedRuntimes.Contains(launchOverride))
        {
            return launchOverride;
        }

        if (agentConfig is { ValueKind: JsonValueKind.Object } obj
            && obj.TryGetProperty("runtime", out var value)
            && value.ValueKind == JsonValueKind.String)
        {
            var raw = value.GetString();
            if (!string.IsNullOrWhiteSpace(raw)
                && AgentConfigSchema.AllowedRuntimes.Contains(raw))
            {
                return raw;
            }
        }

        return AgentConfigSchema.OpenCodeRuntime;
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
