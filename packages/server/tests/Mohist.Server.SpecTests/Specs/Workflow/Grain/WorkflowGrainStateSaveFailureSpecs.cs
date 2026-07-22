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
        var defaults = await scope.ServiceProvider
            .GetRequiredService<WorkflowRunProfileManager>()
            .GetDefaultVariablesAsync(workflowRunId);
        Assert.NotNull(run);
        Assert.Equal(WorkflowRunStatus.Pending, run!.Status);
        Assert.Equal(projectId, run.Metadata.Annotations!["projectId"]);
        Assert.Equal("1", run.Metadata.Annotations["issueNumber"]);
        Assert.Equal("2", run.Metadata.Annotations["epicNumber"]);
        Assert.Equal(string.Empty, defaults.DefaultVars!.Value.GetProperty("archive").GetString());
        Assert.Single(await events.ListAsync(workflowRunId), entry =>
            entry.Envelope.Type == EventCatalog.ReverseDns.WorkflowRunStarted);
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
        Assert.Equal("2", persisted!.Metadata.Annotations!["epicNumber"]);
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
        Assert.False(persisted.Metadata.Annotations!.ContainsKey("epicNumber"));
    }

    private static WorkflowGrain CreateGrain(
        IServiceProvider services,
        IWorkflowRunStore store,
        string workflowRunId) =>
        new(
            store,
            services.GetRequiredService<WorkflowProfileManager>(),
            services.GetRequiredService<WorkflowRunProfileManager>(),
            TimeProvider,
            NullLogger<WorkflowGrain>.Instance)
        {
            GrainKeyForTest = workflowRunId,
        };

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
            DefaultTemplateId = templateId,
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
    }
}
