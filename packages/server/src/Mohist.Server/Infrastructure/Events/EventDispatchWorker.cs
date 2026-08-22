using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mohist.Server.Infrastructure.Events;

/// <summary>
/// Background dispatch workers. Each loop claims and drains one stream;
/// idle workers sleep on the wake signal with a slow-poll fallback that
/// alone guarantees delivery. WorkerCount zero (test hosts) disables the
/// service entirely — only explicit drains run.
/// </summary>
public sealed class EventDispatchWorker : BackgroundService
{
    private readonly EventDispatcherService _dispatcher;
    private readonly EventDispatchSignal _signal;
    private readonly EventDispatcherOptions _options;
    private readonly ILogger<EventDispatchWorker> _log;

    public EventDispatchWorker(
        EventDispatcherService dispatcher,
        EventDispatchSignal signal,
        IOptions<EventDispatcherOptions> options,
        ILogger<EventDispatchWorker> log)
    {
        _dispatcher = dispatcher;
        _signal = signal;
        _options = options.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.WorkerCount == 0)
            return;

        var workers = Enumerable.Range(0, _options.WorkerCount)
            .Select(i => RunWorkerAsync($"worker-{Environment.MachineName}-{i}-{Guid.NewGuid():N}", stoppingToken))
            .ToArray();
        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    private async Task RunWorkerAsync(string owner, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var worked = await _dispatcher
                    .ClaimAndDrainOneAsync(owner, ct)
                    .ConfigureAwait(false);
                if (worked)
                    continue;
                await _signal.WaitAsync(_options.SlowPollInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Event dispatch worker failed; retrying after slow poll");
                await Task.Delay(_options.SlowPollInterval, ct).ConfigureAwait(false);
            }
        }
    }
}
