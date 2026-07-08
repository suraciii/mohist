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

    public WorkflowRunStore(
        IDbContextFactory<MohistDbContext> dbFactory,
        IEventStore eventStore)
    {
        _dbFactory = dbFactory;
        _eventStore = eventStore;
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
        var annotations = run.Metadata?.Annotations;
        var projectId = annotations?.GetValueOrDefault("projectId");
        var issueId = annotations?.GetValueOrDefault("issueId");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await StageRunAsync(db, run, ct);
            foreach (var evt in events)
            {
                if (evt is null) continue;
                var envelope = ToCloudEvent(evt, source, projectId, issueId);
                await _eventStore.AppendAsync(db, envelope, ct);
            }
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw;
        }
    }

    private static CloudEvent ToCloudEvent(WorkflowEvent evt, string source, string? projectId, string? issueId)
    {
        var type = WorkflowEventSerializer.BusType(evt);
        var data = WorkflowEventSerializer.ToData(evt);
        Dictionary<string, string>? extensions = null;
        var hasProjectId = !string.IsNullOrWhiteSpace(projectId);
        var hasIssueId = !string.IsNullOrWhiteSpace(issueId);
        if (hasProjectId || hasIssueId)
        {
            extensions = new Dictionary<string, string>(StringComparer.Ordinal);
            if (hasProjectId) extensions["projectid"] = projectId!;
            if (hasIssueId) extensions["issueid"] = issueId!;
        }
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
            return JSON.Deserialize<WorkflowRun>(MigrateLegacyWorkflowRunJson(json));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to deserialize workflow run state. The persisted JSON is corrupt.", ex);
        }
    }

    public static string MigrateLegacyWorkflowRunJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return json;

        var changed = root.TryGetProperty("claim", out _)
            || (root.TryGetProperty("assignment", out var assignment) && assignment.ValueKind == JsonValueKind.Object && assignment.TryGetProperty("runnerId", out _))
            || ContainsLegacyTaskRunnerId(root);
        if (!changed)
            return json;

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteRunObject(root, writer);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteRunObject(JsonElement root, Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, "claim", StringComparison.Ordinal))
            {
                if (!root.TryGetProperty("assignment", out _))
                {
                    writer.WritePropertyName("assignment");
                    WriteAssignmentObject(property.Value, writer);
                }
                continue;
            }

            if (string.Equals(property.Name, "assignment", StringComparison.Ordinal)
                && property.Value.ValueKind == JsonValueKind.Object)
            {
                writer.WritePropertyName(property.Name);
                WriteAssignmentObject(property.Value, writer);
                continue;
            }

            if (string.Equals(property.Name, "stages", StringComparison.Ordinal)
                && property.Value.ValueKind == JsonValueKind.Array)
            {
                writer.WritePropertyName(property.Name);
                WriteStagesArray(property.Value, writer);
                continue;
            }

            property.WriteTo(writer);
        }
        writer.WriteEndObject();
    }

    private static void WriteAssignmentObject(JsonElement assignment, Utf8JsonWriter writer)
    {
        var hasWorkerId = assignment.TryGetProperty("workerId", out _);
        var hasAssignedAt = assignment.TryGetProperty("assignedAt", out _);
        writer.WriteStartObject();
        foreach (var property in assignment.EnumerateObject())
        {
            if (string.Equals(property.Name, "runnerId", StringComparison.Ordinal))
            {
                if (hasWorkerId) continue;
                writer.WritePropertyName("workerId");
            }
            else if (string.Equals(property.Name, "claimedAt", StringComparison.Ordinal))
            {
                if (hasAssignedAt) continue;
                writer.WritePropertyName("assignedAt");
            }
            else
            {
                writer.WritePropertyName(property.Name);
            }
            property.Value.WriteTo(writer);
        }
        writer.WriteEndObject();
    }

    private static void WriteStagesArray(JsonElement stages, Utf8JsonWriter writer)
    {
        writer.WriteStartArray();
        foreach (var stage in stages.EnumerateArray())
        {
            if (stage.ValueKind != JsonValueKind.Object)
            {
                stage.WriteTo(writer);
                continue;
            }

            writer.WriteStartObject();
            foreach (var property in stage.EnumerateObject())
            {
                if (string.Equals(property.Name, "tasks", StringComparison.Ordinal)
                    && property.Value.ValueKind == JsonValueKind.Array)
                {
                    writer.WritePropertyName(property.Name);
                    WriteTasksArray(property.Value, writer);
                    continue;
                }

                property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteTasksArray(JsonElement tasks, Utf8JsonWriter writer)
    {
        writer.WriteStartArray();
        foreach (var task in tasks.EnumerateArray())
        {
            if (task.ValueKind != JsonValueKind.Object)
            {
                task.WriteTo(writer);
                continue;
            }

            writer.WriteStartObject();
            var hasWorkerId = task.TryGetProperty("workerId", out _);
            foreach (var property in task.EnumerateObject())
            {
                if (string.Equals(property.Name, "runnerId", StringComparison.Ordinal))
                {
                    if (hasWorkerId) continue;
                    writer.WritePropertyName("workerId");
                }
                else
                {
                    writer.WritePropertyName(property.Name);
                }
                property.Value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static bool ContainsLegacyTaskRunnerId(JsonElement root)
    {
        if (!root.TryGetProperty("stages", out var stages) || stages.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var stage in stages.EnumerateArray())
        {
            if (stage.ValueKind != JsonValueKind.Object
                || !stage.TryGetProperty("tasks", out var tasks)
                || tasks.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var task in tasks.EnumerateArray())
            {
                if (task.ValueKind == JsonValueKind.Object && task.TryGetProperty("runnerId", out _))
                    return true;
            }
        }

        return false;
    }

    private static string WorkflowEventSource(string workflowRunId) =>
        $"/mohist/workflow-runs/{workflowRunId}";
}
