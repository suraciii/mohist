using EnvironmentAbstractions.TestHelpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Artifacts;
using Mohist.Server.Workflow.Services.Prompts;
using Mohist.Server.Workflow.Storage;
using Orleans;
using Orleans.TestingHost;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow;

public class WorkflowGrainFixture : IAsyncLifetime
{
    public InProcessTestCluster Cluster { get; private set; } = null!;
    public IGrainFactory Grains => Cluster.Client;
    public RecordingEventStore EventStore => _sharedEventStore;
    public string ConnectionString => _keeper.ConnectionString;
    public FakeTimeProvider TimeProvider { get; } = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    public AgentSessionPersistenceTestProbe Persistence { get; }
    public ControllableDispatchPollObserver DispatchPollObserver { get; } = new();

    private readonly RecordingEventStore _sharedEventStore = new();
    private readonly InMemoryEventBus _sharedEventBus;
    private SqliteConnection _keeper = null!;

    public WorkflowGrainFixture()
    {
        Persistence = new AgentSessionPersistenceTestProbe(
            () => TimeProvider.Advance(TimeSpan.FromSeconds(1)));
        _sharedEventBus = new InMemoryEventBus(
            _sharedEventStore,
            TimeProvider,
            NullLogger<InMemoryEventBus>.Instance);
    }

    public async ValueTask InitializeAsync()
    {
        var dbName = $"mohist-test-{Guid.NewGuid():N}";
        var connectionString = $"Data Source={dbName};Mode=Memory;Cache=Shared";
        _keeper = new SqliteConnection(connectionString);
        _keeper.Open();

        MigratedSqliteTemplate.CopyTo(_keeper);

        var builder = new InProcessTestClusterBuilder();
        builder.Options.InitialSilosCount = 1;
        builder.ConfigureSilo((_, siloBuilder) =>
        {
            GrainTestConfig.ConfigureSilo(
                siloBuilder,
                connectionString,
                _sharedEventBus,
                _sharedEventStore,
                TimeProvider,
                Persistence);
            siloBuilder.Services.AddSingleton<IDispatchPollObserver>(DispatchPollObserver);
            // IssueGrain + ProjectGrain dependencies: the workflow-only
            // tests above never activate issue/project grains, but the
            // #290 batch-C grain migration (items 16/17/18) drives
            // IssueGrain.AddCommentAsync, IssueGrain.CreateAsync and the
            // composite-aggregation grain surface from this fixture. The
            // production service graph registers these via
            // ConfigureMohistServices; mirror the issue/project subset
            // here so the silo can activate the grain without dragging
            // in the full WebApplicationFactory. The Scrutor
            // IScopedService/ISingletonService markers are intentionally
            // not invoked — they would also re-register the workflow
            // singletons already set up above and risk double
            // registration. Only the explicitly-non-conventional
            // dependencies are listed.
            siloBuilder.Services.AddScoped<Mohist.Server.Infrastructure.Data.Issue.IIssueStore,
                Mohist.Server.Infrastructure.Data.Issue.IssueStore>();
            siloBuilder.Services.AddSingleton<Mohist.Server.Issue.Services.IssueRepositoryResolver>();
            siloBuilder.Services.AddScoped<Mohist.Server.Issue.Services.Attachments.AttachmentService>();
            // AttachmentService needs an IAttachmentStorage; the
            // production file-system implementation would touch the real
            // filesystem, so swap in the in-memory test one plus the
            // options it expects. AttachmentService's attachmentIds path
            // is exercised with null/empty arrays in the batch-C specs
            // (AddCommentAsync with no attachments), so the in-memory
            // fake never needs to actually persist a file.
            siloBuilder.Services.AddSingleton<Mohist.Server.TestSupport.InMemoryAttachmentStorage>();
            siloBuilder.Services.AddSingleton<IAttachmentStorage>(sp =>
                sp.GetRequiredService<Mohist.Server.TestSupport.InMemoryAttachmentStorage>());
            siloBuilder.Services.Configure<AttachmentStorageOptions>(opts =>
            {
                opts.Root = "/mohist-tests/workflow-attachments";
            });
            // WorkflowQuerier is an IScopedService that IssueGrain takes
            // directly (different from the WorkflowRunQuerier already
            // registered for the workflow grain tests). Register it
            // explicitly along with the WorkflowVariableResolver +
            // WorkflowArtifactQuerier + WorkflowRunStatusCache +
            // IWorkflowRunDeserializer it needs; the rest of its
            // dependencies (DbContextFactory, WorkflowDefinitionResolver)
            // are already registered above.
            siloBuilder.Services.AddScoped<Mohist.Server.Workflow.Services.WorkflowQuerier>();
            siloBuilder.Services.AddScoped<Mohist.Server.Workflow.Services.Artifacts.WorkflowArtifactQuerier>();
            siloBuilder.Services.AddSingleton<IWorkflowArtifactQuerier>(sp =>
                sp.GetRequiredService<Mohist.Server.Workflow.Services.Artifacts.WorkflowArtifactQuerier>());
            siloBuilder.Services.AddSingleton<WorkflowRunStatusCache>();
            siloBuilder.Services.AddSingleton<Mohist.Server.Workflow.Services.WorkflowRunDeserializer>();
            siloBuilder.Services.AddSingleton<IWorkflowRunDeserializer>(sp =>
                sp.GetRequiredService<Mohist.Server.Workflow.Services.WorkflowRunDeserializer>());
            // Batch C #290 item 17: IssueGrain.StartWorkAsync calls
            // EnsurePromptsReferencesResolve against the workflow
            // definition. The FakePromptLoader GrainTestConfig wires by
            // default only registers a 7-prompt subset; the mohist/local
            // profile references 12 prompts (apply-feedback,
            // resolve-rebase-conflicts, fix-plan-review, fix-ci,
            // auto-fix, …) so any IssueGrain.StartWorkAsync call from
            // this fixture would throw MissingPromptsException. Swap in
            // the production-shaped InMemoryPromptLoader so all prompt
            // references resolve. The fake's existing users
            // (WorkflowRetrySpecs, StatusSpecs, etc.) only read
            // prompts.Load / LoadAll and never assert the count, so the
            // extra keys are inert for them.
            siloBuilder.Services.RemoveAll<IPromptLoader>();
            siloBuilder.Services.AddSingleton<IPromptLoader>(_ => new InMemoryPromptLoader());
            // Issue-318 / batch C #290 item 17: WorkspaceQuerier
            // (which WorkflowQuerier.StartWorkflowAsync calls to mint
            // the run workspace) depends on AgentSessionQuerier, which
            // the workflow-only test registration in GrainTestConfig
            // never wires up. Register it explicitly so IssueGrain
            // .StartWorkAsync can mint the workspace grain.
            siloBuilder.Services.AddScoped<Mohist.Server.Sessions.Services.AgentSessionQuerier>();
            // Batch C #290 item 17: IssueQuerier is what the lifecycle
            // grain-level specs (CompleteWorkAsync, CancelAsync, …) use
            // to assert IssueInfo status transitions through the
            // production projection path. GrainTestConfig does not
            // register it (its workflow-only suite never asks for one),
            // so register it here. It depends on IssueRepositoryResolver
            // (already registered above) and the DbContextFactory.
            siloBuilder.Services.AddScoped<Mohist.Server.Issue.Services.IssueQuerier>();
            // IssueQuerier depends on IssueReadModelLoader
            // (IScopedService — already covered by Scrutor) and the
            // issue metrics querier infra the lifecycle tests don't
            // touch. IssueReadModelLoader is registered automatically
            // via the conventional scan, so no explicit registration is
            // needed; the failure here is that GrainTestConfig does not
            // expose IssueQuerier in the first place. The Scrutor
            // IScopedService scan in MohistServiceRegistration handles
            // IssueReadModelLoader when the production service graph is
            // used, but GrainTestConfig bypasses that scan — register
            // the loader explicitly so the querier resolves end-to-end.
            siloBuilder.Services.AddScoped<Mohist.Server.Issue.Services.IssueReadModelLoader>();
            siloBuilder.Services.RemoveAll<IEnvironmentVariableProvider>();
            siloBuilder.Services.AddSingleton<IEnvironmentVariableProvider, MockEnvironmentVariableProvider>();
        });
        Cluster = builder.Build();
        await Cluster.DeployAsync();
    }

    public ValueTask DisposeAsync()
    {
        Cluster?.Dispose();
        _keeper?.DisposeAsync();
        return ValueTask.CompletedTask;
    }

}

public sealed class ControllableDispatchPollObserver : IDispatchPollObserver
{
    private TaskCompletionSource _runnerInfoObserved = NewSignal();
    private TaskCompletionSource? _afterRunnerInfoBlock;

    public Task AfterRunnerInfoAsync(string runnerId)
    {
        _runnerInfoObserved.TrySetResult();
        return _afterRunnerInfoBlock?.Task ?? Task.CompletedTask;
    }

    public Task WaitForRunnerInfoAsync() => _runnerInfoObserved.Task;

    public void BlockAfterRunnerInfo() => _afterRunnerInfoBlock ??= NewSignal();

    public void ReleaseAfterRunnerInfo() => _afterRunnerInfoBlock?.TrySetResult();

    public void Reset()
    {
        _afterRunnerInfoBlock?.TrySetResult();
        _afterRunnerInfoBlock = null;
        _runnerInfoObserved = NewSignal();
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
