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

[Collection("AgentJobGrain")]
public sealed class AgentSpawnCoordinatorSpecs : AgentJobGrainTestSupport
{
    public AgentSpawnCoordinatorSpecs(AgentJobGrainFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task SpawnRecoveryKeepsProvisionalArtifactsHidden_ThenCommitsAndReplaysStablePlan()
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
            _fixture.LaunchFaults.FailNext(LaunchParticipantGate.EnsureInitialLaunch);
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
            Assert.Equal(0, beforeCommit.GraphRevision);
            Assert.Equal(LinkReservationState.Reserved, Assert.Single(beforeCommit.Reservations!).State);

            _fixture.LaunchFaults.StopFailing(LaunchParticipantGate.EnsureInitialLaunch);
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
            Assert.Equal(LinkReservationState.Attached, Assert.Single(afterCommit.Reservations!).State);

            await using (var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope())
            {
                var sessions = scope.ServiceProvider.GetRequiredService<AgentSessionQuery>();
                var jobs = scope.ServiceProvider.GetRequiredService<AgentJobQuerier>();
                Assert.Single(await sessions.ListByIdsAsync([childSessionId]));
                Assert.NotNull(await jobs.GetByKeyAsync(jobKey));
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
            _fixture.LaunchFaults.StopFailing(LaunchParticipantGate.EnsureInitialLaunch);
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
