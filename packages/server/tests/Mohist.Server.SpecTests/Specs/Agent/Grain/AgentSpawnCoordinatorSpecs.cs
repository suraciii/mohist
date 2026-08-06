using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Grain;

[Collection("AgentSpawnCoordinator")]
public sealed class AgentSpawnCoordinatorSpecs : AgentJobGrainTestSupport
{
    public AgentSpawnCoordinatorSpecs(AgentJobGrainFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public void SharedFixtureLaunchObservationsStartFreshForEachSpec()
    {
        Assert.Empty(_fixture.LaunchFaults.ParticipantIds(LaunchParticipantGate.PrepareJob));
        Assert.Empty(_fixture.LaunchFaults.CommandIds(LaunchParticipantGate.PrepareJob));
    }

    [Theory]
    [InlineData(LaunchParticipantGate.EnsureInitialLaunch)]
    [InlineData(LaunchParticipantGate.ParentLinkCommitted)]
    public async Task SpawnRecoveryKeepsProvisionalArtifactsHidden_ThenCommitsAndReplaysStablePlan(
        LaunchParticipantGate failureGate)
    {
        var runnerId = $"spawn-runner-{Guid.NewGuid():N}";
        var projectId = $"spawn-project-{Guid.NewGuid():N}";
        await RegisterAgentJobRunnerAsync(runnerId, projectId);

        var parentSessionId = $"spawn-parent-{Guid.NewGuid():N}";
        var childSessionId = $"spawn-child-{Guid.NewGuid():N}";
        var inputId = $"spawn-input-{Guid.NewGuid():N}";
        var turnId = $"spawn-turn-{Guid.NewGuid():N}";
        const string targetAgentId = "agent-target";
        const string prompt = "preserve this spawn";
        var idempotencyKey = $"spawn-key-{Guid.NewGuid():N}";
        var edgeId = $"edge-{Guid.NewGuid():N}";

        var parent = Grains.GetGrain<IAgentSessionGrain>(parentSessionId);
        var parentDefinition = new AgentExecutionDefinition(
            "parent instructions",
            "opencode",
            "gpt-5.6-luna",
            "xhigh",
            [],
            [new AllowedSubagentSnapshot(targetAgentId, "Target", "target description")]);
        await parent.OpenAsync(new OpenAgentSessionCommand(
            runnerId,
            "opencode",
            "/workspace",
            Metadata: Metadata(projectId, "agent-parent", "agent-launch"),
            Definition: parentDefinition));
        await parent.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
            "parent-runtime",
            ExpectedRunnerId: runnerId,
            ExpectedRuntime: "opencode"));

        var request = new AgentLaunchCoordinatorRequest(
            Prompt: prompt,
            AgentRef: targetAgentId,
            Runtime: "pi",
            WorkspacePath: "/workspace",
            IssueNumber: null,
            EpicNumber: null,
            Repository: null,
            Title: null,
            AttachmentIds: null,
            StartupContext: null,
            ExactPromptFingerprint: true);
        var startup = new AgentSessionStartup(
            projectId,
            childSessionId,
            parentSessionId,
            [],
            "mo agent spawn");
        var command = new AgentLaunchCoordinatorCommandEnvelope(
            ProjectId: projectId,
            IdempotencyKey: idempotencyKey,
            AgentId: targetAgentId,
            AgentName: "Target",
            AgentInstructions: "child instructions",
            AgentConfigJson: null,
            Model: "gpt-5.6-luna",
            Variant: "xhigh",
            Runtime: "pi",
            Prompt: prompt,
            WorkspacePath: "/workspace",
            IssueNumber: null,
            EpicNumber: null,
            Repository: null,
            Title: null,
            Request: request,
            ConnectionOrigin: null,
            PreMintedInputId: inputId,
            PreMintedTurnId: turnId,
            PreMintedSessionId: childSessionId,
            AllowedSubagents: [],
            PinnedRunnerId: runnerId,
            AgentSessionStartup: startup,
            ParentSessionId: parentSessionId,
            ParentAgentId: "agent-parent",
            ParentExpectedWorkDir: "/workspace",
            ParentExpectedRunnerId: runnerId,
            ParentExpectedRuntime: "opencode",
            ParentExpectedRuntimeSessionId: "parent-runtime",
            ParentExpectedBindingEpoch: 1,
            ParentLinkEdgeId: edgeId,
            SpawnRequestFingerprint: AgentLaunchCoordinatorCodec.SpawnFingerprint(targetAgentId, prompt));
        var coordinator = Grains.GetGrain<IAgentLaunchCoordinatorGrain>(
            AgentLaunchCoordinatorCodec.KeyFor(projectId, parentSessionId, idempotencyKey));
        await Grains.GetGrain<ISpawnRequestFenceGrain>(
            AgentLaunchCoordinatorCodec.KeyFor(projectId, parentSessionId, idempotencyKey))
            .StartAsync(new SpawnRequestFence(
                projectId,
                parentSessionId,
                idempotencyKey,
                AgentLaunchCoordinatorCodec.SpawnFingerprint(targetAgentId, prompt),
                SpawnRequestFenceOutcome.ValidationPending));

        try
        {
            _fixture.LaunchFaults.ClearObservations();
            _fixture.LaunchFaults.FailNext(failureGate);
            await Assert.ThrowsAsync<LaunchSetupPendingException>(() => coordinator.LaunchAsync(command));

            var jobKey = Assert.Single(_fixture.LaunchFaults.ParticipantIds(LaunchParticipantGate.PrepareJob));
            await using (var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope())
            {
                var sessions = scope.ServiceProvider.GetRequiredService<AgentSessionQuery>();
                var jobs = scope.ServiceProvider.GetRequiredService<AgentJobQuerier>();
                var store = scope.ServiceProvider.GetRequiredService<IAgentJobStore>();
                Assert.Empty(await sessions.ListByIdsAsync([childSessionId]));
                Assert.Null(await jobs.GetByKeyAsync(jobKey));
                Assert.Empty(await store.ListEligiblePendingAsync(projectId, 10));
                Assert.Empty(await store.ListAssignedPendingForRunnerAsync(runnerId, 10));
            }

            var beforeCommit = await Grains
                .GetGrain<ISessionTreeMutationFenceGrain>(projectId)
                .GetAsync();
            if (failureGate == LaunchParticipantGate.ParentLinkCommitted)
            {
                Assert.Equal(1, beforeCommit.GraphRevision);
                var attached = Assert.Single(beforeCommit.Reservations!);
                Assert.Equal(LinkReservationState.Attached, attached.State);
                Assert.Equal(1, attached.AttachedRevision);

                var services = _fixture.Cluster.GetSiloServiceProvider(null);
                var tree = new AgentSessionTreeQuerier(
                    services.GetRequiredService<IDbContextFactory<MohistDbContext>>());
                var published = await tree.GetAsync(projectId, parentSessionId, 10, null);
                Assert.NotNull(published);
                Assert.Equal(1, published!.Revision);
                Assert.Equal(
                    new[] { parentSessionId, childSessionId },
                    published.Nodes.Select(node => node.SessionId).ToArray());
                Assert.Equal(edgeId, Assert.Single(published.Edges).EdgeId);
            }
            else
            {
                Assert.Equal(0, beforeCommit.GraphRevision);
                Assert.Equal(LinkReservationState.Reserved, Assert.Single(beforeCommit.Reservations!).State);
            }

            _fixture.LaunchFaults.StopFailing(failureGate);
            var recovered = await coordinator.LaunchAsync(command);
            Assert.Equal(childSessionId, recovered.SessionId);
            Assert.Equal(inputId, recovered.InputId);
            Assert.Equal(turnId, recovered.TurnId);
            Assert.Equal(edgeId, recovered.ParentLinkEdgeId);
            Assert.Equal(jobKey, recovered.JobKey);

            var afterCommit = await Grains
                .GetGrain<ISessionTreeMutationFenceGrain>(projectId)
                .GetAsync();
            Assert.Equal(1, afterCommit.GraphRevision);
            var afterAttached = Assert.Single(afterCommit.Reservations!);
            Assert.Equal(LinkReservationState.Attached, afterAttached.State);
            Assert.Equal(1, afterAttached.AttachedRevision);

            await using (var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope())
            {
                var sessions = scope.ServiceProvider.GetRequiredService<AgentSessionQuery>();
                var jobs = scope.ServiceProvider.GetRequiredService<AgentJobQuerier>();
                Assert.Single(await sessions.ListByIdsAsync([childSessionId]));
                Assert.NotNull(await jobs.GetByKeyAsync(jobKey));
            }

            if (failureGate == LaunchParticipantGate.ParentLinkCommitted)
            {
                var services = _fixture.Cluster.GetSiloServiceProvider(null);
                var tree = new AgentSessionTreeQuerier(
                    services.GetRequiredService<IDbContextFactory<MohistDbContext>>());
                var recoveredTree = await tree.GetAsync(projectId, parentSessionId, 10, null);
                Assert.NotNull(recoveredTree);
                Assert.Equal(1, recoveredTree!.Revision);
                Assert.Equal(
                    new[] { parentSessionId, childSessionId },
                    recoveredTree.Nodes.Select(node => node.SessionId).ToArray());
                Assert.Equal(edgeId, Assert.Single(recoveredTree.Edges).EdgeId);
            }

            var status = await Grains.GetGrain<IAgentJobGrain>(jobKey).GetStatusAsync();
            Assert.True(status is AgentJobStatus.Pending or AgentJobStatus.Running);

            var replay = await coordinator.LaunchAsync(command);
            Assert.Equal(recovered.JobKey, replay.JobKey);
            Assert.Equal(recovered.SessionId, replay.SessionId);
            Assert.Equal(recovered.InputId, replay.InputId);
            Assert.Equal(recovered.TurnId, replay.TurnId);
            Assert.Equal(recovered.ParentLinkEdgeId, replay.ParentLinkEdgeId);
            Assert.True(replay.AlreadyPersisted);
        }
        finally
        {
            _fixture.LaunchFaults.StopFailing(failureGate);
        }
    }

    [Theory]
    [InlineData(LaunchParticipantGate.PrepareJob)]
    [InlineData(LaunchParticipantGate.ReserveLink)]
    [InlineData(LaunchParticipantGate.SubmitJob)]
    public async Task SpawnRecoveryAfterGateFailure_ConvergesToOneAcceptedSpawn_NoDuplicateArtifacts(
        LaunchParticipantGate failureGate)
    {
        var runnerId = $"spawn-runner-{Guid.NewGuid():N}";
        var projectId = $"spawn-project-{Guid.NewGuid():N}";
        await RegisterAgentJobRunnerAsync(runnerId, projectId);

        var parentSessionId = $"spawn-parent-{Guid.NewGuid():N}";
        var childSessionId = $"spawn-child-{Guid.NewGuid():N}";
        var inputId = $"spawn-input-{Guid.NewGuid():N}";
        var turnId = $"spawn-turn-{Guid.NewGuid():N}";
        const string targetAgentId = "agent-target";
        const string prompt = "preserve this spawn";
        var idempotencyKey = $"spawn-key-{Guid.NewGuid():N}";
        var edgeId = $"edge-{Guid.NewGuid():N}";

        var parent = Grains.GetGrain<IAgentSessionGrain>(parentSessionId);
        var parentDefinition = new AgentExecutionDefinition(
            "parent instructions",
            "opencode",
            "gpt-5.6-luna",
            "xhigh",
            [],
            [new AllowedSubagentSnapshot(targetAgentId, "Target", "target description")]);
        await parent.OpenAsync(new OpenAgentSessionCommand(
            runnerId,
            "opencode",
            "/workspace",
            Metadata: Metadata(projectId, "agent-parent", "agent-launch"),
            Definition: parentDefinition));
        await parent.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
            "parent-runtime",
            ExpectedRunnerId: runnerId,
            ExpectedRuntime: "opencode"));

        var request = new AgentLaunchCoordinatorRequest(
            Prompt: prompt,
            AgentRef: targetAgentId,
            Runtime: "pi",
            WorkspacePath: "/workspace",
            IssueNumber: null,
            EpicNumber: null,
            Repository: null,
            Title: null,
            AttachmentIds: null,
            StartupContext: null,
            ExactPromptFingerprint: true);
        var startup = new AgentSessionStartup(
            projectId,
            childSessionId,
            parentSessionId,
            [],
            "mo agent spawn");
        var command = new AgentLaunchCoordinatorCommandEnvelope(
            ProjectId: projectId,
            IdempotencyKey: idempotencyKey,
            AgentId: targetAgentId,
            AgentName: "Target",
            AgentInstructions: "child instructions",
            AgentConfigJson: null,
            Model: "gpt-5.6-luna",
            Variant: "xhigh",
            Runtime: "pi",
            Prompt: prompt,
            WorkspacePath: "/workspace",
            IssueNumber: null,
            EpicNumber: null,
            Repository: null,
            Title: null,
            Request: request,
            ConnectionOrigin: null,
            PreMintedInputId: inputId,
            PreMintedTurnId: turnId,
            PreMintedSessionId: childSessionId,
            AllowedSubagents: [],
            PinnedRunnerId: runnerId,
            AgentSessionStartup: startup,
            ParentSessionId: parentSessionId,
            ParentAgentId: "agent-parent",
            ParentExpectedWorkDir: "/workspace",
            ParentExpectedRunnerId: runnerId,
            ParentExpectedRuntime: "opencode",
            ParentExpectedRuntimeSessionId: "parent-runtime",
            ParentExpectedBindingEpoch: 1,
            ParentLinkEdgeId: edgeId,
            SpawnRequestFingerprint: AgentLaunchCoordinatorCodec.SpawnFingerprint(targetAgentId, prompt));
        var coordinator = Grains.GetGrain<IAgentLaunchCoordinatorGrain>(
            AgentLaunchCoordinatorCodec.KeyFor(projectId, parentSessionId, idempotencyKey));
        await Grains.GetGrain<ISpawnRequestFenceGrain>(
            AgentLaunchCoordinatorCodec.KeyFor(projectId, parentSessionId, idempotencyKey))
            .StartAsync(new SpawnRequestFence(
                projectId,
                parentSessionId,
                idempotencyKey,
                AgentLaunchCoordinatorCodec.SpawnFingerprint(targetAgentId, prompt),
                SpawnRequestFenceOutcome.ValidationPending));

        try
        {
            _fixture.LaunchFaults.ClearObservations();
            _fixture.LaunchFaults.FailNext(failureGate);
            await Assert.ThrowsAsync<LaunchSetupPendingException>(() => coordinator.LaunchAsync(command));

            var jobKey = Assert.Single(_fixture.LaunchFaults.ParticipantIds(LaunchParticipantGate.PrepareJob));
            var expectedParticipantId = failureGate == LaunchParticipantGate.ReserveLink ? edgeId : jobKey;
            var firstCommandId = Assert.Single(_fixture.LaunchFaults.CommandIds(failureGate));
            Assert.Equal(expectedParticipantId, Assert.Single(_fixture.LaunchFaults.ParticipantIds(failureGate)));

            var fence = await Grains
                .GetGrain<ISessionTreeMutationFenceGrain>(projectId)
                .GetAsync();
            var spawnFence = await Grains
                .GetGrain<ISpawnRequestFenceGrain>(
                    AgentLaunchCoordinatorCodec.KeyFor(projectId, parentSessionId, idempotencyKey))
                .GetAsync();
            Assert.NotNull(spawnFence);

            await using (var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope())
            {
                var sessions = scope.ServiceProvider.GetRequiredService<AgentSessionQuery>();
                var jobs = scope.ServiceProvider.GetRequiredService<AgentJobQuerier>();
                var store = scope.ServiceProvider.GetRequiredService<IAgentJobStore>();
                if (failureGate == LaunchParticipantGate.SubmitJob)
                {
                    Assert.Single(await sessions.ListByIdsAsync([childSessionId]));
                    Assert.NotNull(await jobs.GetByKeyAsync(jobKey));
                }
                else
                {
                    Assert.Empty(await sessions.ListByIdsAsync([childSessionId]));
                    Assert.Null(await jobs.GetByKeyAsync(jobKey));
                    Assert.Empty(await store.ListEligiblePendingAsync(projectId, 10));
                    Assert.Empty(await store.ListAssignedPendingForRunnerAsync(runnerId, 10));
                }
            }

            if (failureGate == LaunchParticipantGate.SubmitJob)
            {
                Assert.Equal(1, fence.GraphRevision);
                var attached = Assert.Single(fence.Reservations!);
                Assert.Equal(LinkReservationState.Attached, attached.State);
                Assert.Equal(1, attached.AttachedRevision);
                Assert.Equal(SpawnRequestFenceOutcome.Admitted, spawnFence.Outcome);
                Assert.True(await Grains.GetGrain<IAgentJobGrain>(jobKey).GetStatusAsync()
                    is AgentJobStatus.Pending or AgentJobStatus.Running);
            }
            else if (failureGate == LaunchParticipantGate.ReserveLink)
            {
                Assert.Equal(0, fence.GraphRevision);
                Assert.Equal(LinkReservationState.Reserved, Assert.Single(fence.Reservations!).State);
                Assert.Equal(SpawnRequestFenceOutcome.Admitted, spawnFence.Outcome);
            }
            else
            {
                Assert.Equal(0, fence.GraphRevision);
                Assert.Null(fence.Reservations);
                Assert.Equal(SpawnRequestFenceOutcome.ValidationPending, spawnFence.Outcome);
            }

            _fixture.LaunchFaults.StopFailing(failureGate);
            var recovered = await coordinator.LaunchAsync(command);
            Assert.Equal(childSessionId, recovered.SessionId);
            Assert.Equal(inputId, recovered.InputId);
            Assert.Equal(turnId, recovered.TurnId);
            Assert.Equal(edgeId, recovered.ParentLinkEdgeId);
            Assert.Equal(jobKey, recovered.JobKey);
            Assert.False(recovered.AlreadyPersisted);

            // The failed attempt and the recovery replay acknowledged the
            // same gate command and participant identity, so no second plan,
            // job, session, edge or dispatch was minted.
            Assert.Equal([firstCommandId, firstCommandId], _fixture.LaunchFaults.CommandIds(failureGate));
            Assert.All(
                _fixture.LaunchFaults.ParticipantIds(failureGate),
                participantId => Assert.Equal(expectedParticipantId, participantId));

            var afterCommit = await Grains
                .GetGrain<ISessionTreeMutationFenceGrain>(projectId)
                .GetAsync();
            Assert.Equal(1, afterCommit.GraphRevision);
            var afterAttached = Assert.Single(afterCommit.Reservations!);
            Assert.Equal(LinkReservationState.Attached, afterAttached.State);
            Assert.Equal(1, afterAttached.AttachedRevision);
            Assert.Equal(
                SpawnRequestFenceOutcome.Admitted,
                (await Grains.GetGrain<ISpawnRequestFenceGrain>(
                    AgentLaunchCoordinatorCodec.KeyFor(projectId, parentSessionId, idempotencyKey))
                    .GetAsync())!.Outcome);

            await using (var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope())
            {
                var sessions = scope.ServiceProvider.GetRequiredService<AgentSessionQuery>();
                var jobs = scope.ServiceProvider.GetRequiredService<AgentJobQuerier>();
                Assert.Single(await sessions.ListByIdsAsync([childSessionId]));
                Assert.NotNull(await jobs.GetByKeyAsync(jobKey));
            }

            var services = _fixture.Cluster.GetSiloServiceProvider(null);
            var tree = new AgentSessionTreeQuerier(
                services.GetRequiredService<IDbContextFactory<MohistDbContext>>());
            var published = await tree.GetAsync(projectId, parentSessionId, 10, null);
            Assert.NotNull(published);
            Assert.Equal(1, published!.Revision);
            Assert.Equal(
                new[] { parentSessionId, childSessionId },
                published.Nodes.Select(node => node.SessionId).ToArray());
            Assert.Equal(edgeId, Assert.Single(published.Edges).EdgeId);

            var status = await Grains.GetGrain<IAgentJobGrain>(jobKey).GetStatusAsync();
            Assert.True(status is AgentJobStatus.Pending or AgentJobStatus.Running);

            var replay = await coordinator.LaunchAsync(command);
            Assert.Equal(recovered.JobKey, replay.JobKey);
            Assert.Equal(recovered.SessionId, replay.SessionId);
            Assert.Equal(recovered.InputId, replay.InputId);
            Assert.Equal(recovered.TurnId, replay.TurnId);
            Assert.Equal(recovered.ParentLinkEdgeId, replay.ParentLinkEdgeId);
            Assert.True(replay.AlreadyPersisted);
        }
        finally
        {
            _fixture.LaunchFaults.StopFailing(failureGate);
        }
    }

    private static AgentSessionMetadata Metadata(string projectId, string agentId, string source) =>
        new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
            [AgentSessionQueryMetadataKeys.SourceKind] = source,
            [GenericAgentSessionMetadata.AgentId] = agentId,
            [GenericAgentSessionMetadata.AgentName] = agentId,
        });
}
