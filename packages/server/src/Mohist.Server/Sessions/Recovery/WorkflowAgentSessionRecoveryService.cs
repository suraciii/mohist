using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Storage.Db;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Sessions.Recovery;

public sealed class WorkflowAgentSessionRecoveryService : IHostedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly ILogger<WorkflowAgentSessionRecoveryService> _log;
    private readonly string _workflowType = typeof(WorkflowGrainState).FullName!;

    public WorkflowAgentSessionRecoveryService(
        IDbContextFactory<MohistDbContext> dbFactory,
        ILogger<WorkflowAgentSessionRecoveryService> log)
    {
        _dbFactory = dbFactory;
        _log = log;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var openSessions = await db.WorkflowAgentSessions
            .Where(s => s.Status != "completed" && s.Status != "failed" && s.Status != "cancelled")
            .ToListAsync(cancellationToken);

        if (openSessions.Count == 0) return;

        var workflowRunIds = openSessions
            .Select(s => s.WorkflowRunId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var rows = await db.GrainStates
            .AsNoTracking()
            .Where(row => row.Type == _workflowType && workflowRunIds.Contains(row.Key))
            .Select(row => new { row.Key, row.JsonState })
            .ToListAsync(cancellationToken);

        var failures = rows
            .Select(row => new { row.Key, Failure = TryRestoreFailure(row.JsonState) })
            .Where(row => row.Failure is not null)
            .ToDictionary(row => row.Key, row => row.Failure!, StringComparer.Ordinal);

        var recovered = 0;
        foreach (var session in openSessions)
        {
            if (!failures.TryGetValue(session.WorkflowRunId, out var failure)) continue;
            if (failure.TaskId is not null && failure.TaskId != session.WorkId) continue;

            var domain = session.ToDomain();
            domain.Fail(DateTime.UtcNow, failure.Message ?? "Workflow failed", session.ExitCode ?? 1);
            session.Apply(domain);
            recovered++;
        }

        if (recovered == 0) return;

        await db.SaveChangesAsync(cancellationToken);
        _log.LogInformation("Recovered {Count} stale sessions from terminal workflow state", recovered);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static FailureDetails? TryRestoreFailure(string jsonState)
    {
        try
        {
            var state = JsonSerializer.Deserialize<WorkflowGrainState>(jsonState);
            if (state?.StageDefinitions is null || state.Run is null)
                return null;

            var run = WorkflowRun.Restore(state.StageDefinitions, state.Run);
            return run.Status == WorkflowRunStatus.Failed ? run.Failure : null;
        }
        catch
        {
            return null;
        }
    }
}