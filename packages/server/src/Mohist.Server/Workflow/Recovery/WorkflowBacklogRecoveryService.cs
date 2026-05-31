using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Infrastructure.Persistence.Db;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Workflow.Recovery;

public sealed class WorkflowBacklogRecoveryService : IHostedService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IGrainFactory _grains;
    private readonly ILogger<WorkflowBacklogRecoveryService> _log;

    public WorkflowBacklogRecoveryService(
        IDbContextFactory<MohistDbContext> dbFactory,
        IGrainFactory grains,
        ILogger<WorkflowBacklogRecoveryService> log)
    {
        _dbFactory = dbFactory;
        _grains = grains;
        _log = log;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.WorkflowRuns
            .AsNoTracking()
            .Select(row => new { row.WorkflowRunId, row.State, row.MetadataProjectId })
            .ToListAsync(cancellationToken);

        var recovered = 0;

        foreach (var row in rows)
        {
            if (!TryRestoreRunnableWorkflow(row.State, row.MetadataProjectId, out var hasWork)) continue;
            if (!hasWork) continue;

            var backlog = _grains.GetGrain<IWorkflowBacklogGrain>(WorkflowBacklogKeys.ForProject(row.MetadataProjectId ?? "default"));
            await backlog.RegisterAsync(row.WorkflowRunId);
            recovered++;
        }

        if (recovered > 0)
            _log.LogInformation("Recovered {Count} runnable workflows into backlog", recovered);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static bool TryRestoreRunnableWorkflow(string jsonState, string? entityProjectId, out bool hasWork)
    {
        hasWork = false;

        try
        {
            var run = Deserialize(jsonState);
            if (run is null) return false;

            if (run.StartedAt is null) return false;

            if (IsTerminal(run)) return true;

            if (run.CurrentStageId is null) return false;
            var currentStage = run.Stages.FirstOrDefault(s => s.Id == run.CurrentStageId);
            if (currentStage is null) return false;

            hasWork = HasPendingWork(currentStage);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsTerminal(WorkflowRun run)
    {
        if (run.CurrentStageId is null) return true;
        var currentStage = run.Stages.FirstOrDefault(s => s.Id == run.CurrentStageId);
        if (currentStage is null) return true;

        if (currentStage.Failure is not null && run.Status != WorkflowRunStatus.Paused) return true;

        if (currentStage.Initialized
            && currentStage.Tasks.All(t => t.Status == TaskRunStatus.Completed)
            && currentStage.Checks.All(c => c.Status == StageCheckStatus.Passed)
            && (!currentStage.RequiresApproval || currentStage.ApprovalStatus is { Result: "approved" })
            && currentStage == run.Stages[^1])
            return true;

        return false;
    }

    private static bool HasPendingWork(StageRun stage)
    {
        if (!stage.Initialized) return true;

        return stage.Tasks.Any(t => t.Status is TaskRunStatus.Pending or TaskRunStatus.Running)
            || stage.Checks.Any(c => c.Status is StageCheckStatus.Pending);
    }

    private static WorkflowRun? Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<WorkflowRun>(json, JsonOptions); }
        catch { return null; }
    }
}