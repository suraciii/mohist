using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Data.Db;
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
    private const string SpecVersion = "1.0";

    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IEventStore _eventStore;
    private readonly IEventPublisher _eventPublisher;

    public WorkflowRunStore(
        IDbContextFactory<MohistDbContext> dbFactory,
        IEventStore eventStore,
        IEventPublisher eventPublisher)
    {
        _dbFactory = dbFactory;
        _eventStore = eventStore;
        _eventPublisher = eventPublisher;
    }

    public async Task SaveAsync(WorkflowRun run, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await StageRunAsync(db, run, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task SaveAsync(WorkflowRun run, IReadOnlyList<WorkflowEvent> events, CancellationToken ct = default)
    {
        var source = WorkflowEventSource(run.Id);

        // 1. update workflow run state
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            try
            {
                await StageRunAsync(db, run, ct);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                throw;
            }
        }

        // 2. insert + 3. publish: convert each event to a CloudEvent, persist via IEventStore, then publish
        foreach (var evt in events)
        {
            if (evt is null) continue;
            var envelope = ToCloudEvent(evt, source);
            await _eventStore.AppendAsync(envelope, ct);
            try
            {
                await _eventPublisher.PublishAsync(envelope, ct);
            }
            catch
            {
                // publish failure must not break workflow execution
            }
        }
    }

    private static CloudEvent ToCloudEvent(WorkflowEvent evt, string source)
    {
        var type = WorkflowEventSerializer.BusType(evt);
        var data = WorkflowEventSerializer.ToData(evt);
        return new CloudEvent(
            id: Guid.NewGuid().ToString(),
            source: new Uri(source, UriKind.Relative),
            type: type,
            time: DateTimeOffset.UtcNow,
            data: data,
            subject: null,
            specVersion: SpecVersion);
    }

    public async Task<WorkflowRun?> LoadAsync(string workflowRunId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.WorkflowRuns.FindAsync([workflowRunId], ct);
        if (entity is null) return null;
        return Deserialize(entity.State);
    }

    private static async Task StageRunAsync(MohistDbContext db, WorkflowRun run, CancellationToken ct)
    {
        var json = JSON.Serialize(run);
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

    private static WorkflowRun? Deserialize(string json)
    {
        try
        {
            return JSON.Deserialize<WorkflowRun>(MigrateAssignmentJson(json));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to deserialize workflow run state. The persisted JSON is corrupt.", ex);
        }
    }

    public static string MigrateAssignmentJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || root.TryGetProperty("assignment", out _)
            || !root.TryGetProperty("claim", out var oldAssignment)
            || oldAssignment.ValueKind != JsonValueKind.Object)
            return json;

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, "claim", StringComparison.Ordinal))
                {
                    writer.WritePropertyName("assignment");
                    writer.WriteStartObject();
                    foreach (var oldProperty in oldAssignment.EnumerateObject())
                    {
                        if (string.Equals(oldProperty.Name, "claimedAt", StringComparison.Ordinal))
                            writer.WritePropertyName("assignedAt");
                        else
                            writer.WritePropertyName(oldProperty.Name);
                        oldProperty.Value.WriteTo(writer);
                    }
                    writer.WriteEndObject();
                    continue;
                }

                property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string WorkflowEventSource(string workflowRunId) =>
        $"/mohist/workflow-runs/{workflowRunId}";
}
