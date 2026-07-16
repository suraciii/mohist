using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Epic;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain.Definition;
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task ApplyIssueLineage_StateOnlySaveFailure_QuarantinesActivationAndFreshRedeliveryPersistsLineage()
    {
        const string workflowRunId = "wr-lineage-save-failure";
        const string projectId = "proj-lineage-save-failure";
        const string issueId = "issue-lineage-save-failure";

        await using var scope = _fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();
        await SeedEpicAsync(scope.ServiceProvider, projectId, "epic_2");
        await store.SaveAsync(CreateBoundRun(workflowRunId, projectId, issueId));

        var failingStore = new FailingWorkflowRunStore(store);
        var failedActivation = CreateGrain(scope.ServiceProvider, failingStore, workflowRunId);
        await failedActivation.OnActivateAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => failedActivation.ApplyIssueLineageAsync(
            new WorkflowIssueLineage(issueId, "epic_2", 2)));

        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() => failedActivation.ApplyIssueLineageAsync(
            new WorkflowIssueLineage(issueId, "epic_2", 2)));
        Assert.Contains("must reload", rejected.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, failingStore.StateOnlySaveAttempts);

        var unchanged = await store.LoadAsync(workflowRunId);
        Assert.NotNull(unchanged);
        Assert.Equal(1, unchanged!.IssueLineageVersion);
        Assert.False(unchanged.Metadata.Annotations!.ContainsKey("epicId"));

        var redeliveredActivation = CreateGrain(scope.ServiceProvider, failingStore, workflowRunId);
        await redeliveredActivation.OnActivateAsync(CancellationToken.None);
        await redeliveredActivation.ApplyIssueLineageAsync(new WorkflowIssueLineage(issueId, "epic_2", 2));

        var persisted = await store.LoadAsync(workflowRunId);
        Assert.NotNull(persisted);
        Assert.Equal(2, persisted!.IssueLineageVersion);
        Assert.Equal("epic_2", persisted.Metadata.Annotations!.GetValueOrDefault("epicId"));
        Assert.Equal(2, failingStore.StateOnlySaveAttempts);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task PrepareIssueStart_StateOnlySaveFailure_QuarantinesActivationAndFreshRedeliveryConfirmsBinding()
    {
        const string workflowRunId = "wr-binding-save-failure";
        const string projectId = "proj-binding-save-failure";
        const string issueId = "issue-binding-save-failure";

        await SeedWorkflowTemplateAsync(projectId);

        await using var scope = _fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();
        var failingStore = new FailingWorkflowRunStore(store);
        var input = CreateStartInput(projectId, issueId);
        var failedActivation = CreateGrain(scope.ServiceProvider, failingStore, workflowRunId);
        await failedActivation.OnActivateAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => failedActivation.PrepareIssueStartAsync(input));

        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() => failedActivation.PrepareIssueStartAsync(input));
        Assert.Contains("must reload", rejected.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, failingStore.StateOnlySaveAttempts);
        Assert.Null(await store.LoadAsync(workflowRunId));

        var redeliveredActivation = CreateGrain(scope.ServiceProvider, failingStore, workflowRunId);
        await redeliveredActivation.OnActivateAsync(CancellationToken.None);
        await redeliveredActivation.PrepareIssueStartAsync(input);
        await redeliveredActivation.ConfirmIssueBindingAsync(new WorkflowIssueBinding(issueId, null, 1));

        var persisted = await store.LoadAsync(workflowRunId);
        Assert.NotNull(persisted);
        Assert.Equal(WorkflowRunStatus.Pending, persisted!.Status);
        Assert.Equal(1, persisted.IssueLineageVersion);
        Assert.Equal(2, failingStore.StateOnlySaveAttempts);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task Start_StateOnlySaveFailure_RejectsDirtyActivationBeforeItCanPersistStaleLineage()
    {
        const string workflowRunId = "wr-paused-lineage-save-failure";
        const string projectId = "proj-paused-lineage-save-failure";
        const string issueId = "issue-paused-lineage-save-failure";

        await using var scope = _fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();
        var pausedRun = CreateBoundRun(workflowRunId, projectId, issueId);
        pausedRun.Pause();
        await store.SaveAsync(pausedRun);

        var failingStore = new FailingWorkflowRunStore(store);
        var failedActivation = CreateGrain(scope.ServiceProvider, failingStore, workflowRunId);
        await failedActivation.OnActivateAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => failedActivation.ApplyIssueLineageAsync(
            new WorkflowIssueLineage(issueId, "epic_2", 2)));

        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() => failedActivation.StartAsync());
        Assert.Contains("must reload", rejected.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, failingStore.StateOnlySaveAttempts);

        var persisted = await store.LoadAsync(workflowRunId);
        Assert.NotNull(persisted);
        Assert.Equal(WorkflowRunStatus.Paused, persisted!.Status);
        Assert.Equal(1, persisted.IssueLineageVersion);
        Assert.False(persisted.Metadata.Annotations!.ContainsKey("epicId"));
    }

    private static WorkflowGrain CreateGrain(
        IServiceProvider services,
        IWorkflowRunStore store,
        string workflowRunId) =>
        new(
            store,
            services.GetRequiredService<WorkflowProfileManager>(),
            services.GetRequiredService<WorkflowSessionHealthService>(),
            TimeProvider,
            NullLogger<WorkflowGrain>.Instance)
        {
            GrainKeyForTest = workflowRunId,
        };

    private static WorkflowRun CreateBoundRun(string workflowRunId, string projectId, string issueId)
    {
        var run = WorkflowRun.Create(workflowRunId, Definition(), FixedTime, CreateStartInput(projectId, issueId).Metadata);
        run.PrepareForIssueBinding();
        run.IssueLineageVersion = 1;
        run.ConfirmIssueBinding(FixedTime);
        return run;
    }

    private static WorkflowStartInput CreateStartInput(string projectId, string issueId) =>
        new(Metadata: new WorkflowRunMetadata(
            Name: null,
            CreatedAt: FixedTime,
            Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["projectId"] = projectId,
                ["issueId"] = issueId,
            }));

    private async Task SeedWorkflowTemplateAsync(string projectId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var definition = Definition();
        db.ProjectWorkflowTemplates.Add(new ProjectWorkflowTemplateRow
        {
            ProjectId = projectId,
            TemplateId = definition.Id,
            Template = JsonSerializer.Serialize(definition, WorkflowYamlSerializer.JsonOptions),
        });
        db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
        {
            ProjectId = projectId,
            DefaultTemplateId = definition.Id,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedEpicAsync(IServiceProvider services, string projectId, string epicId)
    {
        var factory = services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        db.Epics.Add(new EpicRow
        {
            Id = epicId,
            ProjectId = projectId,
            Number = 1,
            Title = epicId,
            Description = "",
            Priority = "p2",
            Status = "running",
            CreatedAt = FixedTime,
            UpdatedAt = FixedTime,
        });
        await db.SaveChangesAsync();
    }

    private static WorkflowDefinition Definition() =>
        new("spec/workflow", [new StageDefinition("plan", [new("draft", "Draft", "spec/task")], [])]);

    private sealed class FailingWorkflowRunStore : IWorkflowRunStore
    {
        private readonly IWorkflowRunStore _delegate;
        private int _stateOnlySaveFailuresRemaining = 1;

        public int StateOnlySaveAttempts { get; private set; }

        public FailingWorkflowRunStore(IWorkflowRunStore @delegate)
        {
            _delegate = @delegate;
        }

        public Task<WorkflowRun?> LoadAsync(string workflowRunId, CancellationToken ct = default) =>
            _delegate.LoadAsync(workflowRunId, ct);

        public Task SaveAsync(WorkflowRun run, CancellationToken ct = default)
        {
            StateOnlySaveAttempts++;
            if (Interlocked.CompareExchange(ref _stateOnlySaveFailuresRemaining, 0, 1) == 1)
                throw new InvalidOperationException("simulated state-only save failure");
            return _delegate.SaveAsync(run, ct);
        }

        public Task SaveAsync(
            WorkflowRun run,
            IReadOnlyList<WorkflowEvent> events,
            CancellationToken ct = default) =>
            _delegate.SaveAsync(run, events, ct);
    }
}
