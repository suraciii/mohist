using System.Text.Json;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Subscriptions;
using Mohist.Server.Contracts;
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
/// pipeline. Shared by the manual HTTP launch route
/// (<c>Api/AgentSessionLaunchRoutes.cs</c>) and the subscription dispatch
/// handler so both compose sessions through a single, observable, testable
/// entry point.
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
    private readonly AgentReadinessService _readiness;
    private readonly AgentExecutionSnapshotResolver _snapshots;
    private readonly AgentSpawnAdmissionService _spawnAdmission;

    public AgentLauncher(
        AgentSessionResolver sessions,
        IGrainFactory grains,
        TimeProvider timeProvider,
        AgentReadinessService readiness,
        AgentExecutionSnapshotResolver snapshots,
        AgentSpawnAdmissionService spawnAdmission)
    {
        _sessions = sessions;
        _grains = grains;
        _timeProvider = timeProvider;
        _readiness = readiness;
        _snapshots = snapshots;
        _spawnAdmission = spawnAdmission;
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

        await EnsureLaunchableAsync(agent, ct);

        var (sessionId, jobKey) = ResolveSessionAndJobKeys(context.ProjectId, triggerLabels);
        var sessionContext = BuildContext(context, agent);
        if (triggerLabels is { Count: > 0 })
        {
            sessionContext = sessionContext with
            {
                Origin = context.Origin ?? "event-router",
                TargetId = context.TargetId ?? agent.Id,
            };
        }
        var metadata = GenericAgentSessionMetadata.Metadata(sessionContext);
        var durableMetadata = triggerLabels is null
            ? metadata
            : WithTriggerLabels(metadata, triggerLabels);

        var definition = await ResolveDefinitionAsync(agent);
        var startup = BuildStartup(
            context.ProjectId,
            sessionId,
            definition,
            workDir: context.WorkspacePath,
            agentId: agent.Id,
            agentName: agent.Name);

        var sessionGrain = _sessions.GetGrain(sessionId);
        await sessionGrain.OpenAsync(
            new OpenAgentSessionCommand(
                RunnerId: string.Empty,
                AgentRuntime: definition.Runtime,
                WorkDir: context.WorkspacePath,
                Metadata: durableMetadata,
                Definition: definition,
                AgentSessionStartup: startup));

        var jobGrain = _grains.GetGrain<IAgentJobGrain>(jobKey);
        var jobInput = new AgentJobInput(
            Prompt: trimmedPrompt,
            Model: definition.Model,
            WorkspaceName: context.WorkspaceName,
            WorkspacePath: context.WorkspacePath,
            ProjectId: context.ProjectId,
            Runtime: definition.Runtime,
            AgentId: agent.Id,
            AgentInstructions: string.IsNullOrWhiteSpace(definition.Instructions) ? null : definition.Instructions,
            AgentConfig: agent.AgentConfig?.Clone(),
            AgentSessionId: sessionId,
            Variant: definition.Variant,
            ReasoningEffort: definition.ReasoningEffort,
            Skills: definition.Skills,
            IssueNumber: context.IssueNumber,
            EpicNumber: context.EpicNumber,
            AllowedSubagents: definition.AllowedSubagents,
            AgentSessionStartup: startup);
        if (triggerLabels is null)
            await jobGrain.SubmitAsync(jobInput);
        else
            await jobGrain.EnsureSubmittedAsync(jobInput);

        return new AgentLaunchResult(
            SessionId: sessionId,
            JobKey: jobKey,
            InputId: string.Empty,
            TurnId: string.Empty,
            AgentId: agent.Id,
            AgentName: agent.Name);
    }

    public Task<AgentLaunchResult> LaunchIdempotentAsync(
        AgentInfo agent,
        string prompt,
        AgentLaunchContext context,
        string idempotencyKey,
        AgentLaunchCoordinatorRequest request,
        IReadOnlyList<AgentSessionInputAttachmentDescriptor>? attachments = null,
        string? preMintedSessionId = null,
        string? preMintedInputId = null,
        string? preMintedTurnId = null,
        CancellationToken ct = default) =>
        LaunchIdempotentCoreAsync(
            agent,
            prompt,
            context,
            idempotencyKey,
            request,
            attachments,
            preMintedSessionId,
            preMintedInputId,
            preMintedTurnId,
            ct: ct,
            definitionOverride: null,
            skipLaunchability: false);

    private async Task<AgentLaunchResult> LaunchIdempotentCoreAsync(
        AgentInfo agent,
        string prompt,
        AgentLaunchContext context,
        string idempotencyKey,
        AgentLaunchCoordinatorRequest request,
        IReadOnlyList<AgentSessionInputAttachmentDescriptor>? attachments = null,
        string? preMintedSessionId = null,
        string? preMintedInputId = null,
        string? preMintedTurnId = null,
        CancellationToken ct = default,
        string? parentSessionId = null,
        string? parentAgentId = null,
        string? parentExpectedWorkDir = null,
        string? parentExpectedRunnerId = null,
        string? parentExpectedRuntime = null,
        string? parentExpectedRuntimeSessionId = null,
        long? parentExpectedBindingEpoch = null,
        string? parentLinkEdgeId = null,
        string? pinnedRunnerId = null,
        AgentExecutionDefinition? definitionOverride = null,
        bool skipLaunchability = false)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new ArgumentException(
                "Idempotency-Key is required for the manual launch path.",
                nameof(idempotencyKey));
        }

        var spawnRequestFingerprint = parentSessionId is null
            ? null
            : AgentLaunchCoordinatorCodec.SpawnFingerprint(
                request.AgentRef ?? string.Empty,
                request.Prompt);
        if (parentSessionId is not null)
        {
            var spawnFence = await _spawnAdmission.StartOrValidateFenceAsync(
                context.ProjectId,
                parentSessionId,
                idempotencyKey,
                request.AgentRef ?? string.Empty,
                request.Prompt);
            if (spawnFence.Outcome == SpawnRequestFenceOutcome.PreplanRejected)
                throw new AgentSpawnPreplanRejectedException(
                    spawnFence.PreplanRejectionReason ?? "spawn_rejected");

            var existingSpawn = await _grains.GetGrain<IAgentLaunchCoordinatorGrain>(
                    AgentLaunchCoordinatorCodec.KeyFor(context.ProjectId, parentSessionId, idempotencyKey))
                .ResumeExistingSpawnAsync(spawnRequestFingerprint!);
            if (existingSpawn is not null)
            {
                return new AgentLaunchResult(
                    existingSpawn.SessionId,
                    existingSpawn.JobKey,
                    existingSpawn.InputId,
                    existingSpawn.TurnId,
                    existingSpawn.AgentId,
                    existingSpawn.AgentName,
                    existingSpawn.ParentLinkEdgeId);
            }
            if (spawnFence.Outcome == SpawnRequestFenceOutcome.Admitted)
                throw new LaunchSetupPendingException(idempotencyKey);
        }

        var hasAttachments = attachments is { Count: > 0 };
        var trimmedPrompt = prompt?.Trim() ?? string.Empty;
        var hasText = string.Equals(request.Origin, "direct-api", StringComparison.Ordinal)
            ? !string.IsNullOrEmpty(prompt)
            : !string.IsNullOrWhiteSpace(trimmedPrompt);
        if (!hasText && !hasAttachments)
        {
            throw new ArgumentException(
                "Prompt must not be empty or whitespace unless at least one attachment is accepted.",
                nameof(prompt));
        }

        if (!skipLaunchability)
            await EnsureLaunchableAsync(agent, ct);

        var definition = definitionOverride ?? await ResolveDefinitionAsync(agent);
        var resolvedRuntime = definition.Runtime;
        var resolvedModel = definition.Model;
        var resolvedVariant = definition.Variant;
        var resolvedReasoningEffort = definition.ReasoningEffort;
        var agentConfigJson = agent.AgentConfig is { ValueKind: not JsonValueKind.Undefined }
            ? agent.AgentConfig.Value.GetRawText()
            : null;

        var coordinatorKey = parentSessionId is null
            ? AgentLaunchCoordinatorCodec.KeyFor(context.ProjectId, idempotencyKey)
            : AgentLaunchCoordinatorCodec.KeyFor(context.ProjectId, parentSessionId, idempotencyKey);
        var coordinator = _grains.GetGrain<IAgentLaunchCoordinatorGrain>(coordinatorKey);
        var outcome = await coordinator.LaunchAsync(new AgentLaunchCoordinatorCommandEnvelope(
            ProjectId: context.ProjectId,
            IdempotencyKey: idempotencyKey,
            AgentId: agent.Id,
            AgentName: agent.Name,
            AgentInstructions: string.IsNullOrWhiteSpace(agent.Instructions) ? null : agent.Instructions,
            AgentConfigJson: agentConfigJson,
            Model: resolvedModel,
            Variant: resolvedVariant,
            Runtime: resolvedRuntime,
            ReasoningEffort: resolvedReasoningEffort,
            Prompt: request.ExactPromptFingerprint ? prompt! : trimmedPrompt,
            WorkspaceName: context.WorkspaceName,
            WorkspacePath: context.WorkspacePath,
            IssueNumber: context.IssueNumber,
            EpicNumber: context.EpicNumber,
            Repository: context.Repository,
            Title: context.Title,
            Origin: context.Origin,
            TargetId: context.TargetId,
            Request: request,
            PreMintedSessionId: preMintedSessionId,
            PreMintedInputId: preMintedInputId,
            PreMintedTurnId: preMintedTurnId,
            Attachments: attachments,
            AllowedSubagents: definition.AllowedSubagents,
            AgentSessionStartup: preMintedSessionId is null
                ? null
                : BuildStartup(
                    context.ProjectId,
                    preMintedSessionId,
                    definition,
                    parentSessionId,
                    request.AgentRef,
                    context.WorkspacePath,
                    pinnedRunnerId,
                    agent.Id,
                    agent.Name),
            PinnedRunnerId: pinnedRunnerId,
            ParentSessionId: parentSessionId,
            ParentAgentId: parentAgentId,
            ParentExpectedWorkDir: parentExpectedWorkDir,
            ParentExpectedRunnerId: parentExpectedRunnerId,
            ParentExpectedRuntime: parentExpectedRuntime,
            ParentExpectedRuntimeSessionId: parentExpectedRuntimeSessionId,
            ParentExpectedBindingEpoch: parentExpectedBindingEpoch,
            ParentLinkEdgeId: parentLinkEdgeId,
            SpawnRequestFingerprint: spawnRequestFingerprint,
            WorkspaceRepositories: request.WorkspaceRepositories));

        return new AgentLaunchResult(
            SessionId: outcome.SessionId,
            JobKey: outcome.JobKey,
            InputId: outcome.InputId,
            TurnId: outcome.TurnId,
            AgentId: outcome.AgentId,
            AgentName: outcome.AgentName,
            ParentLinkEdgeId: outcome.ParentLinkEdgeId);
    }

    public async Task<AgentLaunchResult> LaunchSubagentAsync(
        string projectId,
        string parentSessionId,
        string targetAgentRef,
        string prompt,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        var normalizedTargetAgentRef = targetAgentRef.Trim();
        var spawnFence = await _spawnAdmission.StartOrValidateFenceAsync(
            projectId,
            parentSessionId,
            idempotencyKey,
            normalizedTargetAgentRef,
            prompt);
        if (spawnFence.Outcome == SpawnRequestFenceOutcome.PreplanRejected)
            throw new AgentSpawnPreplanRejectedException(
                spawnFence.PreplanRejectionReason ?? "spawn_rejected");

        var existingSpawn = await _grains.GetGrain<IAgentLaunchCoordinatorGrain>(
                AgentLaunchCoordinatorCodec.KeyFor(projectId, parentSessionId, idempotencyKey))
            .ResumeExistingSpawnAsync(
                AgentLaunchCoordinatorCodec.SpawnFingerprint(normalizedTargetAgentRef, prompt));
        if (existingSpawn is not null)
        {
            return new AgentLaunchResult(
                existingSpawn.SessionId,
                existingSpawn.JobKey,
                existingSpawn.InputId,
                existingSpawn.TurnId,
                existingSpawn.AgentId,
                existingSpawn.AgentName,
                existingSpawn.ParentLinkEdgeId);
        }
        if (spawnFence.Outcome == SpawnRequestFenceOutcome.Admitted)
            throw new LaunchSetupPendingException(idempotencyKey);

        var admission = await _spawnAdmission.AdmitAsync(
            projectId,
            parentSessionId,
            idempotencyKey,
            normalizedTargetAgentRef,
            prompt,
            ct);
        var childSessionId = _sessions.NewSessionId();
        var edgeId = $"agent-edge-{AgentLaunchCoordinatorCodec.StableToken(
            $"{projectId}\n{parentSessionId}\n{idempotencyKey}")}";
        var request = new AgentLaunchCoordinatorRequest(
            prompt,
            normalizedTargetAgentRef,
            admission.Definition.Runtime,
            admission.WorkDir,
            null,
            null,
            null,
            null,
            ExactPromptFingerprint: true);
        return await LaunchIdempotentCoreAsync(
            admission.TargetAgent,
            prompt,
            new AgentLaunchContext(
                projectId,
                WorkspaceName: admission.ParentWorkspaceName,
                WorkspacePath: admission.WorkDir),
            idempotencyKey,
            request,
            ct: ct,
            parentSessionId: parentSessionId,
            parentAgentId: admission.ParentAgentId,
            parentExpectedWorkDir: admission.WorkDir,
            parentExpectedRunnerId: admission.RunnerId,
            parentExpectedRuntime: admission.Runtime,
            parentExpectedRuntimeSessionId: admission.RuntimeSessionId,
            parentExpectedBindingEpoch: admission.BindingEpoch,
            parentLinkEdgeId: edgeId,
            pinnedRunnerId: admission.RunnerId,
            preMintedSessionId: childSessionId,
            definitionOverride: admission.Definition,
            skipLaunchability: true);
    }

    public async Task<AgentLaunchResult> LaunchConnectionAsync(
        AgentInfo agent,
        string prompt,
        ConnectionLaunchOrigin origin,
        string? workspaceName = null,
        AgentStartupContext? startupContext = null,
        IReadOnlyList<AgentSessionInputAttachmentDescriptor>? attachments = null,
        IReadOnlyList<string>? attachmentIds = null,
        string? preMintedSessionId = null,
        string? preMintedInputId = null,
        string? preMintedTurnId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(origin);
        var trimmedPrompt = prompt?.Trim() ?? string.Empty;

        await EnsureLaunchableAsync(agent, ct);

        var context = new AgentLaunchContext(agent.ProjectId);
        var key = $"slack:{origin.WorkspaceTeamId}:{origin.ConversationId}:{origin.MessageTs}";
        var definition = ResolveExecutionDefinition(agent);
        var agentConfigJson = agent.AgentConfig is { ValueKind: not JsonValueKind.Undefined }
            ? agent.AgentConfig.Value.GetRawText()
            : null;
        var coordinator = _grains.GetGrain<IAgentLaunchCoordinatorGrain>(
            AgentLaunchCoordinatorCodec.KeyFor(context.ProjectId, key));
        var outcome = await coordinator.LaunchAsync(new AgentLaunchCoordinatorCommandEnvelope(
            ProjectId: context.ProjectId,
            IdempotencyKey: key,
            AgentId: agent.Id,
            AgentName: agent.Name,
            AgentInstructions: string.IsNullOrWhiteSpace(agent.Instructions) ? null : agent.Instructions,
            AgentConfigJson: agentConfigJson,
            Model: definition.Model,
            Variant: definition.Variant,
            Runtime: definition.Runtime,
            ReasoningEffort: definition.ReasoningEffort,
            Prompt: trimmedPrompt,
            WorkspaceName: workspaceName,
            WorkspacePath: null,
            IssueNumber: null,
            EpicNumber: null,
            Repository: null,
            Title: null,
            Request: new AgentLaunchCoordinatorRequest(
                trimmedPrompt,
                agent.Id,
                null,
                null,
                null,
                null,
                null,
                null,
                attachmentIds ?? attachments?.Select(attachment => attachment.Id).ToArray(),
                StartupContext: startupContext),
            ConnectionOrigin: origin,
            PreMintedSessionId: preMintedSessionId,
            PreMintedInputId: preMintedInputId,
            PreMintedTurnId: preMintedTurnId,
            Attachments: attachments,
            StartupContext: startupContext));

        return new AgentLaunchResult(outcome.SessionId, outcome.JobKey, outcome.InputId, outcome.TurnId, outcome.AgentId, outcome.AgentName);
    }

    public async Task<AgentLaunchResult?> ResumeIdempotentAsync(
        string projectId,
        string idempotencyKey,
        AgentLaunchCoordinatorRequest request,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentNullException.ThrowIfNull(request);

        var coordinator = _grains.GetGrain<IAgentLaunchCoordinatorGrain>(
            AgentLaunchCoordinatorCodec.KeyFor(projectId, idempotencyKey));
        var outcome = await coordinator.ResumeAsync(request);
        return outcome is null
            ? null
            : new AgentLaunchResult(
                SessionId: outcome.SessionId,
                JobKey: outcome.JobKey,
                InputId: outcome.InputId,
                TurnId: outcome.TurnId,
                AgentId: outcome.AgentId,
                AgentName: outcome.AgentName);
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
    /// Routed-launch path. The
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

        await EnsureLaunchableAsync(agent, ct);

        var sessionId = _sessions.StableSessionId(executionContext.ProjectId, triggeringEvent.Id, ruleId);
        var jobKey = _sessions.StableJobKey(executionContext.ProjectId, triggeringEvent.Id, ruleId);

        var definition = await ResolveDefinitionAsync(agent);
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
            AgentInstructions: string.IsNullOrWhiteSpace(definition.Instructions) ? null : definition.Instructions,
            AgentConfigJson: agentConfigJson,
            Model: definition.Model,
            Variant: definition.Variant,
            Prompt: trimmedPrompt,
            Runtime: definition.Runtime,
            Skills: definition.Skills,
            WorkflowRunId: executionContext.WorkflowRunId,
            ReasoningEffort: definition.ReasoningEffort);

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
    /// Mention-launch path. Reuses the
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

        await EnsureLaunchableAsync(agent, ct);

        var sessionId = _sessions.CommentSessionId(context.ProjectId, commentId, agent.Id);
        var jobKey = _sessions.CommentJobKey(context.ProjectId, commentId, agent.Id);

        var sessionContext = BuildContext(context, agent) with
        {
            Origin = context.Origin ?? "event-router",
            TargetId = context.TargetId ?? agent.Id,
        };
        var metadata = GenericAgentSessionMetadata.Metadata(sessionContext);
        var triggerLabels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GenericAgentSessionMetadata.TriggerEventId] = triggeringEventId,
            [GenericAgentSessionMetadata.TriggerCommentId] = commentId,
        };
        var durableMetadata = WithTriggerLabels(metadata, triggerLabels);

        var definition = await ResolveDefinitionAsync(agent);
        var startup = BuildStartup(
            context.ProjectId,
            sessionId,
            definition,
            workDir: context.WorkspacePath,
            agentId: agent.Id,
            agentName: agent.Name);

        var sessionGrain = _sessions.GetGrain(sessionId);
        await sessionGrain.OpenAsync(
            new OpenAgentSessionCommand(
                RunnerId: string.Empty,
                AgentRuntime: definition.Runtime,
                WorkDir: context.WorkspacePath,
                Metadata: durableMetadata,
                Definition: definition,
                AgentSessionStartup: startup));

        var jobGrain = _grains.GetGrain<IAgentJobGrain>(jobKey);
        var jobInput = new AgentJobInput(
            Prompt: trimmedPrompt,
            Model: definition.Model,
            WorkspaceName: context.WorkspaceName,
            WorkspacePath: context.WorkspacePath,
            ProjectId: context.ProjectId,
            Runtime: definition.Runtime,
            AgentId: agent.Id,
            AgentInstructions: string.IsNullOrWhiteSpace(definition.Instructions) ? null : definition.Instructions,
            AgentConfig: agent.AgentConfig?.Clone(),
            AgentSessionId: sessionId,
            Variant: definition.Variant,
            ReasoningEffort: definition.ReasoningEffort,
            Skills: definition.Skills,
            IssueNumber: context.IssueNumber,
            EpicNumber: context.EpicNumber,
            AllowedSubagents: definition.AllowedSubagents,
            AgentSessionStartup: startup);
        await jobGrain.EnsureSubmittedAsync(jobInput);

        return new AgentLaunchResult(
            SessionId: sessionId,
            JobKey: jobKey,
            InputId: string.Empty,
            TurnId: string.Empty,
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
            WorkspaceName: context.WorkspaceName,
            WorkspacePath: context.WorkspacePath,
            Title: context.Title);

    private async Task EnsureLaunchableAsync(AgentInfo agent, CancellationToken ct)
    {
        await _readiness.EnsureLaunchableAsync(agent.ProjectId, agent, ct);
    }

    private async Task<AgentExecutionDefinition> ResolveDefinitionAsync(AgentInfo agent)
    {
        var resolved = await _snapshots.ResolveAsync(agent.ProjectId, agent.Id);
        return resolved ?? ResolveExecutionDefinition(agent);
    }

    private static AgentSessionStartup BuildStartup(
        string projectId,
        string sessionId,
        AgentExecutionDefinition definition,
        string? parentSessionId = null,
        string? agentRef = null,
        string? workDir = null,
        string? pinnedRunnerId = null,
        string? agentId = null,
        string? agentName = null) =>
        new(
            ProjectId: projectId,
            SessionId: sessionId,
            ParentSessionId: parentSessionId,
            AllowedSubagents: definition.AllowedSubagents ?? [],
            SpawnCommand: $"mo agent spawn {agentRef ?? "<agent-ref>"} --project {projectId} --parent-session {parentSessionId ?? sessionId} --prompt \"<brief>\" --idempotency-key <key>",
            WorkDir: workDir,
            PinnedRunnerId: pinnedRunnerId,
            AgentId: agentId,
            AgentName: agentName);

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
    /// the resolved model/variant — they were copied at launch time.
    /// </summary>
    internal static (string? Model, string? Variant) ResolveModelAndVariant(JsonElement? agentConfig)
    {
        if (agentConfig is not { ValueKind: JsonValueKind.Object } config)
            return (null, null);

        var model = TryReadString(config, "model");
        var variant = string.IsNullOrWhiteSpace(model) ? null : TryReadString(config, "variant");
        return (model, variant);
    }

    internal static string? ResolveReasoningEffort(JsonElement? agentConfig)
    {
        if (agentConfig is not { ValueKind: JsonValueKind.Object } config)
            return null;

        var effort = TryReadString(config, "reasoningEffort");
        return ReasoningEfforts.Contains(effort) ? effort : null;
    }

    /// <summary>
    /// Resolves the execution backend as
    /// <c>agentConfig.runtime ?? "opencode"</c>. Out-of-set values fall
    /// back to the same default. Editing the Agent's runtime config
    /// after launch cannot change the snapshotted result because the
    /// helper runs only at the launcher level; callers (manual launch,
    /// mention launch, routed launch, routed preflight) all consume
    /// the resolver output and stamp it onto the durable
    /// <see cref="AgentJobInput"/> or <see cref="RoutedAgentLaunchPlan"/>.
    /// </summary>
    internal static string ResolveRuntime(JsonElement? agentConfig)
    {
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

    /// <summary>
    /// Build the immutable execution-definition value for a launch or
    /// read-side snapshot. Wraps <see cref="ResolveRuntime"/> and
    /// <see cref="ResolveModelAndVariant"/> with the Agent's stored
    /// Instructions and ordered Skills. Skills are returned as a defensive
    /// copy so callers cannot mutate the Agent's stored list.
    /// </summary>
    internal static AgentExecutionDefinition ResolveExecutionDefinition(AgentInfo agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        var (model, variant) = ResolveModelAndVariant(agent.AgentConfig);
        var runtime = ResolveRuntime(agent.AgentConfig);
        var skills = agent.Skills.ToArray();
        return new AgentExecutionDefinition(
            Instructions: agent.Instructions,
            Runtime: runtime,
            Model: model,
            Variant: variant,
            Skills: skills,
            AllowedSubagents: null,
            ReasoningEffort: ResolveReasoningEffort(agent.AgentConfig));
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
