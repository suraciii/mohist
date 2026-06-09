using CloudNative.CloudEvents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Infrastructure.Events;

/// <summary>
/// In-process outbox relay. Reads <c>Events</c> rows in id order and
/// emits them to the in-process bus, awaiting every handler.
///
/// <para>
/// <b>Why this exists</b>. The bus is synchronous: <c>Emit</c>
/// awaits every typed handler. Calling <c>Emit</c> from inside a
/// grain deadlocks the grain — the handler is typically
/// <c>await issueGrain.AbortWorkAsync(...)</c>, which is queued on
/// the same activation the caller is occupying, and the caller is
/// blocking on the bus waiting for the handler. Out of grain
/// contexts (web / SignalR / hosted service) the bus is safe.
///
/// <para>
/// Rather than <c>await</c> an out-of-band dispatch path on every
/// save, grain code persists the event in the same transaction
/// that updates the workflow state, returns, and the relay —
/// which is a hosted service running on a non-grain thread —
/// picks the row up and emits it. The bus then runs handlers
/// safely, with the caller's activation long since gone.
/// </para>
///
/// <para>
/// <b>Why a process-lifetime watermark</b>. The relay remembers
/// the highest <c>Id</c> it has dispatched; rows above that line
/// are picked up on the next poll. The watermark is in-memory only,
/// so on process restart the relay re-dispatches every row from
/// id 1. Handlers are expected to be idempotent on the dispatch
/// path: <c>IssueWorkflowCompletionHandler</c> and
/// <c>IssueWorkflowAbortedHandler</c> gate on the issue state
/// (<c>_activeWorkflowRunId</c> match) before mutating, so a
/// re-dispatch is a no-op once the issue has already moved on.
/// </para>
/// </summary>
public sealed class OutboxRelayService : BackgroundService
{
    public static TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    public const int MaxBatchSize = 200;

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<OutboxRelayService> _log;
    private long _lastDispatchedId = 0;

    public OutboxRelayService(IServiceScopeFactory scopes, ILogger<OutboxRelayService> log)
    {
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var dispatched = await DispatchPendingAsync(stoppingToken);
                if (dispatched == 0)
                {
                    await Task.Delay(PollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "OutboxRelayService poll failed");
                await Task.Delay(PollInterval, stoppingToken);
            }
        }
    }

    private async Task<int> DispatchPendingAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        var bus = scope.ServiceProvider.GetRequiredService<IEventBus>();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var pending = await db.Events
            .Where(e => e.Id > _lastDispatchedId)
            .OrderBy(e => e.Id)
            .Take(MaxBatchSize)
            .ToListAsync(ct);

        if (pending.Count == 0) return 0;

        var count = 0;
        foreach (var row in pending)
        {
            try
            {
                if (row.WorkflowEvent is null) continue;
                var we = (WorkflowEvent)row.WorkflowEvent;
                var busType = WorkflowEventSerializer.BusType(we);
                if (busType is null) continue;

                var evt = CloudEventFactory.Create(
                    type: busType,
                    source: new Uri($"about:blank", UriKind.Absolute),
                    data: row.Data,
                    workflowRunId: ExtractRunIdFromSource(row.Source));
                await bus.EmitAsync(evt, ct);
                _lastDispatchedId = row.Id;
                count++;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "OutboxRelayService failed to dispatch event id={Id}", row.Id);
            }
        }

        return count;
    }

    private static string? ExtractRunIdFromSource(string source)
    {
        if (source.StartsWith("/workflow-runs/", StringComparison.Ordinal))
        {
            return source["/workflow-runs/".Length..];
        }
        return null;
    }
}
