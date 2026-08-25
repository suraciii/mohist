using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Artifacts;
using Orleans;
using Orleans.Runtime;
using Mohist.Workflow.Definition;

namespace Mohist.Server.UnitTests.Workflow.GrainContracts;

/// <summary>
/// Shared arrangement for direct-construction WorkflowGrain contract specs.
/// Mirrors the seeding the cluster fixture performed (workflow profile +
/// project default profile rows) without an Orleans silo.
/// </summary>
internal static partial class WorkflowGrainContractSupport
{
    internal static async Task SeedTemplateAsync(
        MohistDbFixture fixture,
        string projectId,
        WorkflowDefinition definition,
        DateTimeOffset fixedTime)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        const string profileId = "spec/workflow";
        var yaml = WorkflowYamlSerializer.ToYaml(definition);
        var changed = false;
        var profile = await db.WorkflowProfileRecords.FindAsync(projectId, profileId);
        if (profile is null)
        {
            db.WorkflowProfileRecords.Add(new WorkflowProfileRecordRow
            {
                ProjectId = projectId,
                ProfileId = profileId,
                Name = profileId,
                DefinitionSource = yaml,
                SourceProvenance = nameof(WorkflowProfileSourceProvenance.Verbatim),
            });
            changed = true;
        }
        else if (profile.DefinitionSource != yaml)
        {
            // A re-seed with a different template must win; an identical
            // re-seed skips the write — repeated seeding of the same class
            // template is the dominant per-test Arrange cost in these specs.
            profile.DefinitionSource = yaml;
            profile.UpdatedAt = fixedTime;
            changed = true;
        }

        if (await db.ProjectWorkflowProfiles.FindAsync(projectId) is null)
        {
            db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
            {
                ProjectId = projectId,
                DefaultWorkflowProfileId = profileId,
            });
            changed = true;
        }

        if (changed)
            await db.SaveChangesAsync();
    }

    internal static WorkflowGrain CreateGrain(
        IServiceProvider services,
        IWorkflowRunStore store,
        string workflowRunId,
        TimeProvider timeProvider,
        Func<string, IRunnerUpdateOperationGrain>? operations = null)
    {
        var resolver = services.GetRequiredService<WorkflowDefinitionResolver>();
        var identity = GrainTestContext.Create(
            workflowRunId,
            new WorkflowGrainTestProfileCoordinatorFactory(store, resolver, operations));
        return new WorkflowGrain(
            identity.Context,
            identity.Runtime,
            store,
            services.GetRequiredService<IDispatchSnapshotStore>(),
            resolver,
            services.GetRequiredService<WorkflowVariableResolver>(),
            services.GetRequiredService<IWorkflowArtifactBindService>(),
            Options.Create(new WorkflowOptions()),
            timeProvider,
            NullLogger<WorkflowGrain>.Instance,
            services.GetRequiredService<WorkflowItemTranslator>());
    }

    /// <summary>
    /// Store wrapper whose event-commit boundary fails when the batch carries
    /// a selected event type; state-only saves and other batches pass through.
    /// Reproduces the cluster fixture's ThrowOnAppend injection at the durable
    /// seam the grain actually commits through.
    /// </summary>
    internal sealed class SelectiveFailingStore : IWorkflowRunStore
    {
        private readonly IWorkflowRunStore _inner;
        private readonly Func<WorkflowEvent, bool> _failBatchWhen;

        public SelectiveFailingStore(IWorkflowRunStore inner, Func<WorkflowEvent, bool> failBatchWhen)
        {
            _inner = inner;
            _failBatchWhen = failBatchWhen;
        }

        public Task<WorkflowRun?> LoadAsync(string workflowRunId, CancellationToken ct = default) =>
            _inner.LoadAsync(workflowRunId, ct);

        public Task SaveAsync(WorkflowRun run, CancellationToken ct = default) => _inner.SaveAsync(run, ct);

        public Task SaveAsync(WorkflowRun run, IReadOnlyList<WorkflowEvent> events, CancellationToken ct = default)
        {
            if (events.Any(_failBatchWhen))
                throw new InvalidOperationException("simulated event save failure");
            return _inner.SaveAsync(run, events, ct);
        }

        public Task DeleteAsync(string workflowRunId, CancellationToken ct = default) =>
            _inner.DeleteAsync(workflowRunId, ct);
    }
}

/// <summary>
/// Handle over an activated, started run: assignment-gated claim/report
/// helpers shared by direct-construction WorkflowGrain specs.
/// </summary>
internal sealed record WorkflowGrainArrangement(
    WorkflowGrain Grain,
    IWorkflowRunStore Store,
    IDispatchSnapshotStore Snapshots,
    IEventStore Events,
    WorkflowQuerier Querier,
    string RunId,
    string WorkerId,
    RunnerUpdateOperationGrainRegistry? Operations = null)
{
    public async Task<WorkItem?> AssignAndClaimAsync()
    {
        await Grain.AssignWorkerAsync(WorkerId);
        return await Grain.ClaimNextAsync(WorkerId);
    }

    /// <summary>Reports the claimed task complete, resolving the persisted task-run id.</summary>
    public async Task<ReportAck> ReportCompletedAsync(WorkItem item) =>
        await ReportTaskAsync(item, TaskReportStatus.Succeeded);

    public async Task<ReportAck> ReportFailedAsync(WorkItem item, string detail)
    {
        var taskRunId = await BuildReportTaskRunIdAsync();
        return await Grain.ReceiveTaskReportAsync(
            WorkerId,
            item.Id!,
            new TaskReport(item.Id!, TaskReportStatus.Failed, Output: null, Artifacts: null, Detail: detail, TaskRunId: taskRunId));
    }

    private async Task<string> BuildReportTaskRunIdAsync()
    {
        var run = await Store.LoadAsync(RunId) ?? throw new InvalidOperationException("run missing");
        var runningTask = run.CurrentStage().RunningTask
            ?? throw new InvalidOperationException("no running task to report");
        return runningTask.Id;
    }

    private async Task<ReportAck> ReportTaskAsync(WorkItem item, TaskReportStatus status)
    {
        var taskRunId = await BuildReportTaskRunIdAsync();
        return await Grain.ReceiveTaskReportAsync(
            WorkerId, item.Id!, new TaskReport(item.Id!, status, Output: null, Artifacts: null, TaskRunId: taskRunId));
    }

    /// <summary>Reports against a work id no active work carries; the grain must fence it.</summary>
    public async Task<ReportAck> ReportUnknownWorkAsync(string workId)
    {
        var taskRunId = await BuildReportTaskRunIdAsync();
        return await Grain.ReceiveTaskReportAsync(
            WorkerId, workId, new TaskReport(workId, TaskReportStatus.Failed, Output: null, Artifacts: null, TaskRunId: taskRunId));
    }

    public async Task<ReportAck> ReportCheckResultsAsync(
        WorkItem check,
        params (string Name, CheckResultStatus Status, string? Message)[] results)
    {
        var payload = results
            .Select(result => new CheckResult(result.Name, result.Status, result.Message))
            .ToList();
        return await Grain.ReceiveCheckReportAsync(WorkerId, check.Id!, new CheckReport(check.Stage, payload));
    }

    public Task<ReportAck> ReportChecksPassAsync(WorkItem check, string checkName) =>
        ReportCheckResultsAsync(check, (checkName, CheckResultStatus.Passed, null));

    /// <summary>
    /// Reports the claimed task with structured output and runtime follow-up
    /// tasks (the recovery-injection path).
    /// </summary>
    public async Task<ReportAck> ReportTaskResultAsync(
        WorkItem item,
        System.Text.Json.JsonElement? output,
        IReadOnlyList<RuntimeTaskInput>? addTasks,
        TaskReportStatus status = TaskReportStatus.Succeeded)
    {
        var taskRunId = await BuildReportTaskRunIdAsync();
        return await Grain.ReceiveTaskReportAsync(
            WorkerId,
            item.Id!,
            new TaskReport(item.Id!, status, Output: output, Artifacts: null, AddTasks: addTasks, TaskRunId: taskRunId));
    }

    public static async Task<WorkflowGrainArrangement> CreateAsync(
        MohistDbFixture fixture,
        string runId,
        WorkflowDefinition definition,
        TimeProvider timeProvider,
        string workerId = "worker-1",
        string? projectId = null)
    {
        // Project identity is derived from the template content so specs
        // that share a definition shape also share one seeded profile row;
        // isolation comes from the unique run id, not the project.
        projectId ??= $"prof-{Math.Abs(WorkflowYamlSerializer.ToYaml(definition).GetHashCode()):x8}";
        await WorkflowGrainContractSupport.SeedTemplateAsync(fixture, projectId, definition, Fixed);
        var scope = fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();
        var querier = scope.ServiceProvider.GetRequiredService<WorkflowQuerier>();
        // Always available: recovery-receipt handling consults the runner
        // update operation state even when no fence exists.
        var operations = new RunnerUpdateOperationGrainRegistry(
            new ConcurrentDictionary<string, RunnerUpdateOperationState>(),
            new RunnerUpdateOperationWriteFailureProbe());
        var grain = WorkflowGrainContractSupport.CreateGrain(
            scope.ServiceProvider,
            store,
            runId,
            timeProvider,
            operations is null ? null : operations.For);
        await grain.OnActivateAsync(CancellationToken.None);
        await grain.EnsureStartedAsync(new WorkflowIssueContext(projectId, 1, null));
        return new WorkflowGrainArrangement(
            grain,
            store,
            scope.ServiceProvider.GetRequiredService<IDispatchSnapshotStore>(),
            scope.ServiceProvider.GetRequiredService<IEventStore>(),
            querier,
            runId,
            workerId,
            operations);
    }

    internal static readonly DateTimeOffset Fixed = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
}

/// <summary>
/// In-memory grain storage backing directly-constructed persistent grains.
/// </summary>
internal sealed class TestGrainStorage<TState>(string key, ConcurrentDictionary<string, TState> store) : IPersistentState<TState>
{
    public TState State { get; set; } = default!;

    public string Etag => Guid.NewGuid().ToString("N");

    public bool RecordExists => store.ContainsKey(key);

    public Task ReadStateAsync()
    {
        State = store.TryGetValue(key, out var value) ? value : default!;
        return Task.CompletedTask;
    }

    public Task WriteStateAsync()
    {
        store[key] = State;
        return Task.CompletedTask;
    }

    public Task ClearStateAsync()
    {
        store.TryRemove(key, out _);
        State = default!;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Registry of directly-constructed <see cref="RunnerUpdateOperationGrain"/>
/// instances keyed by runner id. The same instance serves both the workflow
/// grain's internal factory lookups and direct test assertions, so operation
/// state written by one side is visible to the other.
/// </summary>
internal sealed class RunnerUpdateOperationGrainRegistry(
    ConcurrentDictionary<string, RunnerUpdateOperationState> backing,
    RunnerUpdateOperationWriteFailureProbe probe)
{
    private readonly ConcurrentDictionary<string, IRunnerUpdateOperationGrain> _grains = new();

    public RunnerUpdateOperationWriteFailureProbe Probe { get; } = probe;

    private RunnerUpdateOperationWriteFailureProbe ProbeField { get; init; } = probe;

    public IRunnerUpdateOperationGrain For(string runnerId) =>
        _grains.GetOrAdd(runnerId, key =>
        {
            var grain = new RunnerUpdateOperationGrain(
                new TestGrainStorage<RunnerUpdateOperationState>($"runner-update:{key}", backing),
                ProbeField);
            WorkflowGrainContractSupport.AttachTestContext(grain, key);
            return grain;
        });
}

internal static partial class WorkflowGrainContractSupport
{
    /// <summary>
    /// Attaches a manual test grain context to a primary-constructor grain so
    /// identity lookups (GetPrimaryKeyString) resolve without Orleans runtime
    /// activation.
    /// </summary>
    internal static void AttachTestContext(global::Orleans.Grain grain, string key)
    {
        var identity = GrainTestContext.Create(key);
        var property = typeof(global::Orleans.Grain)
            .GetProperties(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
            .SingleOrDefault(property => property.PropertyType == typeof(IGrainContext) && property.CanWrite)
            ?? throw new MissingMemberException(nameof(Grain), "expected writable grain-context property");
        property.SetValue(grain, identity.Context);
    }
}
