using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Subscriptions;
using Mohist.Server.Infrastructure;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Grain;

[Collection("AgentJobGrain")]
public sealed class AgentJobSubagentTerminalCallbackSpecs : AgentJobGrainTestSupport
{
    private const string ChildTerminalEventType = "com.mohist.agent.job.subagent-terminal";

    public AgentJobSubagentTerminalCallbackSpecs(AgentJobGrainFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task SpawnOriginChildJobFailure_PersistsOneTerminalEventWithObservationReferenceOnly()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"project-terminal-callback-{suffix}";
        var childSessionId = $"child-session-terminal-callback-{suffix}";
        var childLaunchJobId = $"child-launch-job-terminal-callback-{suffix}";
        var parentSessionId = $"parent-session-terminal-callback-{suffix}";
        var edgeId = $"edge-terminal-callback-{suffix}";
        var initialInputId = $"child-input-terminal-callback-{suffix}";
        var initialTurnId = $"child-turn-terminal-callback-{suffix}";

        var child = await OpenSessionAsync(projectId, childSessionId, "child-agent");
        var attached = await child.ApplyParentLinkAttachAsync(new ApplyParentLinkAttachCommand(
            $"attach-terminal-callback-{suffix}",
            edgeId,
            parentSessionId,
            "parent-agent",
            childLaunchJobId,
            1,
            "/workspace",
            "runner-1",
            "opencode",
            "runtime-session",
            projectId,
            1,
            "standalone-receipt",
            SessionTreeExpectedLinkState.Absent));
        Assert.Equal(SessionTreeAttachMutationState.Attached, attached.State);

        await child.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            initialInputId,
            initialTurnId,
            "child work",
            "agent-launch",
            childLaunchJobId,
            Runtime: "opencode",
            WorkDir: "/workspace",
            AgentSessionStartup: new AgentSessionStartup(
                projectId,
                childSessionId,
                parentSessionId,
                [],
                "spawn-agent",
                "/workspace",
                "runner-1",
                "child-agent",
                "Child Agent")));

        var job = JobGrain(childLaunchJobId);
        await job.SubmitAsync(new AgentJobInput(
            Prompt: "child work",
            WorkspacePath: "/workspace",
            ProjectId: projectId,
            AgentId: "child-agent",
            AgentSessionId: childSessionId,
            InitialInputId: initialInputId,
            InitialTurnId: initialTurnId,
            AgentSessionStartup: new AgentSessionStartup(
                projectId,
                childSessionId,
                parentSessionId,
                [],
                "spawn-agent",
                "/workspace",
                "runner-1",
                "child-agent",
                "Child Agent"),
            SpawnOrigin: new AgentJobSpawnOrigin(
                parentSessionId,
                "parent-agent",
                edgeId,
                childSessionId,
                childLaunchJobId,
                initialTurnId)));

        await job.FailAsync("child failed", "child-agent");
        await job.FailAsync("child failed", "child-agent");

        var terminal = Assert.Single(_fixture.EventStore.Appended, evt =>
            evt.Envelope.Type == ChildTerminalEventType
            && evt.Envelope.Source.ToString() == $"/mohist/agent-job/{childLaunchJobId}");
        var data = terminal.Envelope.Data!.Value;
        Assert.Equal(childLaunchJobId, data.GetProperty("childLaunchJobId").GetString());
        Assert.Equal(childSessionId, data.GetProperty("childSessionId").GetString());
        Assert.Equal(parentSessionId, data.GetProperty("parentSessionId").GetString());
        Assert.Equal(edgeId, data.GetProperty("edgeId").GetString());
        Assert.Equal(initialTurnId, data.GetProperty("initialTurnId").GetString());
        Assert.Equal("failed", data.GetProperty("status").GetString());
        Assert.Equal($"agent-job:{childLaunchJobId}", data.GetProperty("resultReference").GetString());
        Assert.False(data.TryGetProperty("output", out _));
        Assert.False(data.TryGetProperty("transcript", out _));
    }

    [Fact]
    public async Task UnknownJobAndTurnTerminal_DoNotPersistSubagentTerminalEvent()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"project-terminal-nontrigger-{suffix}";
        var childSessionId = $"child-session-terminal-nontrigger-{suffix}";
        var childLaunchJobId = $"child-launch-job-terminal-nontrigger-{suffix}";
        var initialInputId = $"child-input-terminal-nontrigger-{suffix}";
        var initialTurnId = $"child-turn-terminal-nontrigger-{suffix}";

        var child = await OpenSessionAsync(projectId, childSessionId, "child-agent");
        await child.ApplyParentLinkAttachAsync(new ApplyParentLinkAttachCommand(
            $"attach-terminal-nontrigger-{suffix}",
            $"edge-terminal-nontrigger-{suffix}",
            $"parent-session-terminal-nontrigger-{suffix}",
            "parent-agent",
            childLaunchJobId,
            1,
            "/workspace",
            "runner-1",
            "opencode",
            "runtime-session",
            projectId,
            1,
            "standalone-receipt",
            SessionTreeExpectedLinkState.Absent));
        await child.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            initialInputId,
            initialTurnId,
            "child work",
            "agent-launch",
            childLaunchJobId));

        var job = JobGrain(childLaunchJobId);
        await job.SubmitAsync(new AgentJobInput(
            Prompt: "child work",
            ProjectId: projectId,
            AgentId: "child-agent",
            AgentSessionId: childSessionId,
            InitialInputId: initialInputId,
            InitialTurnId: initialTurnId,
            SpawnOrigin: new AgentJobSpawnOrigin(
                $"parent-session-terminal-nontrigger-{suffix}",
                "parent-agent",
                $"edge-terminal-nontrigger-{suffix}",
                childSessionId,
                childLaunchJobId,
                initialTurnId)));
        await child.MarkInitialTurnTerminalAsync(childLaunchJobId, AgentTurnStatus.Completed, null);
        await job.MarkUnknownAsync("runner state was inconclusive");

        Assert.DoesNotContain(_fixture.EventStore.Appended, evt =>
            evt.Envelope.Type == ChildTerminalEventType
            && evt.Envelope.Source.ToString() == $"/mohist/agent-job/{childLaunchJobId}");
    }

    [Fact]
    public async Task SubagentTerminalInput_UsesExactKeyAndReusesTheFirstInputAndTurnOnRetry()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var parent = await OpenSessionAsync(
            $"project-terminal-input-{suffix}",
            $"parent-session-terminal-input-{suffix}",
            "parent-agent");
        var edgeId = $"edge-terminal-input-{suffix}";
        var childLaunchJobId = $"child-launch-job-terminal-input-{suffix}";
        var key = SubagentTerminalReportIdempotencyKeys.For(edgeId, childLaunchJobId);
        var provenance = new AgentSessionInputProvenance(
            ProviderKind: "subagent-terminal",
            WorkspaceId: $"child-session-terminal-input-{suffix}",
            ConversationId: childLaunchJobId,
            ThreadId: $"child-turn-terminal-input-{suffix}",
            MemberId: edgeId,
            MessageId: $"agent-job:{childLaunchJobId}");

        var first = await parent.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "child failed; result=agent-job:" + childLaunchJobId,
            Source: "subagent-terminal",
            IdempotencyKey: key,
            PreMintedInputId: $"parent-input-terminal-input-{suffix}",
            PreMintedTurnId: $"parent-turn-terminal-input-{suffix}",
            Provenance: provenance));
        var retry = await parent.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "child failed; result=agent-job:" + childLaunchJobId,
            Source: "subagent-terminal",
            IdempotencyKey: key,
            PreMintedInputId: $"parent-input-terminal-input-retry-{suffix}",
            PreMintedTurnId: $"parent-turn-terminal-input-retry-{suffix}",
            Provenance: provenance));

        Assert.Equal($"subagent-terminal:{edgeId}:{childLaunchJobId}", key);
        Assert.False(first.AlreadyAccepted);
        Assert.True(retry.AlreadyAccepted);
        Assert.Equal(first.InputId, retry.InputId);
        Assert.Equal(first.TurnId, retry.TurnId);
        var turns = await parent.ListTurnsAsync();
        var turn = Assert.Single(turns);
        Assert.Equal(first.TurnId, turn.Id);
        Assert.Single(turn.InputIds);
        Assert.Equal(first.InputId, turn.InputIds[0]);
    }

    [Fact]
    public async Task AbortedBeforeAcceptance_DoesNotStageSubagentTerminalEvent()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"project-terminal-noaccept-{suffix}";
        var childSessionId = $"child-session-noaccept-{suffix}";
        var childLaunchJobId = $"child-launch-job-noaccept-{suffix}";
        var parentSessionId = $"parent-session-noaccept-{suffix}";
        var edgeId = $"edge-noaccept-{suffix}";
        var initialInputId = $"child-input-noaccept-{suffix}";
        var initialTurnId = $"child-turn-noaccept-{suffix}";

        var child = await OpenSessionAsync(projectId, childSessionId, "child-agent");
        await child.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            initialInputId,
            initialTurnId,
            "child work",
            "agent-launch",
            childLaunchJobId));

        var job = JobGrain(childLaunchJobId);
        await job.PrepareManualLaunchAsync(new PrepareManualLaunchCommand(
            SessionId: childSessionId,
            InputId: initialInputId,
            TurnId: initialTurnId,
            Prompt: "child work",
            ProjectId: projectId,
            AgentId: "child-agent",
            AgentSessionStartup: new AgentSessionStartup(
                projectId,
                childSessionId,
                parentSessionId,
                [],
                "spawn-agent",
                "/workspace",
                "runner-1",
                "child-agent",
                "Child Agent"),
            SpawnOrigin: new AgentJobSpawnOrigin(
                parentSessionId,
                "parent-agent",
                edgeId,
                childSessionId,
                childLaunchJobId,
                initialTurnId)));

        await job.AbortPreparedLaunchAsync("parent_link_rejected");

        Assert.Equal(AgentJobStatus.Cancelled, await job.GetStatusAsync());
        Assert.DoesNotContain(_fixture.EventStore.Appended, evt =>
            evt.Envelope.Type == ChildTerminalEventType
            && evt.Envelope.Source.ToString() == $"/mohist/agent-job/{childLaunchJobId}");
    }

    [Fact]
    public async Task RejectedClaimEvent_HandlerCompletesWithoutAppendingParentInput()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"project-terminal-rejectedclaim-{suffix}";
        var childSessionId = $"child-session-rejectedclaim-{suffix}";
        var parentSessionId = $"parent-session-rejectedclaim-{suffix}";
        var edgeId = $"edge-rejectedclaim-{suffix}";
        var childLaunchJobId = $"child-launch-job-rejectedclaim-{suffix}";
        var initialTurnId = $"child-turn-rejectedclaim-{suffix}";

        await OpenSessionAsync(projectId, childSessionId, "child-agent");
        var parent = await OpenSessionAsync(projectId, parentSessionId, "parent-agent");

        var envelope = AgentJobLineage.BuildSubagentTerminalEnvelope(
            childLaunchJobId,
            new PendingSubagentTerminalEvent(
                EventId: $"evt-rejectedclaim-{suffix}",
                Origin: new AgentJobSpawnOrigin(
                    parentSessionId,
                    "parent-agent",
                    edgeId,
                    childSessionId,
                    childLaunchJobId,
                    initialTurnId),
                Status: AgentJobStatus.Cancelled,
                ResultReference: $"agent-job:{childLaunchJobId}",
                RecordedAt: _fixture.TimeProvider.GetUtcNow()));

        var handler = BuildTerminalHandler();

        await handler.HandleAsync(envelope, CancellationToken.None);

        Assert.Empty(await parent.ListTurnsAsync());
    }

    [Fact]
    public async Task AcceptedChildTerminal_HandlerDeliversExactlyOneParentInput()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"project-terminal-accepted-{suffix}";
        var childSessionId = $"child-session-accepted-{suffix}";
        var childLaunchJobId = $"child-launch-job-accepted-{suffix}";
        var parentSessionId = $"parent-session-accepted-{suffix}";
        var edgeId = $"edge-accepted-{suffix}";
        var initialInputId = $"child-input-accepted-{suffix}";
        var initialTurnId = $"child-turn-accepted-{suffix}";

        var child = await OpenSessionAsync(projectId, childSessionId, "child-agent");
        await child.ApplyParentLinkAttachAsync(new ApplyParentLinkAttachCommand(
            $"attach-accepted-{suffix}",
            edgeId,
            parentSessionId,
            "parent-agent",
            childLaunchJobId,
            1,
            "/workspace",
            "runner-1",
            "opencode",
            "runtime-session",
            projectId,
            1,
            "standalone-receipt",
            SessionTreeExpectedLinkState.Absent));
        await child.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            initialInputId,
            initialTurnId,
            "child work",
            "agent-launch",
            childLaunchJobId,
            Runtime: "opencode",
            WorkDir: "/workspace",
            AgentSessionStartup: new AgentSessionStartup(
                projectId,
                childSessionId,
                parentSessionId,
                [],
                "spawn-agent",
                "/workspace",
                "runner-1",
                "child-agent",
                "Child Agent")));
        var parent = await OpenSessionAsync(projectId, parentSessionId, "parent-agent");

        var job = JobGrain(childLaunchJobId);
        await job.SubmitAsync(new AgentJobInput(
            Prompt: "child work",
            WorkspacePath: "/workspace",
            ProjectId: projectId,
            AgentId: "child-agent",
            AgentSessionId: childSessionId,
            InitialInputId: initialInputId,
            InitialTurnId: initialTurnId,
            AgentSessionStartup: new AgentSessionStartup(
                projectId,
                childSessionId,
                parentSessionId,
                [],
                "spawn-agent",
                "/workspace",
                "runner-1",
                "child-agent",
                "Child Agent"),
            SpawnOrigin: new AgentJobSpawnOrigin(
                parentSessionId,
                "parent-agent",
                edgeId,
                childSessionId,
                childLaunchJobId,
                initialTurnId)));
        await job.FailAsync("child failed", "child-agent");

        var envelope = Assert.Single(_fixture.EventStore.Appended, evt =>
            evt.Envelope.Type == ChildTerminalEventType
            && evt.Envelope.Source.ToString() == $"/mohist/agent-job/{childLaunchJobId}").Envelope;

        var handler = BuildTerminalHandler();

        await handler.HandleAsync(envelope, CancellationToken.None);
        var turnsAfterFirst = await parent.ListTurnsAsync();
        var turnAfterFirst = Assert.Single(turnsAfterFirst);
        var firstInputId = Assert.Single(turnAfterFirst.InputIds);

        await handler.HandleAsync(envelope, CancellationToken.None);
        var turnsAfterReplay = await parent.ListTurnsAsync();
        var turnAfterReplay = Assert.Single(turnsAfterReplay);
        Assert.Equal(turnAfterFirst.Id, turnAfterReplay.Id);
        Assert.Equal(firstInputId, Assert.Single(turnAfterReplay.InputIds));
    }

    private AgentJobSubagentTerminalHandler BuildTerminalHandler() =>
        new(_fixture.Grains, NullLogger<AgentJobSubagentTerminalHandler>.Instance);

    private async Task<IAgentSessionGrain> OpenSessionAsync(
        string projectId,
        string sessionId,
        string agentId)
    {
        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.OpenAsync(new OpenAgentSessionCommand(
            "runner-1",
            "opencode",
            "/workspace",
            Metadata: new AgentSessionMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                [GenericAgentSessionMetadata.AgentId] = agentId,
                [GenericAgentSessionMetadata.AgentName] = agentId,
            })));
        await session.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
            "runtime-session",
            ExpectedRunnerId: "runner-1",
            ExpectedRuntime: "opencode"));
        return session;
    }
}
