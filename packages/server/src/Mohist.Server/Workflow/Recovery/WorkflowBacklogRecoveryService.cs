using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Storage.Db;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Workflow.Recovery;

public sealed class WorkflowBacklogRecoveryService : IHostedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IGrainFactory _grains;
    private readonly ILogger<WorkflowBacklogRecoveryService> _log;
    private readonly string _workflowType = typeof(WorkflowGrainState).FullName!;

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
        var rows = await db.GrainStates
            .AsNoTracking()
            .Where(row => row.Type == _workflowType)
            .Select(row => new { row.Key, row.JsonState })
            .ToListAsync(cancellationToken);

        var recovered = 0;
        var backlog = _grains.GetGrain<IWorkflowBacklogGrain>(WorkflowBacklogKeys.Key);

        foreach (var row in rows)
        {
            if (!TryRestoreRunnableWorkflow(row.JsonState, out var hasWork)) continue;
            if (!hasWork) continue;

            await backlog.RegisterAsync(row.Key);
            recovered++;
        }

        if (recovered > 0)
            _log.LogInformation("Recovered {Count} runnable workflows into backlog", recovered);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static bool TryRestoreRunnableWorkflow(string jsonState, out bool hasWork)
    {
        hasWork = false;

        try
        {
            var state = JsonSerializer.Deserialize<WorkflowGrainState>(jsonState);
            if (state?.StageDefinitions is null || state.Run is null || state.Lease is not null)
                return false;

            var run = WorkflowRun.Restore(state.StageDefinitions, state.Run);
            if (run.Status != WorkflowRunStatus.Running)
                return true;

            hasWork = run.GetNextWork() is not null;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
