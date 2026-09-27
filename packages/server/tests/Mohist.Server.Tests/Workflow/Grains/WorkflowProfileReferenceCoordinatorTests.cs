using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Tests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Grains.Coordinator;
using Mohist.Server.Workflow.Services;
using Orleans;
using Orleans.Runtime;
using Xunit;

namespace Mohist.Server.Tests.Workflow.Grains;

[Trait("level", "L0")]
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
    public async Task UpdateProfile_AppliesUnderReadPrecondition_AndClearsFence()
    {
        var (coordinator, state, provider, database) = NewUpdateFixture();
        const string profileId = "apply/profile";
        var created = await provider.CreateAsync("proj-1", Entry(profileId, BuildStage("build")));
        var payload = new WorkflowProfileCommandPayload.UpdateProfile(
            "proj-1",
            profileId,
            Name: "Renamed",
            Description: "Updated",
            DefinitionSource: BuildStage("deliver"),
            ExpectedContentRevision: created.Profile.Revision);

        var result = await coordinator.UpdateProfileAsync(payload, "cmd-apply-1", expectedRevision: null);

        Assert.True(result.ValidationResult.IsValid);
        Assert.Null(state.State.Pending);
        var detail = (await provider.GetDetailAsync("proj-1", profileId))!;
        Assert.Contains("stage: deliver", detail.Profile.DefinitionSource, StringComparison.Ordinal);
        Assert.Equal(detail.Profile.Revision, result.Profile.Revision);
        Assert.NotEqual(created.Profile.Revision, result.Profile.Revision);
        await database.DisposeAsync();
    }

    [Fact]
    public async Task UpdateProfile_RejoinAfterLostResponse_ReportsStoredStateWithoutSecondWrite()
    {
        var (coordinator, state, provider, database) = NewUpdateFixture();
        const string profileId = "lost/profile";
        var created = await provider.CreateAsync("proj-1", Entry(profileId, BuildStage("build")));
        var payload = new WorkflowProfileCommandPayload.UpdateProfile(
            "proj-1",
            profileId,
            Name: "Renamed",
            Description: "Updated",
            DefinitionSource: BuildStage("deliver"),
            ExpectedContentRevision: created.Profile.Revision);

        var applied = await coordinator.UpdateProfileAsync(payload, "cmd-lost-1", expectedRevision: null);

        // Crash after commit but before fence cleanup: restore the pending
        // fence, then retry the same command id after the lost response.
        state.State = state.State with
        {
            Pending = new PendingWorkflowProfileCommand(
                "cmd-lost-1",
                payload.Kind,
                payload.ProfileId,
                ExpectedRevision: 1L,
                WorkflowProfileCommandPayloadCodec.Serialize(payload)),
        };

        var rejoined = await coordinator.UpdateProfileAsync(payload, "cmd-lost-1", expectedRevision: null);

        // The rejoined answer reports the stored revision and content this
        // command produced; it neither overwrote anything nor failed as a
        // fresh stale write would.
        Assert.Equal(applied.Profile.Revision, rejoined.Profile.Revision);
        Assert.Contains("stage: deliver", rejoined.Profile.DefinitionSource, StringComparison.Ordinal);
        Assert.Null(state.State.Pending);
        var detail = (await provider.GetDetailAsync("proj-1", profileId))!;
        Assert.Equal(applied.Profile.Revision, detail.Profile.Revision);
        await database.DisposeAsync();
    }

    [Fact]
    public async Task UpdateProfile_SupersededPendingFence_DoesNotOverwrite_AndUnblocksLaterEdits()
    {
        var (coordinator, state, provider, database) = NewUpdateFixture();
        const string profileId = "superseded/profile";
        var created = await provider.CreateAsync("proj-1", Entry(profileId, BuildStage("build")));

        // A pending fence whose write never happened (crash before commit).
        var stalePayload = new WorkflowProfileCommandPayload.UpdateProfile(
            "proj-1",
            profileId,
            Name: "Stale",
            Description: "Never landed",
            DefinitionSource: BuildStage("stale-stage"),
            ExpectedContentRevision: created.Profile.Revision);
        state.State = new WorkflowProfileCoordinatorState(
            new PendingWorkflowProfileCommand(
                "cmd-superseded-1",
                stalePayload.Kind,
                stalePayload.ProfileId,
                ExpectedRevision: 1L,
                WorkflowProfileCommandPayloadCodec.Serialize(stalePayload)));

        // A different caller lands a newer edit first.
        var newer = await provider.UpdateAsync(
            "proj-1",
            Entry(profileId, BuildStage("newer-stage"), name: "Newer"),
            created.Profile.Revision!);

        // The next command replays the pending fence, sees it superseded,
        // and proceeds: no overwrite of the newer content, no block.
        var latest = await provider.GetDetailAsync("proj-1", profileId);
        var freshPayload = new WorkflowProfileCommandPayload.UpdateProfile(
            "proj-1",
            profileId,
            Name: "Fresh",
            Description: "Updated",
            DefinitionSource: BuildStage("fresh-stage"),
            ExpectedContentRevision: latest!.Profile.Revision);
        var result = await coordinator.UpdateProfileAsync(freshPayload, "cmd-superseded-2", expectedRevision: null);

        Assert.True(result.ValidationResult.IsValid);
        Assert.Null(state.State.Pending);
        var detail = (await provider.GetDetailAsync("proj-1", profileId))!;
        Assert.Contains("stage: fresh-stage", detail.Profile.DefinitionSource, StringComparison.Ordinal);
        Assert.DoesNotContain("stale-stage", detail.Profile.DefinitionSource, StringComparison.Ordinal);
        Assert.NotEqual(newer.Profile.Revision, detail.Profile.Revision);
        await database.DisposeAsync();
    }

    [Fact]
    public async Task UpdateProfile_SameCommandIdRejoin_WhenSuperseded_ThrowsRevisionConflict()
    {
        var (coordinator, state, provider, database) = NewUpdateFixture();
        const string profileId = "rejoin-stale/profile";
        var created = await provider.CreateAsync("proj-1", Entry(profileId, BuildStage("build")));

        var stalePayload = new WorkflowProfileCommandPayload.UpdateProfile(
            "proj-1",
            profileId,
            Name: "Stale",
            Description: string.Empty,
            DefinitionSource: BuildStage("stale-stage"),
            ExpectedContentRevision: created.Profile.Revision);
        state.State = new WorkflowProfileCoordinatorState(
            new PendingWorkflowProfileCommand(
                "cmd-rejoin-stale-1",
                stalePayload.Kind,
                stalePayload.ProfileId,
                ExpectedRevision: 1L,
                WorkflowProfileCommandPayloadCodec.Serialize(stalePayload)));

        // Someone else owns the row now.
        var newer = await provider.UpdateAsync(
            "proj-1",
            Entry(profileId, BuildStage("newer-stage"), name: "Newer"),
            created.Profile.Revision!);

        var conflict = await Assert.ThrowsAsync<WorkflowProfileRevisionConflictException>(() =>
            coordinator.UpdateProfileAsync(stalePayload, "cmd-rejoin-stale-1", expectedRevision: null));

        Assert.Equal(newer.Profile.Revision, conflict.CurrentRevision);
        Assert.Null(state.State.Pending);
        var detail = (await provider.GetDetailAsync("proj-1", profileId))!;
        Assert.Contains("stage: newer-stage", detail.Profile.DefinitionSource, StringComparison.Ordinal);

        // The rejected command does not poison later ones: a fresh edit
        // under the current revision still applies.
        var fresh = await coordinator.UpdateProfileAsync(
            new WorkflowProfileCommandPayload.UpdateProfile(
                "proj-1",
                profileId,
                Name: "Fresh",
                Description: string.Empty,
                DefinitionSource: BuildStage("fresh-stage"),
                ExpectedContentRevision: detail.Profile.Revision),
            "cmd-rejoin-stale-2",
            expectedRevision: null);
        Assert.True(fresh.ValidationResult.IsValid);
        await database.DisposeAsync();
    }

    [Fact]
    public async Task UpdateProfile_MissingPrecondition_AtCoordinator_IsRejectedWithoutFence()
    {
        var (coordinator, state, _, database) = NewUpdateFixture();
        var payload = new WorkflowProfileCommandPayload.UpdateProfile(
            "proj-1",
            "missing/profile",
            Name: "No Precondition",
            Description: string.Empty,
            DefinitionSource: BuildStage("build"),
            ExpectedContentRevision: null);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            coordinator.UpdateProfileAsync(payload, "cmd-missing-1", expectedRevision: null));

        Assert.Null(state.State.Pending);
        Assert.Equal(0, state.WriteCount);
        await database.DisposeAsync();
    }

    private static string BuildStage(string stage) => $$"""
        id: spec-profile
        stages:
          - stage: {{stage}}
            tasks:
              - id: t
                uses: test/action
                with: {}
            checks: []
        """;

    private static WorkflowProfileCollectionEntry Entry(
        string profileId,
        string yaml,
        string name = "Custom") =>
        new(
            ProjectId: "proj-1",
            ProfileId: profileId,
            Name: name,
            Description: string.Empty,
            SourceProvenance: WorkflowProfileSourceProvenance.Verbatim,
            IsBuiltIn: false,
            DefinitionSource: yaml);

    private static (WorkflowProfileReferenceCoordinatorGrain Coordinator, FakePersistentState State, WorkflowProfileProvider Provider, TestSqliteDatabase Database) NewUpdateFixture()
    {
        var database = TestSqliteDatabase.CreateModelSchema();
        var provider = new WorkflowProfileProvider(
            new TestDbContextFactory(database.Options),
            NullActionCatalogSource.Instance);
        var state = new FakePersistentState(WorkflowProfileCoordinatorState.Empty);
        var coordinator = new WorkflowProfileReferenceCoordinatorGrain(
            state,
            grains: null!,
            NullLogger<WorkflowProfileReferenceCoordinatorGrain>.Instance,
            provider);
        using var db = new MohistDbContext(database.Options);
        db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
        {
            ProjectId = "proj-1",
            DefaultWorkflowProfileId = "mohist/local",
            DefaultWorkflowProfileIdKey = null,
            Variables = "{}",
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
        return (coordinator, state, provider, database);
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
