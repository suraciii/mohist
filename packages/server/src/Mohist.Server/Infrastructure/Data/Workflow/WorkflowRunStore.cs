using System.Text.Json;
using System.Text.Json.Serialization;
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
}

public class WorkflowRunStore : IWorkflowRunStore
{
    private const string SpecVersion = "1.0";

    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IEventStore _eventStore;
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<WorkflowRunStore> _log;

    public WorkflowRunStore(
        IDbContextFactory<MohistDbContext> dbFactory,
        IEventStore eventStore,
        IGrainFactory grainFactory,
        ILogger<WorkflowRunStore> log)
    {
        _dbFactory = dbFactory;
        _eventStore = eventStore;
        _grainFactory = grainFactory;
        _log = log;
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

        PokeDispatcherBestEffort();
    }

    private void PokeDispatcherBestEffort() =>
        EventDispatcherPoke.PokeAfterCommit(_grainFactory, _log, nameof(WorkflowRunStore));

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
                $"Failed to deserialize workflow run state: {ex.Message}", ex);
        }
    }

    public static string MigrateLegacyWorkflowRunJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return json;

        var legacyRecovery = BuildLegacyRecoveryPlan(root);
        var changed = root.TryGetProperty("claim", out _)
            || (root.TryGetProperty("assignment", out var assignment) && assignment.ValueKind == JsonValueKind.Object && assignment.TryGetProperty("runnerId", out _))
            || ContainsLegacyTaskRunnerId(root)
            || legacyRecovery.Count > 0;
        if (!changed)
            return json;

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteRunObject(root, writer, legacyRecovery);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private sealed record LegacyRecoveryTask(
        string DefinitionId,
        int StageIndex,
        int TaskIndex,
        int Attempt,
        JsonElement? Recovery);

    private sealed record LegacyRecoveryNormalization(JsonElement Recovery, int Remaining);

    private static Dictionary<(int StageIndex, int TaskIndex), LegacyRecoveryNormalization> BuildLegacyRecoveryPlan(JsonElement root)
    {
        var groups = new Dictionary<(int StageIndex, string DefinitionId), List<LegacyRecoveryTask>>();
        if (!TryGetProperty(root, "stages", out var stages) || stages.ValueKind != JsonValueKind.Array)
            return [];

        var stageIndex = 0;
        foreach (var stage in stages.EnumerateArray())
        {
            if (stage.ValueKind != JsonValueKind.Object
                || !TryGetProperty(stage, "tasks", out var tasks)
                || tasks.ValueKind != JsonValueKind.Array)
            {
                stageIndex++;
                continue;
            }

            var taskIndex = 0;
            foreach (var task in tasks.EnumerateArray())
            {
                if (task.ValueKind == JsonValueKind.Object
                    && !TryGetProperty(task, "recoveryRemaining", out _)
                    && TryGetProperty(task, "definitionId", out var definitionId)
                    && definitionId.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(definitionId.GetString()))
                {
                    var recovery = TryGetProperty(task, "recovery", out var recoveryValue)
                        && recoveryValue.ValueKind == JsonValueKind.Object
                            ? recoveryValue.Clone()
                            : (JsonElement?)null;
                    var entry = new LegacyRecoveryTask(
                        definitionId.GetString()!,
                        stageIndex,
                        taskIndex,
                        ReadAttempt(task),
                        recovery);
                    var key = (entry.StageIndex, entry.DefinitionId);
                    if (!groups.TryGetValue(key, out var group))
                    {
                        group = [];
                        groups.Add(key, group);
                    }
                    group.Add(entry);
                }

                taskIndex++;
            }

            stageIndex++;
        }

        var plan = new Dictionary<(int StageIndex, int TaskIndex), LegacyRecoveryNormalization>();
        foreach (var group in groups)
        {
            if (group.Value.All(t => t.Recovery is null))
                continue;
            if (group.Value.Any(t => t.Recovery is null))
            {
                throw new InvalidOperationException(
                    $"Cannot normalize legacy recovery state for definition id '{group.Key.DefinitionId}' in stage index {group.Key.StageIndex}: some attempts have no recovery declaration");
            }

            var canonical = group.Value
                .OrderBy(t => t.Attempt)
                .ThenBy(t => t.StageIndex)
                .ThenBy(t => t.TaskIndex)
                .First();

            if (group.Value.Any(t => !RecoveryDeclarationsMatch(canonical.Recovery!.Value, t.Recovery!.Value)))
            {
                throw new InvalidOperationException(
                    $"Cannot normalize legacy recovery state for definition id '{group.Key.DefinitionId}' in stage index {group.Key.StageIndex}: recovery handlers or task declarations differ");
            }

            var declaredBudget = Math.Max(0, ReadRecoveryBudget(canonical.Recovery!.Value));
            foreach (var task in group.Value)
            {
                var remaining = Math.Clamp(ReadRecoveryBudget(task.Recovery!.Value), 0, declaredBudget);
                plan[(task.StageIndex, task.TaskIndex)] = new(canonical.Recovery.Value, remaining);
            }
        }

        return plan;
    }

    private static void WriteRunObject(
        JsonElement root,
        Utf8JsonWriter writer,
        IReadOnlyDictionary<(int StageIndex, int TaskIndex), LegacyRecoveryNormalization> legacyRecovery)
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
                WriteStagesArray(property.Value, writer, legacyRecovery);
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

    private static void WriteStagesArray(
        JsonElement stages,
        Utf8JsonWriter writer,
        IReadOnlyDictionary<(int StageIndex, int TaskIndex), LegacyRecoveryNormalization> legacyRecovery)
    {
        writer.WriteStartArray();
        var stageIndex = 0;
        foreach (var stage in stages.EnumerateArray())
        {
            if (stage.ValueKind != JsonValueKind.Object)
            {
                stage.WriteTo(writer);
                stageIndex++;
                continue;
            }

            writer.WriteStartObject();
            foreach (var property in stage.EnumerateObject())
            {
                if (string.Equals(property.Name, "tasks", StringComparison.Ordinal)
                    && property.Value.ValueKind == JsonValueKind.Array)
                {
                    writer.WritePropertyName(property.Name);
                    WriteTasksArray(property.Value, writer, stageIndex, legacyRecovery);
                    continue;
                }

                property.WriteTo(writer);
            }
            writer.WriteEndObject();
            stageIndex++;
        }
        writer.WriteEndArray();
    }

    private static void WriteTasksArray(
        JsonElement tasks,
        Utf8JsonWriter writer,
        int stageIndex,
        IReadOnlyDictionary<(int StageIndex, int TaskIndex), LegacyRecoveryNormalization> legacyRecovery)
    {
        writer.WriteStartArray();
        var taskIndex = 0;
        foreach (var task in tasks.EnumerateArray())
        {
            if (task.ValueKind != JsonValueKind.Object)
            {
                task.WriteTo(writer);
                taskIndex++;
                continue;
            }

            WriteTaskObject(task, writer, stageIndex, taskIndex, legacyRecovery);
            taskIndex++;
        }
        writer.WriteEndArray();
    }

    private static void WriteTaskObject(
        JsonElement task,
        Utf8JsonWriter writer,
        int stageIndex,
        int taskIndex,
        IReadOnlyDictionary<(int StageIndex, int TaskIndex), LegacyRecoveryNormalization> legacyRecovery)
    {
        writer.WriteStartObject();
        var hasWorkerId = TryGetProperty(task, "workerId", out _);
        var normalized = legacyRecovery.TryGetValue((stageIndex, taskIndex), out var recovery);
        var wroteRecovery = false;
        foreach (var property in task.EnumerateObject())
        {
            if (normalized && string.Equals(property.Name, "recoveryRemaining", StringComparison.OrdinalIgnoreCase))
                continue;

            if (normalized && string.Equals(property.Name, "recovery", StringComparison.OrdinalIgnoreCase))
            {
                writer.WritePropertyName(property.Name);
                recovery!.Recovery.WriteTo(writer);
                wroteRecovery = true;
                continue;
            }

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

        if (normalized)
        {
            if (!wroteRecovery)
            {
                writer.WritePropertyName("recovery");
                recovery!.Recovery.WriteTo(writer);
            }
            writer.WriteNumber("recoveryRemaining", recovery!.Remaining);
        }

        writer.WriteEndObject();
    }

    private enum JsonComparisonContext
    {
        Ordinary,
        RecoveryDeclarationRoot,
        RecoveryHandlers,
        RecoveryHandler,
        TaskDefinitions,
        TaskDefinition,
    }

    // Only the root recovery object of the legacy task attempt being normalized
    // ignores `budget` (that budget encodes the consumed allowance of this
    // attempt's round). Nested handler-task recovery declarations are definition
    // data and must match verbatim, so their comparison uses Ordinary context.
    private static bool RecoveryDeclarationsMatch(JsonElement left, JsonElement right) =>
        JsonValuesEqual(left, right, JsonComparisonContext.RecoveryDeclarationRoot);

    private static bool JsonValuesEqual(JsonElement left, JsonElement right, JsonComparisonContext context)
    {
        if (left.ValueKind != right.ValueKind)
            return false;

        return left.ValueKind switch
        {
            JsonValueKind.Object => ObjectsEqual(left, right, context),
            JsonValueKind.Array => left.EnumerateArray().Zip(right.EnumerateArray()).All(pair =>
                    JsonValuesEqual(pair.First, pair.Second, ArrayElementContext(context)))
                && left.GetArrayLength() == right.GetArrayLength(),
            JsonValueKind.String => left.GetString() == right.GetString(),
            JsonValueKind.Number => left.TryGetDecimal(out var leftNumber)
                && right.TryGetDecimal(out var rightNumber)
                    ? leftNumber == rightNumber
                    : left.GetRawText() == right.GetRawText(),
            JsonValueKind.True or JsonValueKind.False => left.GetBoolean() == right.GetBoolean(),
            JsonValueKind.Null => true,
            _ => left.GetRawText() == right.GetRawText(),
        };
    }

    private static bool ObjectsEqual(JsonElement left, JsonElement right, JsonComparisonContext context)
    {
        var leftProperties = left.EnumerateObject()
            .Where(p => context != JsonComparisonContext.RecoveryDeclarationRoot
                || !string.Equals(p.Name, "budget", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
        var rightProperties = right.EnumerateObject()
            .Where(p => context != JsonComparisonContext.RecoveryDeclarationRoot
                || !string.Equals(p.Name, "budget", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
        if (leftProperties.Count != rightProperties.Count)
            return false;

        foreach (var property in leftProperties)
        {
            if (!rightProperties.TryGetValue(property.Key, out var rightValue)
                || !JsonValuesEqual(property.Value, rightValue, PropertyContext(context, property.Key)))
                return false;
        }

        return true;
    }

    private static JsonComparisonContext ArrayElementContext(JsonComparisonContext context) => context switch
    {
        JsonComparisonContext.RecoveryHandlers => JsonComparisonContext.RecoveryHandler,
        JsonComparisonContext.TaskDefinitions => JsonComparisonContext.TaskDefinition,
        _ => JsonComparisonContext.Ordinary,
    };

    private static JsonComparisonContext PropertyContext(JsonComparisonContext context, string propertyName)
    {
        if (context == JsonComparisonContext.RecoveryDeclarationRoot
            && string.Equals(propertyName, "handlers", StringComparison.OrdinalIgnoreCase))
            return JsonComparisonContext.RecoveryHandlers;
        if (context == JsonComparisonContext.RecoveryHandler
            && string.Equals(propertyName, "tasks", StringComparison.OrdinalIgnoreCase))
            return JsonComparisonContext.TaskDefinitions;
        // Nested handler-task recovery declarations compare as Ordinary (verbatim,
        // including their own budget) — only the root recovery of the attempt
        // being normalized ignores budget.

        return JsonComparisonContext.Ordinary;
    }

    private static int ReadAttempt(JsonElement task) =>
        TryGetProperty(task, "attempt", out var attempt)
        && attempt.ValueKind == JsonValueKind.Number
        && attempt.TryGetInt32(out var value)
            ? value
            : int.MaxValue;

    private static int ReadRecoveryBudget(JsonElement recovery)
    {
        if (!TryGetProperty(recovery, "budget", out var budget)
            || budget.ValueKind != JsonValueKind.Number
            || !budget.TryGetInt32(out var value))
            throw new InvalidOperationException("Cannot normalize legacy recovery state: recovery budget is not an integer");
        return value;
    }

    private static bool TryGetProperty(JsonElement value, string name, out JsonElement property)
    {
        if (value.TryGetProperty(name, out property))
            return true;

        foreach (var candidate in value.EnumerateObject())
        {
            if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                property = candidate.Value;
                return true;
            }
        }

        property = default;
        return false;
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
