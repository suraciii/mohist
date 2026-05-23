using Mohist.Runner.Actions;
using Mohist.Runner.Transport;

namespace Mohist.Runner;

public class RunnerHost
{
    private readonly IServerConnection _connection;
    private readonly IWorkExecutor _executor;
    private readonly ILogger<RunnerHost> _log;
    private readonly TimeProvider _timeProvider;
    private readonly RunnerHostOptions _options;

    public RunnerHost(
        IServerConnection connection,
        IWorkExecutor executor,
        ILogger<RunnerHost> log,
        TimeProvider timeProvider,
        RunnerHostOptions options)
    {
        _connection = connection;
        _executor = executor;
        _log = log;
        _timeProvider = timeProvider;
        _options = options;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _log.LogInformation("Runner connecting to server...");
        await _connection.ConnectAsync(ct);
        _log.LogInformation("Runner connected, polling for work...");

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeatTask = RunHeartbeatAsync(heartbeatCts.Token);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var workItem = await _connection.PollAsync(ct);

                if (workItem is null)
                {
                    await Task.Delay(_options.IdleDelay, _timeProvider, ct);
                    continue;
                }

                _log.LogInformation("Received work: {WorkId} type={WorkType} stage={Stage} uses={Uses}",
                    workItem.WorkId, workItem.WorkType, workItem.Stage, workItem.Uses);

                var result = await _executor.ExecuteAsync(workItem, ct);

                _log.LogInformation("Work {WorkId} completed: {Status}", workItem.WorkId, result.Status);

                await _connection.ReportAsync(workItem, result, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _log.LogInformation("Runner stopping");
        }
        finally
        {
            heartbeatCts.Cancel();
            await StopAsync(heartbeatTask);
        }
    }

    private async Task RunHeartbeatAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.HeartbeatInterval, _timeProvider);
        while (!ct.IsCancellationRequested && await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await _connection.HeartbeatAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Runner heartbeat failed");
            }
        }
    }

    private async Task StopAsync(Task heartbeatTask)
    {
        try
        {
            await heartbeatTask;
        }
        catch (OperationCanceledException)
        {
        }

        using var shutdownCts = new CancellationTokenSource(_options.ShutdownTimeout, _timeProvider);
        await _connection.DisconnectAsync(shutdownCts.Token);
    }
}
