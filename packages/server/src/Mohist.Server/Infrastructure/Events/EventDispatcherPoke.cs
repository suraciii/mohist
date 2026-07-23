using Microsoft.Extensions.Logging;
using Mohist.Server.Events.Grains;
using Mohist.Server.Otel;
using Orleans;

namespace Mohist.Server.Infrastructure.Events;

/// <summary>
/// Shared best-effort poke helper used by every event producer (workflow run,
/// issue, agent session). Producers call this after their state transaction
/// commits so the dispatcher wakes up immediately instead of waiting up to
/// <c>EventDispatcherOptions.ReminderPeriod</c> for the next reminder tick.
/// Correctness never depends on the poke — if it is lost, the next reminder
/// tick catches up.
///
/// <para>
/// The poke is fire-and-forget: any exception thrown by the dispatch cycle
/// (e.g., the event store is unreachable, the dispatcher grain's constructor
/// pre-conditions failed) is captured via <see cref="Task.ContinueWith{TAntecedentResult,TResult}(Func{Task,object},System.Threading.CancellationToken)"/>
/// and logged so it never becomes an unobserved task exception on the silo.
/// </para>
/// </summary>
public static class EventDispatcherPoke
{
    public static void PokeAfterCommit(
        IGrainFactory grainFactory,
        ILogger log,
        string storeName,
        IBackgroundTaskLauncher launcher)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        try
        {
            launcher.Launch(async _ =>
            {
                try
                {
                    var dispatcher = grainFactory
                        .GetGrain<IEventDispatcherGrain>(EventDispatcherGrain.Global);
                    await dispatcher.DispatchNowAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    log.LogDebug(ex,
                        "{StoreName} immediate-trigger poke to EventDispatcherGrain failed; reminder tick will recover",
                        storeName);
                }
            });
        }
        catch (Exception ex)
        {
            log.LogDebug(ex,
                "{StoreName} immediate-trigger poke to EventDispatcherGrain failed; reminder tick will recover",
                storeName);
        }
    }
}
