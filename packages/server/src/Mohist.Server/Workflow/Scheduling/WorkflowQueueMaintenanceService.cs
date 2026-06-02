using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Persistence.Db;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Workflow.Scheduling;

public sealed class WorkflowQueueMaintenanceService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IWorkflowScheduler _scheduler;
    private readonly ILogger<WorkflowQueueMaintenanceService> _log;

    public WorkflowQueueMaintenanceService(
        IDbContextFactory<MohistDbContext> dbFactory,
        IWorkflowScheduler scheduler,
        ILogger<WorkflowQueueMaintenanceService> log)
    {
        _dbFactory = dbFactory;
        _scheduler = scheduler;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await MaintainOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Workflow queue maintenance failed");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }

    internal async Task MaintainOnceAsync(CancellationToken cancellationToken = default)
    {
        var expired = await _scheduler.ExpireLeasesAsync(cancellationToken);
        if (expired > 0)
            _log.LogWarning("Returned {Count} expired workflow queue leases to queued state", expired);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var variables = await db.WorkflowVariables
            .AsNoTracking()
            .ToDictionaryAsync(row => row.WorkflowRunId, row => row.StateJson, cancellationToken);
        var rows = await db.WorkflowRuns
            .AsNoTracking()
            .Select(row => new { row.WorkflowRunId, row.State, row.MetadataProjectId })
            .ToListAsync(cancellationToken);

        var recovered = 0;
        var runnable = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var run = DeserializeRun(row.State);
            if (run is null || run.Status != WorkflowRunStatus.Running || run.NextWork() is null)
                continue;

            var projectId = ResolveProjectId(row.MetadataProjectId, run, variables.GetValueOrDefault(row.WorkflowRunId));
            if (string.IsNullOrWhiteSpace(projectId))
                continue;

            runnable.Add(row.WorkflowRunId);
            await _scheduler.EnqueueAsync(row.WorkflowRunId, projectId, cancellationToken);
            recovered++;
        }

        var queuedWorkflowIds = await db.WorkflowQueue
            .AsNoTracking()
            .Select(row => row.WorkflowRunId)
            .ToListAsync(cancellationToken);
        foreach (var workflowId in queuedWorkflowIds.Where(id => !runnable.Contains(id)))
            await _scheduler.ClearAsync(workflowId, cancellationToken);

        if (recovered > 0)
            _log.LogDebug("Ensured {Count} runnable workflows are present in workflow queue", recovered);
    }

    private static WorkflowRun? DeserializeRun(string json)
    {
        try { return JsonSerializer.Deserialize<WorkflowRun>(json, JsonOptions); }
        catch { return null; }
    }

    private static string? ResolveProjectId(string? indexedProjectId, WorkflowRun run, string? variablesJson)
    {
        if (!string.IsNullOrWhiteSpace(indexedProjectId)) return indexedProjectId;

        if (run.Metadata.Annotations is not null
            && run.Metadata.Annotations.TryGetValue("projectId", out var annotationProjectId)
            && !string.IsNullOrWhiteSpace(annotationProjectId))
            return annotationProjectId;

        if (string.IsNullOrWhiteSpace(variablesJson)) return null;

        try
        {
            var variables = JsonSerializer.Deserialize<WorkflowExecutionContext>(variablesJson, JsonOptions);
            return variables?.String("project", "id");
        }
        catch
        {
            return null;
        }
    }
}
