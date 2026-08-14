using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Grains.Coordinator;
using Orleans;
using Orleans.Runtime;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Grains;

public sealed class WorkflowProfileReferenceCoordinatorTests
{
    [Fact]
    public void SameStartRequest_IgnoresRetryTimestamp_AndComparesMetadataStructurally()
    {
        var first = StartPayload(
            new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero),
            labels: new Dictionary<string, string> { ["kind"] = "issue" });
        var replay = StartPayload(
            new DateTimeOffset(2026, 8, 14, 0, 1, 0, TimeSpan.Zero),
            labels: new Dictionary<string, string> { ["kind"] = "issue" });
        var changed = StartPayload(
            new DateTimeOffset(2026, 8, 14, 0, 1, 0, TimeSpan.Zero),
            labels: new Dictionary<string, string> { ["kind"] = "epic" });

        Assert.True(WorkflowProfileReferenceCoordinatorGrain.SameStartRequest(first, replay));
        Assert.False(WorkflowProfileReferenceCoordinatorGrain.SameStartRequest(first, changed));
    }

    [Fact]
    public async Task BindWorkflowRun_RejectsPendingCommandIdReusedAcrossKinds()
    {
        const string commandId = "same-command";
        var pendingPayload = new WorkflowProfileCommandPayload.SetProjectDefault("project-1", "mohist/local");
        var state = new FakePersistentState(new WorkflowProfileCoordinatorState(
            new PendingWorkflowProfileCommand(
                commandId,
                pendingPayload.Kind,
                pendingPayload.ProfileId,
                ExpectedRevision: 7,
                WorkflowProfileCommandPayloadCodec.Serialize(pendingPayload))));
        var coordinator = new WorkflowProfileReferenceCoordinatorGrain(
            state,
            grains: null!,
            NullLogger<WorkflowProfileReferenceCoordinatorGrain>.Instance,
            provider: null!);

        var result = await coordinator.BindWorkflowRunAsync(
            new WorkflowProfileCommandPayload.BindWorkflowRun(
                "project-1",
                "run-1",
                IssueNumber: 1,
                EpicNumber: null,
                ExplicitProfileId: "mohist/github-pr",
                Metadata: new WorkflowRunMetadata(
                    "Issue 1",
                    new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero),
                    ProjectId: "project-1",
                    IssueNumber: 1)),
            commandId,
            expectedRevision: null);

        Assert.Equal(WorkflowProfileReferenceResultCode.ConflictingRequest, result.Code);
        Assert.Equal(0, state.WriteCount);
        Assert.Equal(pendingPayload.Kind, state.State.Pending?.Kind);
    }

    [Fact]
    public async Task AgentActionOverride_ReplayReturnsParticipantRejection()
    {
        const string commandId = "override-command";
        var payload = new WorkflowProfileCommandPayload.SetAgentActionOverride(
            "project-1", "custom/profile", "mohist/pi");
        var state = new FakePersistentState(new WorkflowProfileCoordinatorState(
            new PendingWorkflowProfileCommand(
                commandId,
                payload.Kind,
                payload.ProfileId,
                ExpectedRevision: 4,
                WorkflowProfileCommandPayloadCodec.Serialize(payload))));
        var coordinator = new WorkflowProfileReferenceCoordinatorGrain(
            state,
            new ParticipantGrainFactory(new RejectingProjectParticipant()),
            NullLogger<WorkflowProfileReferenceCoordinatorGrain>.Instance,
            provider: null!);

        var result = await coordinator.SetAgentActionOverrideAsync(payload, commandId, expectedRevision: null);

        Assert.Equal(WorkflowProfileReferenceResultCode.ProfileUnknown, result.Code);
        Assert.Equal(4, result.AppliedRevision);
        Assert.Null(state.State.Pending);
        Assert.Equal(1, state.WriteCount);
    }

    private static WorkflowProfileCommandPayload.BindWorkflowRun StartPayload(
        DateTimeOffset createdAt,
        Dictionary<string, string> labels) =>
        new(
            "project-1",
            "run-1",
            IssueNumber: 1,
            EpicNumber: null,
            ExplicitProfileId: "mohist/github-pr",
            Metadata: new WorkflowRunMetadata(
                "Issue 1",
                createdAt,
                Labels: labels,
                Annotations: new Dictionary<string, string> { ["source"] = "issue" },
                ProjectId: "project-1",
                IssueNumber: 1),
            Workspace: new WorkspaceIdentity("/tmp/run-1", "issue-1"));

    private sealed class FakePersistentState : IPersistentState<WorkflowProfileCoordinatorState>
    {
        public FakePersistentState(WorkflowProfileCoordinatorState state)
        {
            State = state;
        }

        public WorkflowProfileCoordinatorState State { get; set; }
        public string Etag { get; set; } = "1";
        public bool RecordExists { get; set; } = true;
        public string StateName => "workflow-profile-coordinator";
        public string StorageName => "test";
        public int WriteCount { get; private set; }

        public Task ClearStateAsync()
        {
            RecordExists = false;
            return Task.CompletedTask;
        }

        public Task ReadStateAsync() => Task.CompletedTask;

        public Task WriteStateAsync()
        {
            WriteCount++;
            RecordExists = true;
            return Task.CompletedTask;
        }
    }

    private sealed class RejectingProjectParticipant : IProjectWorkflowProfileBindingParticipant
    {
        public Task<ProjectWorkflowProfileBindingOutcome> SetDefaultAsync(
            WorkflowProfileCommandPayload.SetProjectDefault payload,
            string commandId,
            long? expectedRevision) => throw new NotSupportedException();

        public Task<ProjectWorkflowProfileBindingOutcome> SetAgentActionOverrideAsync(
            WorkflowProfileCommandPayload.SetAgentActionOverride payload,
            string commandId,
            long? expectedRevision) => Task.FromResult(ProjectWorkflowProfileBindingOutcome.ProfileUnknown);

        public Task<long> GetWorkflowProfileBindingRevisionAsync() => Task.FromResult(0L);
    }

    private sealed class ParticipantGrainFactory(IProjectWorkflowProfileBindingParticipant participant) : IGrainFactory
    {
        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(string grainPrimaryKey, string? grainClassNamePrefix)
            => participant is TGrainInterface typed ? typed : throw new NotSupportedException();
        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(Guid grainPrimaryKey, string? grainClassNamePrefix)
            => throw new NotSupportedException();
        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(long grainPrimaryKey, string? grainClassNamePrefix)
            => throw new NotSupportedException();
        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix)
            => throw new NotSupportedException();
        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix)
            => throw new NotSupportedException();
        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(GrainId grainId)
            => throw new NotSupportedException();
        IAddressable IGrainFactory.GetGrain(GrainId grainId) => throw new NotSupportedException();
        IAddressable IGrainFactory.GetGrain(GrainId grainId, GrainInterfaceType interfaceType) => throw new NotSupportedException();
        IAddressable IGrainFactory.GetGrain(Type grainInterfaceType, IdSpan grainKey, string? grainClassNamePrefix) => throw new NotSupportedException();
        IAddressable IGrainFactory.GetGrain(Type grainInterfaceType, IdSpan grainKey) => throw new NotSupportedException();
        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, Guid grainPrimaryKey) => throw new NotSupportedException();
        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension) => throw new NotSupportedException();
        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, long grainPrimaryKey) => throw new NotSupportedException();
        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension) => throw new NotSupportedException();
        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, string grainPrimaryKey) => throw new NotSupportedException();
        TGrainObserverInterface IGrainFactory.CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj) => throw new NotSupportedException();
        void IGrainFactory.DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj) => throw new NotSupportedException();
    }
}
