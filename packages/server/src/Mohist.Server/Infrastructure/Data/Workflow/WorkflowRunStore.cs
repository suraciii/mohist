using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Infrastructure.Data.Workflow;

public interface IWorkflowRunStore
{
    Task SaveAsync(WorkflowRun run, CancellationToken ct = default);
    Task SaveAsync(WorkflowRun run, IReadOnlyList<WorkflowEvent> events, CancellationToken ct = default);
    Task SaveAllAsync(WorkflowRun run, IReadOnlyList<WorkflowEvent> events, WorkLease? lease, WorkflowExecutionContext? variables, CancellationToken ct = default);
    Task<WorkflowRun?> LoadAsync(string workflowRunId, CancellationToken ct = default);
}

public class WorkflowRunStore : IWorkflowRunStore
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IEventPublisher _eventBus;
    private readonly IEventStore _eventStore;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public WorkflowRunStore(
        IDbContextFactory<MohistDbContext> dbFactory,
        IEventPublisher eventBus,
        IEventStore eventStore)
    {
        _dbFactory = dbFactory;
        _eventBus = eventBus;
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
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await StageRunAsync(db, run, ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            await PublishAsync(run, events, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw;
        }
    }

    public async Task SaveAllAsync(
        WorkflowRun run,
        IReadOnlyList<WorkflowEvent> events,
        WorkLease? lease,
        WorkflowExecutionContext? variables,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await StageRunAsync(db, run, ct);
            await StageLeaseAsync(db, run.Id, lease, ct);
            await StageVariablesAsync(db, run.Id, variables, ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            await PublishAsync(run, events, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw;
        }
    }

    private async Task PublishAsync(WorkflowRun run, IReadOnlyList<WorkflowEvent> events, CancellationToken ct)
    {
        var projectId = ExtractProjectId(run) ?? string.Empty;
        var issueNumber = ExtractIssueNumber(run) ?? string.Empty;
        var source = WorkflowRunEventPersistence.SourcePrefix + run.Id;
        var subject = string.IsNullOrEmpty(issueNumber) ? null : issueNumber;
        var extensions = new Dictionary<string, string>
        {
            ["projectid"] = projectId,
            ["workflowrunid"] = run.Id,
            ["issueno"] = issueNumber,
        };

        foreach (var (evt, type) in EnumerateWithTypes(events))
        {
            var dataJson = JsonSerializer.SerializeToElement(evt, JsonOptions);
            var envelope = new CloudEvent(
                id: Guid.NewGuid().ToString(),
                source: new Uri(source, UriKind.Relative),
                type: type,
                time: DateTimeOffset.UtcNow,
                data: dataJson,
                subject: subject,
                extensions: extensions);

            await _eventStore.AppendAsync(envelope, ct);
            await _eventBus.PublishAsync(evt, type, source, subject, extensions, ct);
        }
    }

    private static IEnumerable<(WorkflowEvent Evt, string Type)> EnumerateWithTypes(IReadOnlyList<WorkflowEvent> events)
    {
        foreach (var evt in events)
        {
            var type = evt switch
            {
                WorkflowRunStarted => "com.mohist.workflow.run.started",
                WorkflowRunResumed => "com.mohist.workflow.run.resumed",
                WorkflowRunPaused => "com.mohist.workflow.run.paused",
                WorkflowRunStopped => "com.mohist.workflow.run.stopped",
                WorkflowRunCompleted => "com.mohist.workflow.run.completed",
                WorkflowRunFailed => "com.mohist.workflow.run.failed",
                StageStarted => "com.mohist.workflow.stage.started",
                StageCompleted => "com.mohist.workflow.stage.completed",
                StageFailed => "com.mohist.workflow.stage.failed",
                StageApprovalRequested => "com.mohist.workflow.stage.approval-requested",
                StageApprovalResolved => "com.mohist.workflow.stage.approval-resolved",
                TaskCompleted => "com.mohist.workflow.task.completed",
                TaskFailed => "com.mohist.workflow.task.failed",
                CheckPassed => "com.mohist.workflow.check.passed",
                CheckFailed => "com.mohist.workflow.check.failed",
                CheckPending => "com.mohist.workflow.check.pending",
                RepairScheduled => "com.mohist.workflow.repair-scheduled",
                _ => throw new InvalidOperationException($"Unknown workflow event: {evt.GetType().Name}"),
            };
            yield return (evt, type);
        }
    }

    private static async Task StageLeaseAsync(MohistDbContext db, string runId, WorkLease? lease, CancellationToken ct)
    {
        if (lease is null) return;
        var row = await db.WorkflowLeases.FindAsync([runId], ct);
        var json = WorkflowLeaseJson.Serialize(lease);
        if (row is null)
        {
            db.WorkflowLeases.Add(new WorkflowLeaseRow { WorkflowRunId = runId, State = json });
        }
        else
        {
            row.State = json;
        }
    }

    private static async Task StageVariablesAsync(MohistDbContext db, string runId, WorkflowExecutionContext? variables, CancellationToken ct)
    {
        if (variables is null) return;
        var row = await db.WorkflowVariables.FindAsync([runId], ct);
        var json = WorkflowVariablesStore.Serialize(variables);
        if (row is null)
        {
            db.WorkflowVariables.Add(new WorkflowVariablesRow { WorkflowRunId = runId, State = json });
        }
        else
        {
            row.State = json;
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
            throw new InvalidOperationException(
                $"Failed to deserialize workflow run state. The persisted JSON is corrupt.", ex);
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
