using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Orleans;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

/// <summary>
/// issue-511 T-001: behavioral contract that reworded
/// <see cref="WorkflowDefinitionResolutionException"/> messages do not
/// alter <c>WorkflowGrain.CommitAsync</c> control flow. The static
/// counterpart (catch-type, no <c>ex.Message.Contains</c> branching)
/// lives in <c>Mohist.Server.ArchTests.WorkflowGrainContractRules</c>.
/// </summary>
[Collection("MohistDb")]
public sealed class WorkflowGrainProductionContractSpecs
{
    private static readonly DateTimeOffset FixedTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly FakeTimeProvider TimeProvider = new(FixedTime);
    private readonly MohistDbFixture _fixture;

    public WorkflowGrainProductionContractSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task CommitAsync_DifferentResolutionExceptionMessages_ProduceIdenticalControlFlow()
    {
        const string projectId = "proj-resolution-typing";
        await SeedSingleStageTemplateAsync(projectId);

        var firstOutcome = await DriveOnceAsync(
            projectId,
            workflowRunId: "wr-resolution-first",
            exceptionMessage: "Workflow 'first' has no definition for stage 'missing'");

        var secondOutcome = await DriveOnceAsync(
            projectId,
            workflowRunId: "wr-resolution-second",
            exceptionMessage: "totally rewritten wording with different punctuation!!!");

        Assert.Equal(firstOutcome.ExceptionType, secondOutcome.ExceptionType);
        Assert.Equal(typeof(WorkflowDefinitionResolutionException), firstOutcome.ExceptionType);
        Assert.Equal(firstOutcome.InputMessage, firstOutcome.ExceptionMessage);
        Assert.Equal(secondOutcome.InputMessage, secondOutcome.ExceptionMessage);
        Assert.NotEqual(firstOutcome.ExceptionMessage, secondOutcome.ExceptionMessage);
    }

    [Fact]
    public void WorkflowDefinitionResolutionException_CarriesReasonDiscriminator_AndPreservesMessage()
    {
        var ex = new WorkflowDefinitionResolutionException(
            WorkflowDefinitionResolutionException.ResolutionReason.NoStageDefinition,
            "irrelevant message text");

        Assert.Equal(WorkflowDefinitionResolutionException.ResolutionReason.NoStageDefinition, ex.Reason);
        Assert.Equal("irrelevant message text", ex.Message);
    }

    private async Task<ResolutionOutcome> DriveOnceAsync(string projectId, string workflowRunId, string exceptionMessage)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();

        var definitionResolver = BuildThrowingDefinitionResolver(scope.ServiceProvider, exceptionMessage);
        var variableResolver = scope.ServiceProvider.GetRequiredService<WorkflowVariableResolver>();
        var identity = GrainTestContext.Create(workflowRunId, new StubProfileCoordinatorGrainFactory());
        var grain = new WorkflowGrain(
            identity.Context,
            identity.Runtime,
            store,
            scope.ServiceProvider.GetRequiredService<IDispatchSnapshotStore>(),
            definitionResolver,
            variableResolver,
            TimeProvider,
            NullLogger<WorkflowGrain>.Instance);
        await grain.OnActivateAsync(CancellationToken.None);

        var context = new WorkflowIssueContext(projectId, 1, null);

        var thrown = await Assert.ThrowsAsync<WorkflowDefinitionResolutionException>(
            () => grain.EnsureStartedAsync(context));

        return new ResolutionOutcome(
            thrown.GetType(),
            thrown.Message,
            exceptionMessage);
    }

    /// <summary>
    /// Build a real <see cref="WorkflowDefinitionResolver"/> backed by a
    /// <see cref="StubFailingOnStageLoadProfileProvider"/>. The provider
    /// returns a valid definition for the startup call so
    /// <c>EnsureCreatedRunAsync</c> succeeds, then returns
    /// <c>null</c> from the stage-spec call so
    /// <c>LoadStageSpecsAsync</c> raises a
    /// <see cref="WorkflowDefinitionResolutionException"/> with the
    /// requested message.
    /// </summary>
    private WorkflowDefinitionResolver BuildThrowingDefinitionResolver(IServiceProvider services, string exceptionMessage)
    {
        var dbFactory = services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        var provider = new StubFailingOnStageLoadProfileProvider(exceptionMessage);
        return new WorkflowDefinitionResolver(
            dbFactory,
            WorkflowGrainTestHelpers.CreateEmptyConfigService(),
            provider);
    }

    private async Task SeedSingleStageTemplateAsync(string projectId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await factory.CreateDbContextAsync();

        const string templateId = "spec/contract-resolution";
        var definition = new WorkflowDefinition(
            [
                new StageDefinition("plan", [new("draft", "Draft", "spec/task")], []),
            ]);
        db.ProjectWorkflowTemplates.Add(new ProjectWorkflowTemplateRow
        {
            ProjectId = projectId,
            TemplateId = templateId,
            Template = WorkflowGrainTestHelpers.SerializeProfile(definition, templateId),
        });
        db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
        {
            ProjectId = projectId,
            DefaultWorkflowProfileId = "mohist/local",
        });
        await db.SaveChangesAsync();
    }

    private sealed record ResolutionOutcome(
        Type ExceptionType,
        string ExceptionMessage,
        string InputMessage);

    /// <summary>
    /// <see cref="IWorkflowProfileProvider"/> that returns a valid
    /// definition for the first <c>GetDefinitionAsync</c> call (the
    /// startup call) and <c>null</c> for every subsequent call (the
    /// stage-spec load). When the manager's
    /// <c>LoadBoundTemplateAsync</c> sees a <c>null</c> bound
    /// definition, it throws
    /// <see cref="WorkflowDefinitionResolutionException"/> with
    /// <see cref="WorkflowDefinitionResolutionException.ResolutionReason.NoCurrentDefinition"/>.
    /// </summary>
    private sealed class StubFailingOnStageLoadProfileProvider : IWorkflowProfileProvider
    {
        private readonly string _exceptionMessage;
        private int _calls;

        public StubFailingOnStageLoadProfileProvider(string exceptionMessage)
        {
            _exceptionMessage = exceptionMessage;
        }

        public Task<WorkflowDefinition?> GetDefinitionAsync(string projectId, string profileId, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                return Task.FromResult<WorkflowDefinition?>(new WorkflowDefinition(
                    [
                        new StageDefinition("plan", [new("draft", "Draft", "spec/task")], []),
                    ]));
            }

            throw new WorkflowDefinitionResolutionException(
                WorkflowDefinitionResolutionException.ResolutionReason.NoCurrentDefinition,
                _exceptionMessage);
        }

        public Task<bool> ContainsAsync(string projectId, string profileId, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<string?> GetDefaultProfileIdAsync(string projectId, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task<IReadOnlyList<WorkflowProfileCollectionEntry>> ListAsync(string projectId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<WorkflowProfileCollectionEntry?> GetAsync(string projectId, string profileId, CancellationToken ct = default) =>
            Task.FromResult<WorkflowProfileCollectionEntry?>(new WorkflowProfileCollectionEntry(
                projectId,
                profileId,
                profileId,
                string.Empty,
                WorkflowProfileSourceProvenance.BuiltIn,
                true,
                null));

        public Task<string?> GetDefinitionSourceAsync(string projectId, string profileId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<WorkflowProfileSourceProvenance?> GetSourceProvenanceAsync(string projectId, string profileId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<WorkflowProfileSaveResult> CreateAsync(
            string projectId,
            WorkflowProfileCollectionEntry request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<WorkflowProfileSaveResult> UpdateAsync(
            string projectId,
            WorkflowProfileCollectionEntry request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(string projectId, string profileId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlySet<string>> GetDisabledProfileIdsAsync(string projectId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(WorkflowProfileCatalog.IdComparer));

        public Task SetProfileEnabledAsync(string projectId, string profileId, bool enabled, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class InertPromptLoader : Mohist.Server.Workflow.Services.Prompts.IPromptLoader
    {
        public Dictionary<string, string> LoadAll() => new(StringComparer.Ordinal);
    }

    private sealed class StubProfileCoordinatorGrainFactory : IGrainFactory
    {
        private static readonly IWorkflowProfileReferenceCoordinatorGrain Stub = new StubCoordinatorGrain();

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix)
        {
            if (typeof(TGrainInterface) == typeof(IWorkflowProfileReferenceCoordinatorGrain))
                return (TGrainInterface)(object)Stub;
            throw new NotSupportedException(
                $"{nameof(StubProfileCoordinatorGrainFactory)} does not support {typeof(TGrainInterface).Name}");
        }

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix)
            => throw new NotSupportedException();
        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix)
            => throw new NotSupportedException();
        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix)
            => throw new NotSupportedException();
        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix)
            => throw new NotSupportedException();
        TGrainObserverInterface IGrainFactory.CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            => throw new NotSupportedException();
        void IGrainFactory.DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            => throw new NotSupportedException();
        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, Guid grainPrimaryKey)
            => throw new NotSupportedException();
        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, long grainPrimaryKey)
            => throw new NotSupportedException();
        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, string grainPrimaryKey)
            => throw new NotSupportedException();
        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension)
            => throw new NotSupportedException();
        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension)
            => throw new NotSupportedException();
        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(GrainId grainId)
            => throw new NotSupportedException();
        IAddressable IGrainFactory.GetGrain(GrainId grainId)
            => throw new NotSupportedException();
        IAddressable IGrainFactory.GetGrain(GrainId grainId, GrainInterfaceType interfaceType)
            => throw new NotSupportedException();
        IAddressable IGrainFactory.GetGrain(Type interfaceType, IdSpan grainKey, string grainClassNamePrefix)
            => throw new NotSupportedException();
        IAddressable IGrainFactory.GetGrain(Type interfaceType, IdSpan grainKey)
            => throw new NotSupportedException();
    }

    private sealed class StubCoordinatorGrain : IWorkflowProfileReferenceCoordinatorGrain
    {
        public Task<WorkflowProfileReferenceResult> SetProjectDefaultAsync(
            WorkflowProfileCommandPayload.SetProjectDefault payload,
            string commandId,
            long? expectedRevision) =>
            throw new NotSupportedException();

        public Task<WorkflowProfileReferenceResult> BindWorkflowRunAsync(
            WorkflowProfileCommandPayload.BindWorkflowRun payload,
            string commandId,
            long? expectedRevision) =>
            Task.FromResult(new WorkflowProfileReferenceResult(
                WorkflowProfileReferenceResultCode.Applied,
                payload.ProfileId,
                expectedRevision ?? 1L));

        public Task<WorkflowProfileReferenceResult> DeleteProfileAsync(
            WorkflowProfileCommandPayload.DeleteProfile payload,
            string commandId,
            long? expectedRevision) =>
            throw new NotSupportedException();
    }
}
