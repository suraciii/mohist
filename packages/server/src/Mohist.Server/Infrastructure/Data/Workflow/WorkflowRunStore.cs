using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Infrastructure.Data.Workflow;

public interface IWorkflowRunStore
{
    Task SaveAsync(WorkflowRun run, CancellationToken ct = default);
    Task SaveAsync(WorkflowRun run, IReadOnlyList<WorkflowEvent> events, CancellationToken ct = default);
    Task<WorkflowRun?> LoadAsync(string workflowRunId, CancellationToken ct = default);
}

public class WorkflowRunStore : IWorkflowRunStore
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IEventBus _eventBus;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public WorkflowRunStore(IDbContextFactory<MohistDbContext> dbFactory, IEventBus eventBus)
    {
        _dbFactory = dbFactory;
        _eventBus = eventBus;
    }

    public async Task SaveAsync(WorkflowRun run, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await StageRunAsync(db, run, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task SaveAsync(WorkflowRun run, IReadOnlyList<WorkflowEvent> events, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await StageRunAsync(db, run, ct);
            var stagedEvents = await WorkflowEventPersistence.StageAsync(db, run.Id, events, ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            var projectId = ExtractProjectId(run);
            var issueNumber = ExtractIssueNumber(run);
            Publish(stagedEvents, projectId, issueNumber);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    private static async Task StageRunAsync(MohistDbContext db, WorkflowRun run, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(run, JsonOptions);
        var entity = await db.WorkflowRuns.FindAsync([run.Id], ct);

        if (entity is null)
        {
            var newEntity = new WorkflowRunRow { WorkflowRunId = run.Id, State = json };
            db.WorkflowRuns.Add(newEntity);
            db.Entry(newEntity).Property<long>("ETag").CurrentValue = 1;
            return;
        }

        entity.State = json;
        var entry = db.Entry(entity);
        entry.Property<long>("ETag").CurrentValue = entry.Property<long>("ETag").OriginalValue + 1;
    }

    public async Task<WorkflowRun?> LoadAsync(string workflowRunId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.WorkflowRuns.FindAsync([workflowRunId], ct);
        if (entity is null) return null;
        return Deserialize(entity.State);
    }

    private static WorkflowRun? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<WorkflowRun>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            // JSON corruption is unrecoverable; surface to caller rather than silently returning null.
            throw new InvalidOperationException(
                $"Failed to deserialize workflow run state. The persisted JSON is corrupt.", ex);
        }
    }

    private void Publish(IReadOnlyList<StagedWorkflowEvent> stagedEvents, string? projectId, string? issueNumber)
    {
        foreach (var e in stagedEvents)
        {
            var (runId, _) = WorkflowEventSerializer.ExtractContext(e);
            var busType = WorkflowEventSerializer.BusType(e.Payload);
            var evt = CloudEventFactory.Create(
                type: busType,
                source: new Uri($"/mohist/workflow/{runId}", UriKind.Relative),
                data: WorkflowEventSerializer.ToData(e.Payload),
                projectId: projectId,
                workflowRunId: runId,
                issueNumber: issueNumber);
            _eventBus.Emit(evt);
        }
    }

    private static string? ExtractProjectId(WorkflowRun run)
    {
        if (run.Metadata.Annotations is not null
            && run.Metadata.Annotations.TryGetValue("projectId", out var pid)
            && !string.IsNullOrWhiteSpace(pid))
        {
            return pid;
        }
        return null;
    }

    private static string? ExtractIssueNumber(WorkflowRun run)
    {
        if (run.Metadata.Annotations is not null
            && run.Metadata.Annotations.TryGetValue("issueNumber", out var n)
            && !string.IsNullOrWhiteSpace(n))
        {
            return n;
        }
        return null;
    }
}
