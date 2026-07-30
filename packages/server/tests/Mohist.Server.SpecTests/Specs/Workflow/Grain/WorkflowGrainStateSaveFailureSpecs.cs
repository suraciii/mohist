using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.SpecTests.Support;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

[Collection("MohistDb")]
public sealed class WorkflowGrainStateSaveFailureSpecs
{
    private static readonly DateTimeOffset FixedTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly FakeTimeProvider TimeProvider = new(FixedTime);
    private readonly MohistDbFixture _fixture;

    public WorkflowGrainStateSaveFailureSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task EnsureStarted_DuplicateDeliveryRefreshesCurrentContextWithoutRestarting()
    {
        const string workflowRunId = "wr-ensure-started-duplicate";
        const string projectId = "proj-ensure-started-duplicate";
        var context = new WorkflowIssueContext(projectId, 1, null);

        await SeedWorkflowTemplateAsync(projectId);
        await using var scope = _fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();
        var events = scope.ServiceProvider.GetRequiredService<IEventStore>();
        var grain = CreateGrain(scope.ServiceProvider, store, workflowRunId);
        await grain.OnActivateAsync(CancellationToken.None);

        await grain.EnsureStartedAsync(context);
        await grain.EnsureStartedAsync(context with { EpicNumber = 2 });

        var run = await store.LoadAsync(workflowRunId);
        var variables = await scope.ServiceProvider
            .GetRequiredService<WorkflowVariableResolver>()
            .ResolveEffectiveVariablesAsync(workflowRunId, null);
        Assert.NotNull(run);
        Assert.Equal(WorkflowRunStatus.Pending, run!.Status);
        Assert.Equal(projectId, run.Metadata.ProjectId);
        Assert.Equal(1, run.Metadata.IssueNumber);
        Assert.Equal(2, run.Metadata.EpicNumber);
        Assert.False(variables.TryGetProperty("archive", out _));
        Assert.Single(await events.ListAsync(workflowRunId), entry =>
            entry.Envelope.Type == EventCatalog.ReverseDns.WorkflowRunStarted);
    }

    [Theory]
    [InlineData("", 1)]
    [InlineData("proj-invalid-initial-context", 0)]
    [InlineData("proj-invalid-initial-context", -1)]
    public async Task EnsureStarted_RejectsInvalidInitialIssueContext(string projectId, int issueNumber)
    {
        var workflowRunId = $"wr-invalid-initial-context-{issueNumber}-{(string.IsNullOrWhiteSpace(projectId) ? "blank" : "project")}";
        await using var scope = _fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();
        var grain = CreateGrain(scope.ServiceProvider, store, workflowRunId);
        await grain.OnActivateAsync(CancellationToken.None);

        if (issueNumber <= 0)
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                grain.EnsureStartedAsync(new WorkflowIssueContext(projectId, issueNumber, null)));
        }
        else
        {
            await Assert.ThrowsAsync<ArgumentException>(() =>
                grain.EnsureStartedAsync(new WorkflowIssueContext(projectId, issueNumber, null)));
        }

        Assert.Null(await store.LoadAsync(workflowRunId));
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    [InlineData(null, 7)]
    public async Task Start_RejectsInvalidTypedIssueContext(int? issueNumber, int? epicNumber)
    {
        var workflowRunId = $"wr-invalid-start-context-{issueNumber}-{epicNumber}";
        await using var scope = _fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();
        var grain = CreateGrain(scope.ServiceProvider, store, workflowRunId);
        await grain.OnActivateAsync(CancellationToken.None);
        var input = new WorkflowStartInput(Metadata: new WorkflowRunMetadata(
            null,
            FixedTime,
            ProjectId: "proj-invalid-start-context",
            IssueNumber: issueNumber,
            EpicNumber: epicNumber));

        if (issueNumber is <= 0)
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => grain.StartAsync(input));
        }
        else
        {
            await Assert.ThrowsAsync<ArgumentException>(() => grain.StartAsync(input));
        }

        Assert.Null(await store.LoadAsync(workflowRunId));
    }

    [Fact]
    public async Task RefreshIssueContext_SaveFailureQuarantinesActivationAndRedeliveryConverges()
    {
        const string workflowRunId = "wr-context-refresh-save-failure";
        const string projectId = "proj-context-refresh-save-failure";
        var initialContext = new WorkflowIssueContext(projectId, 1, null);
        var refreshedContext = new WorkflowIssueContext(projectId, 1, 2);

        await SeedWorkflowTemplateAsync(projectId);
        await using var scope = _fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();
        var started = CreateGrain(scope.ServiceProvider, store, workflowRunId);
        await started.OnActivateAsync(CancellationToken.None);
        await started.EnsureStartedAsync(initialContext);

        var failingStore = new FailingWorkflowRunStore(store);
        var failedActivation = CreateGrain(scope.ServiceProvider, failingStore, workflowRunId);
        await failedActivation.OnActivateAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            failedActivation.RefreshIssueContextAsync(refreshedContext));
        await Assert.ThrowsAsync<InvalidOperationException>(() => failedActivation.StartAsync());
        Assert.Equal(1, failingStore.StateOnlySaveAttempts);

        var redelivery = CreateGrain(scope.ServiceProvider, failingStore, workflowRunId);
        await redelivery.OnActivateAsync(CancellationToken.None);
        await redelivery.RefreshIssueContextAsync(refreshedContext);

        var persisted = await store.LoadAsync(workflowRunId);
        Assert.NotNull(persisted);
        Assert.Equal(2, persisted!.Metadata.EpicNumber);
        Assert.Equal(2, failingStore.StateOnlySaveAttempts);
    }

    [Fact]
    public async Task RefreshIssueContext_TerminalRunNoops()
    {
        const string workflowRunId = "wr-terminal-context-refresh";
        const string projectId = "proj-terminal-context-refresh";

        await SeedWorkflowTemplateAsync(projectId);
        await using var scope = _fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();
        var grain = CreateGrain(scope.ServiceProvider, store, workflowRunId);
        await grain.OnActivateAsync(CancellationToken.None);
        await grain.EnsureStartedAsync(new WorkflowIssueContext(projectId, 1, null));
        await grain.StopAsync("test");

        await grain.RefreshIssueContextAsync(new WorkflowIssueContext(projectId, 1, 2));

        var persisted = await store.LoadAsync(workflowRunId);
        Assert.NotNull(persisted);
        Assert.Equal(WorkflowRunStatus.Stopped, persisted!.Status);
        Assert.Null(persisted.Metadata.EpicNumber);
    }

    private static WorkflowGrain CreateGrain(
        IServiceProvider services,
        IWorkflowRunStore store,
        string workflowRunId)
    {
        var identity = GrainTestContext.Create(workflowRunId, new StubProfileCoordinatorGrainFactory());
        return new WorkflowGrain(
            identity.Context,
            identity.Runtime,
            store,
            services.GetRequiredService<WorkflowProfileManager>(),
            services.GetRequiredService<WorkflowVariableResolver>(),
            TimeProvider,
            NullLogger<WorkflowGrain>.Instance);
    }

    private async Task SeedWorkflowTemplateAsync(string projectId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var definition = new WorkflowDefinition( [
            new StageDefinition("plan", [new("draft", "Draft", "spec/task")], []),
        ]);
        const string templateId = "spec/workflow";
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

    private sealed class FailingWorkflowRunStore : IWorkflowRunStore
    {
        private readonly IWorkflowRunStore _inner;
        private int _remainingFailures = 1;

        public FailingWorkflowRunStore(IWorkflowRunStore inner)
        {
            _inner = inner;
        }

        public int StateOnlySaveAttempts { get; private set; }

        public Task<WorkflowRun?> LoadAsync(string workflowRunId, CancellationToken ct = default) =>
            _inner.LoadAsync(workflowRunId, ct);

        public Task SaveAsync(WorkflowRun run, CancellationToken ct = default)
        {
            StateOnlySaveAttempts++;
            if (Interlocked.CompareExchange(ref _remainingFailures, 0, 1) == 1)
                throw new InvalidOperationException("simulated state-only save failure");
            return _inner.SaveAsync(run, ct);
        }

        public Task SaveAsync(WorkflowRun run, IReadOnlyList<WorkflowEvent> events, CancellationToken ct = default) =>
            _inner.SaveAsync(run, events, ct);

        public Task DeleteAsync(string workflowRunId, CancellationToken ct = default) =>
            _inner.DeleteAsync(workflowRunId, ct);
    }

    /// <summary>
    /// Minimal <see cref="IGrainFactory"/> that returns a stub
    /// <see cref="IWorkflowProfileReferenceCoordinatorGrain"/> for any string
    /// key. The stub yields an <see cref="WorkflowProfileReferenceResultCode.Applied"/>
    /// result for any bind request, mirroring the pre-removal
    /// <c>BindProfileForTest</c> behaviour these specs depended on, but via
    /// the production grain call site — no override hook on
    /// <see cref="WorkflowGrain"/>.
    /// </summary>
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
            => throw new NotSupportedException(
                $"{nameof(StubProfileCoordinatorGrainFactory)} does not support {typeof(TGrainInterface).Name}");

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix)
            => throw new NotSupportedException(
                $"{nameof(StubProfileCoordinatorGrainFactory)} does not support {typeof(TGrainInterface).Name}");

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix)
            => throw new NotSupportedException(
                $"{nameof(StubProfileCoordinatorGrainFactory)} does not support {typeof(TGrainInterface).Name}");

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix)
            => throw new NotSupportedException(
                $"{nameof(StubProfileCoordinatorGrainFactory)} does not support {typeof(TGrainInterface).Name}");

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
            throw new NotSupportedException(
                $"{nameof(StubCoordinatorGrain)} only supports BindWorkflowRunAsync");

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
            throw new NotSupportedException(
                $"{nameof(StubCoordinatorGrain)} only supports BindWorkflowRunAsync");
    }
}
