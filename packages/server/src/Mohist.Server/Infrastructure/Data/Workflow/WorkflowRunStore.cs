using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Workflow.Domain.Run;
using Orleans;

namespace Mohist.Server.Infrastructure.Data.Workflow;

public interface IWorkflowRunStore
{
    Task SaveAsync(WorkflowRun run, CancellationToken ct = default);
    Task SaveAsync(WorkflowRun run, IReadOnlyList<WorkflowEvent> events, CancellationToken ct = default);
    Task<WorkflowRun?> LoadAsync(string workflowRunId, CancellationToken ct = default);
    Task DeleteAsync(string workflowRunId, CancellationToken ct = default);
}

public class WorkflowRunStore : IWorkflowRunStore
{
    private const string SpecVersion = "1.0";

    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IEventStore _eventStore;
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<WorkflowRunStore> _log;
    private readonly IBackgroundTaskLauncher _backgroundTasks;

    public WorkflowRunStore(
        IDbContextFactory<MohistDbContext> dbFactory,
        IEventStore eventStore,
        IGrainFactory grainFactory,
        ILogger<WorkflowRunStore> log,
        IBackgroundTaskLauncher backgroundTasks)
    {
        _dbFactory = dbFactory;
        _eventStore = eventStore;
        _grainFactory = grainFactory;
        _log = log;
        _backgroundTasks = backgroundTasks;
    }

    public async Task SaveAsync(WorkflowRun run, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var epicNumber = WorkflowRunLineage.EpicAffiliationOf(run);
        await StageRunAsync(db, run, epicNumber, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task SaveAsync(WorkflowRun run, IReadOnlyList<WorkflowEvent> events, CancellationToken ct = default) =>
        await SaveEventsAsync(run, events, ct);

    private async Task SaveEventsAsync(
        WorkflowRun run,
        IReadOnlyList<WorkflowEvent> events,
        CancellationToken ct)
    {
        var source = WorkflowEventSource(run.Id);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var epicNumber = WorkflowRunLineage.EpicAffiliationOf(run);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await StageRunAsync(db, run, epicNumber, ct);
            foreach (var evt in events)
            {
                if (evt is null) continue;
                var envelope = ToCloudEvent(evt, source, run);
                await _eventStore.AppendAsync(db, envelope, ct);
            }
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw;
        }

        PokeDispatcherBestEffort();
    }

    private void PokeDispatcherBestEffort() =>
        EventDispatcherPoke.PokeAfterCommit(_grainFactory, _log, nameof(WorkflowRunStore), _backgroundTasks);

    /// <summary>
    /// Build the CloudEvent envelope for a workflow domain event, stamping its
    /// full business lineage onto <c>extensions</c> from the producing run's own
    /// typed metadata (no cross-aggregate query). <c>workflowrunid</c> is
    /// stamped unconditionally because the run itself is the producer. <c>stage</c>
    /// is stamped when the unwrapped <see cref="WorkflowEvent"/> variant exposes
    /// a <c>Stage</c> member — see <c>WorkflowRunLineage.StageOf</c>.
    /// </summary>
    private static CloudEvent ToCloudEvent(WorkflowEvent evt, string source, WorkflowRun run)
    {
        var type = WorkflowEventSerializer.BusType(evt);
        var data = WorkflowEventSerializer.ToData(evt);
        var extensions = WorkflowRunLineage.BuildExtensions(run, evt);
        return new CloudEvent(
            id: Guid.NewGuid().ToString(),
            source: new Uri(source, UriKind.Relative),
            type: type,
            time: DateTimeOffset.UtcNow,
            data: data,
            subject: null,
            specVersion: SpecVersion,
            extensions: extensions);
    }

    public async Task<WorkflowRun?> LoadAsync(string workflowRunId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.WorkflowRuns.FindAsync([workflowRunId], ct);
        if (entity is null) return null;
        var run = Deserialize(entity.State);
        if (run is not null)
            WorkflowRunLineage.RestoreStoredEpicNumber(run, entity.EpicNumber);
        return run;
    }

    public async Task DeleteAsync(string workflowRunId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.WorkflowRuns.FindAsync([workflowRunId], ct);
        if (row is null) return;
        db.WorkflowRuns.Remove(row);
        await db.SaveChangesAsync(ct);
    }

    private static async Task StageRunAsync(
        MohistDbContext db,
        WorkflowRun run,
        int? epicNumber,
        CancellationToken ct)
    {
        var entity = await db.WorkflowRuns.FindAsync([run.Id], ct);

        if (entity is null)
        {
            var newEntity = new WorkflowRunRow
            {
                WorkflowRunId = run.Id,
                State = JSON.Serialize(run),
                EpicNumber = epicNumber,
                WorkflowProfileIdKey = run.Status.IsTerminal()
                    ? null
                    : WorkflowProfileBindingKey.For(run.WorkflowProfileId),
            };
            db.WorkflowRuns.Add(newEntity);
            db.Entry(newEntity).Property<long>("ETag").CurrentValue = 1;
            return;
        }

        entity.EpicNumber = epicNumber;
        entity.State = JSON.Serialize(run);
        entity.WorkflowProfileIdKey = run.Status.IsTerminal()
            ? null
            : WorkflowProfileBindingKey.For(run.WorkflowProfileId);
        var entry = db.Entry(entity);
        entry.Property<long>("ETag").CurrentValue = entry.Property<long>("ETag").OriginalValue + 1;
    }

    private static WorkflowRun? Deserialize(string json)
    {
        try
        {
            return JSON.Deserialize<WorkflowRun>(json);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to deserialize workflow run state: {ex.Message}", ex);
        }
    }

    private static string WorkflowEventSource(string workflowRunId) =>
        $"/mohist/workflow-runs/{workflowRunId}";
}
