using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Artifacts;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

[Collection("MohistDb")]
public sealed partial class WorkflowGrainStateSaveFailureSpecs
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

    [Fact]
    public async Task TerminalReport_SaveFailure_LeavesSnapshotIntact()
    {
        const string workflowRunId = "wr-snapshot-save-failure";
        const string projectId = "proj-snapshot-save-failure";
        const string workerId = "worker-snapshot-save-failure";
        var context = new WorkflowIssueContext(projectId, 1, null);

        await SeedWorkflowTemplateAsync(projectId);
        await using var scope = _fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();
        var snapshotStore = scope.ServiceProvider.GetRequiredService<IDispatchSnapshotStore>();

        var setup = CreateGrain(scope.ServiceProvider, store, workflowRunId);
        await setup.OnActivateAsync(CancellationToken.None);
        await setup.EnsureStartedAsync(context);
        await setup.AssignWorkerAsync(workerId);
        var workItem = await setup.ClaimNextAsync(workerId);
        Assert.NotNull(workItem);
        var workId = workItem!.Id!;

        var dispatch = new WorkDispatch(workflowRunId, workId, Uses: "spec/task");
        var stored = await setup.StoreActiveWorkDispatchAsync(workerId, workId, dispatch);
        Assert.NotNull(stored);
        Assert.NotNull(await snapshotStore.LoadJsonAsync(workflowRunId, workId));

        var failingStore = new FailingWorkflowRunStore(store);
        var failingGrain = CreateGrain(scope.ServiceProvider, failingStore, workflowRunId);
        await failingGrain.OnActivateAsync(CancellationToken.None);

        var taskRunId = Assert.Single((await store.LoadAsync(workflowRunId))!.CurrentStage().Tasks).Id;
        var report = new TaskReport(
            workId,
            TaskReportStatus.Succeeded,
            Output: null,
            Artifacts: null,
            TaskRunId: taskRunId);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            failingGrain.ReceiveTaskReportAsync(workerId, workId, report));
        Assert.Equal(1, failingStore.EventSaveAttempts);

        Assert.NotNull(await snapshotStore.LoadJsonAsync(workflowRunId, workId));
    }

    [Fact]
    public async Task UnknownObservation_ReminderRegistrationFailureIsRepairedOnActivation()
    {
        const string workflowRunId = "wr-settlement-register-recovery";
        const string projectId = "proj-settlement-register-recovery";
        const string workerId = "worker-settlement-register-recovery";
        var calls = new ReminderCalls { FailNextEnsure = true };

        await SeedWorkflowTemplateAsync(projectId, AgentWorkflowDefinition());
        await using var scope = _fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();
        var failing = CreateReminderGrain(scope.ServiceProvider, store, workflowRunId, calls);
        await failing.OnActivateAsync(CancellationToken.None);
        var binding = await StartAgentWorkAsync(failing, store, workflowRunId, projectId, workerId);
        var observation = new AgentExecutionObservation(
            binding, AgentExecutionObservationKind.Disconnected, "runner-disconnected");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            failing.ObserveAgentExecutionAsync(observation));

        var persisted = await store.LoadAsync(workflowRunId);
        var settlement = Assert.IsType<AgentResultSettlement>(Assert.Single(persisted!.CurrentStage().Tasks).AgentResultSettlement);
        Assert.Equal(AgentResultSettlementState.Unknown, settlement.State);
        Assert.NotNull(settlement.DeadlineAt);
        Assert.Equal(1, calls.EnsureAttempts);

        Assert.Equal(ReportAck.Accepted, await failing.ObserveAgentExecutionAsync(observation));
        var retried = await store.LoadAsync(workflowRunId);
        var retriedSettlement = Assert.IsType<AgentResultSettlement>(
            Assert.Single(retried!.CurrentStage().Tasks).AgentResultSettlement);
        Assert.Equal(settlement.DeadlineAt, retriedSettlement.DeadlineAt);
        Assert.Equal(2, calls.EnsureAttempts);

        var recovered = CreateReminderGrain(scope.ServiceProvider, store, workflowRunId, calls);
        await recovered.OnActivateAsync(CancellationToken.None);

        Assert.Equal(3, calls.EnsureAttempts);
        Assert.Equal(0, calls.RemoveAttempts);
    }

    [Fact]
    public async Task ExplicitStop_ReminderRemovalFailureIsRepairedOnActivation()
    {
        const string workflowRunId = "wr-settlement-remove-recovery";
        const string projectId = "proj-settlement-remove-recovery";
        const string workerId = "worker-settlement-remove-recovery";
        var calls = new ReminderCalls { FailNextRemove = true };

        await SeedWorkflowTemplateAsync(projectId, AgentWorkflowDefinition());
        await using var scope = _fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();
        var failing = CreateReminderGrain(scope.ServiceProvider, store, workflowRunId, calls);
        await failing.OnActivateAsync(CancellationToken.None);
        var binding = await StartAgentWorkAsync(failing, store, workflowRunId, projectId, workerId);
        await failing.ObserveAgentExecutionAsync(new AgentExecutionObservation(
            binding, AgentExecutionObservationKind.StopUnconfirmed, "stop-unconfirmed"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.StopAsync("operator stop"));

        var stopped = await store.LoadAsync(workflowRunId);
        Assert.Equal(WorkflowRunStatus.Stopped, stopped!.Status);
        Assert.Equal(TaskRunStatus.Cancelled, Assert.Single(stopped.CurrentStage().Tasks).Status);
        Assert.Equal(1, calls.RemoveAttempts);

        var recovered = CreateReminderGrain(scope.ServiceProvider, store, workflowRunId, calls);
        await recovered.OnActivateAsync(CancellationToken.None);

        Assert.Equal(2, calls.RemoveAttempts);
    }

    private static WorkflowDefinition AgentWorkflowDefinition() => new([
        new StageDefinition("plan", [new("agent", "Agent", "mohist/opencode")], []),
    ]);

    private static async Task<AgentExecutionBinding> StartAgentWorkAsync(
        WorkflowGrain grain,
        IWorkflowRunStore store,
        string workflowRunId,
        string projectId,
        string workerId)
    {
        await grain.EnsureStartedAsync(new WorkflowIssueContext(projectId, 1, null));
        await grain.AssignWorkerAsync(workerId);
        var work = await grain.ClaimNextAsync(workerId);
        Assert.NotNull(work);
        var run = await store.LoadAsync(workflowRunId);
        var task = Assert.Single(
            run!.Stages.SelectMany(stage => stage.Tasks),
            candidate => candidate.Status == TaskRunStatus.Running
                && candidate.AgentResultSettlement is not null);
        var binding = new AgentExecutionBinding(
            task.Id,
            work!.Id!,
            workerId,
            "agent-session",
            "agent-turn",
            "opencode",
            "runtime-session");
        Assert.Equal(ReportAck.Accepted, await grain.BindAgentExecutionAsync(binding));
        return binding;
    }

    private static WorkflowGrain CreateGrain(
        IServiceProvider services,
        IWorkflowRunStore store,
        string workflowRunId)
    {
        var resolver = services.GetRequiredService<WorkflowDefinitionResolver>();
        var identity = GrainTestContext.Create(
            workflowRunId,
            new WorkflowGrainTestProfileCoordinatorFactory(store, resolver));
        return new WorkflowGrain(
            identity.Context,
            identity.Runtime,
            store,
            services.GetRequiredService<IDispatchSnapshotStore>(),
            resolver,
            services.GetRequiredService<WorkflowVariableResolver>(),
            services.GetRequiredService<IWorkflowArtifactBindService>(),
            Options.Create(new WorkflowOptions()),
            TimeProvider,
            NullLogger<WorkflowGrain>.Instance);
    }

    private static ReminderWorkflowGrain CreateReminderGrain(
        IServiceProvider services,
        IWorkflowRunStore store,
        string workflowRunId,
        ReminderCalls calls)
    {
        var resolver = services.GetRequiredService<WorkflowDefinitionResolver>();
        var identity = GrainTestContext.Create(
            workflowRunId,
            new WorkflowGrainTestProfileCoordinatorFactory(store, resolver));
        return new ReminderWorkflowGrain(
            identity.Context,
            identity.Runtime,
            store,
            services.GetRequiredService<IDispatchSnapshotStore>(),
            resolver,
            services.GetRequiredService<WorkflowVariableResolver>(),
            services.GetRequiredService<IWorkflowArtifactBindService>(),
            Options.Create(new WorkflowOptions()),
            TimeProvider,
            NullLogger<WorkflowGrain>.Instance,
            calls);
    }

    private async Task SeedWorkflowTemplateAsync(string projectId, WorkflowDefinition? definition = null)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        definition ??= new WorkflowDefinition([
            new StageDefinition("plan", [new("draft", "Draft", "spec/task")], []),
        ]);
        const string profileId = "spec/workflow";
        var profile = await db.WorkflowProfileRecords.FindAsync(projectId, profileId);
        if (profile is null)
        {
            db.WorkflowProfileRecords.Add(new WorkflowProfileRecordRow
            {
                ProjectId = projectId,
                ProfileId = profileId,
                Name = profileId,
                DefinitionSource = WorkflowYamlSerializer.ToYaml(definition),
                SourceProvenance = nameof(WorkflowProfileSourceProvenance.Verbatim),
            });
        }
        else
        {
            profile.DefinitionSource = WorkflowYamlSerializer.ToYaml(definition);
            profile.UpdatedAt = FixedTime;
        }

        var projectProfile = await db.ProjectWorkflowProfiles.FindAsync(projectId);
        if (projectProfile is null)
        {
            db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
            {
                ProjectId = projectId,
                DefaultWorkflowProfileId = profileId,
            });
        }
        else
        {
            projectProfile.DefaultWorkflowProfileId = profileId;
        }
        await db.SaveChangesAsync();
    }

    private sealed class FailingWorkflowRunStore : IWorkflowRunStore
    {
        private readonly IWorkflowRunStore _inner;
        private int _remainingFailures;

        public FailingWorkflowRunStore(IWorkflowRunStore inner, int remainingFailures = 1)
        {
            _inner = inner;
            _remainingFailures = remainingFailures;
        }

        public int StateOnlySaveAttempts { get; private set; }
        public int EventSaveAttempts { get; private set; }

        public Task<WorkflowRun?> LoadAsync(string workflowRunId, CancellationToken ct = default) =>
            _inner.LoadAsync(workflowRunId, ct);

        public Task SaveAsync(WorkflowRun run, CancellationToken ct = default)
        {
            StateOnlySaveAttempts++;
            if (Interlocked.CompareExchange(ref _remainingFailures, 0, 1) == 1)
                throw new InvalidOperationException("simulated state-only save failure");
            return _inner.SaveAsync(run, ct);
        }

        public Task SaveAsync(WorkflowRun run, IReadOnlyList<WorkflowEvent> events, CancellationToken ct = default)
        {
            EventSaveAttempts++;
            if (Interlocked.CompareExchange(ref _remainingFailures, 0, 1) == 1)
                throw new InvalidOperationException("simulated event save failure");
            return _inner.SaveAsync(run, events, ct);
        }

        public Task DeleteAsync(string workflowRunId, CancellationToken ct = default) =>
            _inner.DeleteAsync(workflowRunId, ct);
    }

    private sealed class ReminderCalls
    {
        public bool FailNextEnsure { get; set; }
        public bool FailNextRemove { get; set; }
        public int EnsureAttempts { get; set; }
        public int RemoveAttempts { get; set; }
    }

    private sealed class ReminderWorkflowGrain : WorkflowGrain
    {
        private readonly ReminderCalls _calls;

        public ReminderWorkflowGrain(
            Orleans.Runtime.IGrainContext context,
            Orleans.Runtime.IGrainRuntime runtime,
            IWorkflowRunStore runStore,
            IDispatchSnapshotStore dispatchSnapshotStore,
            WorkflowDefinitionResolver definitionResolver,
            WorkflowVariableResolver variableResolver,
            IWorkflowArtifactBindService artifactBindService,
            IOptions<WorkflowOptions> options,
            TimeProvider timeProvider,
            ILogger<WorkflowGrain> log,
            ReminderCalls calls)
            : base(context, runtime, runStore, dispatchSnapshotStore, definitionResolver, variableResolver, artifactBindService,
                options, timeProvider, log)
        {
            _calls = calls;
        }

        protected override Task EnsureAgentResultSettlementReminderAsync(DateTimeOffset deadline)
        {
            _calls.EnsureAttempts++;
            if (_calls.FailNextEnsure)
            {
                _calls.FailNextEnsure = false;
                throw new InvalidOperationException("simulated reminder registration failure");
            }

            return Task.CompletedTask;
        }

        protected override Task RemoveAgentResultSettlementReminderAsync()
        {
            _calls.RemoveAttempts++;
            if (_calls.FailNextRemove)
            {
                _calls.FailNextRemove = false;
                throw new InvalidOperationException("simulated reminder removal failure");
            }

            return Task.CompletedTask;
        }
    }

}
